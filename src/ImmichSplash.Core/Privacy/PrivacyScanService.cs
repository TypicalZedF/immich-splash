using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Services;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Storage;

namespace ImmichSplash.Core.Privacy;

public enum PrivacyScanState { Off, Unavailable, Starting, Scanning, Paused, Done, Error }

/// <summary>Where the privacy check is, for the Utilities page.</summary>
/// <param name="Remaining">Estimated time to finish, from the recent pace (null until there's enough to go on).</param>
/// <param name="OneTime">A one-time check (the setting is off): it stops once the library is done.</param>
public sealed record PrivacyScanStatus(PrivacyScanState State, int Checked, int Total, int Suggestions, bool OnGpu, string? Detail = null,
    TimeSpan? Remaining = null, bool OneTime = false)
{
    public static PrivacyScanStatus Off { get; } = new(PrivacyScanState.Off, 0, 0, 0, false);
}

/// <summary>
/// Looks through your whole Immich library, on this PC, for pictures you may want in the locked
/// folder: nudity, payment cards, IDs, bank papers, passwords, medical records. It downloads each
/// photo's preview from your own server, checks it with the bundled models, and keeps only the
/// result (what kind of thing was found) in the local database. Nothing is sent anywhere and
/// nothing is moved without you. Pauses on battery, on metered connections and (if chosen)
/// while you're using the PC; picks up where it left off.
/// </summary>
public sealed class PrivacyScanService : IDisposable
{
    /// <summary>Bump when the checks change enough that everything should be looked at again.</summary>
    /// <remarks>2: NudeNet replaced NSFWJS. Results of the thorough check are saved as the next version, so
    /// turning it on checks everything again, and turning it off keeps its (better) results.</remarks>
    public const int ChecksVersion = 2;

    // NudeNet: how sure it must be that it found an exposed part (or underwear/swimwear).
    const float ExplicitThreshold = 0.45f;
    const float RevealingThreshold = 0.6f;
    // Thorough check (LAION's detector over CLIP): "NSFW" covers nudity and revealing pictures alike, so
    // only a very sure score counts as nudity on its own.
    const float ClipExplicitThreshold = 0.9f;
    const float ClipRevealingThreshold = 0.6f;
    // Videos checked before this only had their preview looked at; the small thumbnail often scores higher.
    static readonly DateTime VideoRecheckBefore = new(2026, 10, 26, 5, 0, 0, DateTimeKind.Utc);
    static readonly TimeSpan IdleNeeded = TimeSpan.FromMinutes(2);
    static readonly TimeSpan RecheckLibraryEvery = TimeSpan.FromHours(1);

    readonly SettingsStore _settings;
    readonly StateDatabase _db;
    readonly AccountService _account;
    readonly Func<bool> _isMetered;
    readonly Func<byte[], BgraImage?> _decode;
    readonly Lock _gate = new();
    CancellationTokenSource? _run;
    Task? _task;
    bool? _runningWithGpu;
    bool? _runningThorough;
    bool _once;        // a one-time check was asked for
    bool _wasEnabled;  // the setting, last time it was applied

    public PrivacyScanService(SettingsStore settings, StateDatabase db, AccountService account, Func<bool> isMetered,
        Func<byte[], BgraImage?> decode)
    {
        _settings = settings;
        _db = db;
        _account = account;
        _isMetered = isMetered;
        _decode = decode;
        _settings.Changed += _ => Apply();
        _account.SessionChanged += Apply;
        Apply();
    }

    public PrivacyScanStatus Status { get; private set; } = PrivacyScanStatus.Off;

    /// <summary>Raised from a background thread.</summary>
    public event Action<PrivacyScanStatus>? StatusChanged;

    /// <summary>Flagged pictures waiting for you to decide.</summary>
    public int Suggestions => _db.CountPrivacySuggestions();

    /// <summary>Starts or stops to match the settings and sign-in.</summary>
    void Apply()
    {
        var s = _settings.Current;
        lock (_gate)
        {
            // Turning the setting off also stops a one-time check that was running alongside it.
            if (_wasEnabled && !s.PrivacyScanEnabled)
                _once = false;
            _wasEnabled = s.PrivacyScanEnabled;
            var wanted = (s.PrivacyScanEnabled || _once) && _account.IsSignedIn;
            var thorough = s.PrivacyScanThorough && ClipNsfwClassifier.Download.IsDownloaded;
            if (wanted && _task is { IsCompleted: false } && _runningWithGpu == s.PrivacyScanUseGpu && _runningThorough == thorough)
                return;
            _run?.Cancel();
            _run = null;
            if (!wanted)
            {
                // A finished one-time check keeps saying so (until it's started again or turned on).
                if (Status is not { State: PrivacyScanState.Done, OneTime: true })
                    Report(PrivacyScanStatus.Off with { Suggestions = Suggestions });
                return;
            }
            if (!OnDeviceModels.Available)
            {
                Report(new PrivacyScanStatus(PrivacyScanState.Unavailable, 0, 0, 0, false, Loc.T("The privacy check's files are missing. Reinstall the app.")));
                return;
            }
            var cts = _run = new CancellationTokenSource();
            var previous = _task;
            _runningWithGpu = s.PrivacyScanUseGpu;
            _runningThorough = thorough;
            _task = Task.Run(async () =>
            {
                if (previous is not null)
                    await previous.ContinueWith(_ => { }, TaskScheduler.Default); // let the last run let go of the models
                await RunAsync(s.PrivacyScanUseGpu, thorough, cts.Token);
            });
        }
    }

    async Task RunAsync(bool useGpu, bool thorough, CancellationToken ct)
    {
        ReportRun(new PrivacyScanStatus(PrivacyScanState.Starting, 0, 0, Suggestions, false), ct);
        var version = thorough ? ChecksVersion + 1 : ChecksVersion;
        Checks? checks = null;
        try
        {
            checks = new Checks(useGpu, thorough);
            var onGpu = checks.OnGpu;
            while (!ct.IsCancellationRequested)
            {
                if (_account.Client is not { } client)
                    return;
                // Not even the photo list is fetched until it's allowed to run (e.g. on a home network).
                await WaitUntilAllowedAsync(Status.Checked, Status.Total, onGpu, ct);
                var (library, videos) = await ListLibraryAsync(client, ct);
                // Videos saved as "nothing found" by older builds, which stored a failed preview that way: look again.
                var forgotten = await Task.Run(() => _db.ForgetCleanScans(videos, VideoRecheckBefore), ct);
                if (forgotten > 0)
                    Log.Info($"Privacy check: looking at {forgotten} videos again");
                var done = await Task.Run(() => _db.GetPrivacyScanned(version), ct);
                var todo = library.Where(id => !done.Contains(id)).ToList();
                var checkedCount = library.Count - todo.Count;
                var lastReport = DateTime.MinValue;
                // Pace over the last photos (not since the start: pauses and the first slow model load would skew it).
                var recent = new Queue<DateTime>();

                // Fetch the next preview while the current one is being checked.
                // (Only ever one ahead, and only while it's allowed to run.)
                Task<byte[]?>? next = null;
                for (var i = 0; i < todo.Count; i++)
                {
                    if (await WaitUntilAllowedAsync(checkedCount, library.Count, onGpu, ct))
                        recent.Clear(); // a pause isn't part of the pace
                    var data = await (next ?? FetchAsync(client, todo[i], AssetImageSize.Preview, ct));
                    next = i + 1 < todo.Count && PauseReason() is null ? FetchAsync(client, todo[i + 1], AssetImageSize.Preview, ct) : null;
                    var image = data is null ? null : _decode(data);
                    var found = SensitiveKind.None;
                    double best = 0;
                    var looked = false;
                    if (image is not null)
                    {
                        (found, best) = Check(checks, image);
                        looked = true;
                    }
                    if (videos.Contains(todo[i]))
                    {
                        // The small thumbnail is a different picture to the model than the big preview, and often
                        // scores higher: keep whichever sees more.
                        var small = await FetchAsync(client, todo[i], AssetImageSize.Thumbnail, ct);
                        if (small is not null && _decode(small) is { } smallImage)
                        {
                            var (kinds2, score2) = Check(checks, smallImage);
                            found |= kinds2;
                            best = Math.Max(best, score2);
                            looked = true;
                        }
                    }
                    if (looked)
                    {
                        _db.SavePrivacyScan(todo[i], version, (int)found, best);
                    }
                    else
                    {
                        // Not saved as checked, so a later pass tries again (a failed download or decode must not
                        // look like "nothing found").
                        Log.Warn($"Privacy check skipped {(videos.Contains(todo[i]) ? "video" : "photo")} {todo[i]}: " +
                            (data is null ? "no preview from the server" : "the preview could not be decoded"));
                    }
                    checkedCount++;
                    recent.Enqueue(DateTime.UtcNow);
                    while (recent.Count > 50)
                        recent.Dequeue();
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(0.5) || i == todo.Count - 1)
                    {
                        lastReport = DateTime.UtcNow;
                        TimeSpan? remaining = recent.Count >= 5
                            ? (recent.Last() - recent.Peek()) / (recent.Count - 1) * (library.Count - checkedCount)
                            : null;
                        ReportRun(new PrivacyScanStatus(PrivacyScanState.Scanning, checkedCount, library.Count, Suggestions, onGpu, Remaining: remaining), ct);
                    }
                }
                ReportRun(new PrivacyScanStatus(PrivacyScanState.Done, library.Count, library.Count, Suggestions, onGpu), ct);
                lock (_gate)
                {
                    if (!_settings.Current.PrivacyScanEnabled)
                    {
                        _once = false; // a one-time check: done, so stop (new photos wait for the next one)
                        return;
                    }
                }
                await Task.Delay(RecheckLibraryEvery, ct); // then look for new photos
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("The privacy check stopped", ex);
            ReportRun(new PrivacyScanStatus(PrivacyScanState.Error, Status.Checked, Status.Total, Suggestions, false, NetworkErrors.Describe(ex)), ct);
            // Try again in a while (e.g. the server was unreachable).
            _ = RetryLaterAsync(ct);
        }
        finally
        {
            checks?.Dispose();
        }
    }

    async Task RetryLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), ct);
            await (_task ?? Task.CompletedTask); // this run has finished by now
            Apply();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The loaded models for one run.</summary>
    public sealed class Checks(bool useGpu, bool thorough) : IDisposable
    {
        public NudeNetDetector Nudity { get; } = new(useGpu);
        public ClipNsfwClassifier? Thorough { get; } = thorough ? new ClipNsfwClassifier(useGpu) : null;
        public PhotoTextReader Text { get; } = new(useGpu);
        public bool OnGpu => Nudity.OnGpu;

        public void Dispose()
        {
            Nudity.Dispose();
            Thorough?.Dispose();
            Text.Dispose();
        }
    }

    /// <summary>What a picture shows, and how sure we are (0–1, for sorting suggestions).</summary>
    public static (SensitiveKind Kinds, double Score) Check(Checks checks, BgraImage image)
    {
        var kinds = SensitiveKind.None;
        double score = 0;
        var nudity = checks.Nudity.Detect(image);
        var clip = checks.Thorough?.Classify(image) ?? 0;
        var explicitScore = Math.Max(nudity.Explicit >= ExplicitThreshold ? nudity.Explicit : 0, clip >= ClipExplicitThreshold ? clip : 0);
        var revealingScore = Math.Max(nudity.Revealing >= RevealingThreshold ? nudity.Revealing : 0, clip >= ClipRevealingThreshold ? clip : 0);
        if (explicitScore > 0)
        {
            kinds |= SensitiveKind.Explicit;
            score = explicitScore;
        }
        else if (revealingScore > 0)
        {
            kinds |= SensitiveKind.Suggestive;
            score = revealingScore * 0.7;
        }
        var text = SensitiveText.Find(checks.Text.Read(image).Select(l => l.Text));
        if (text != SensitiveKind.None)
        {
            kinds |= text;
            score = Math.Max(score, text.HasFlag(SensitiveKind.PaymentCard) || text.HasFlag(SensitiveKind.IdDocument) ? 0.95 : 0.8);
        }
        return (kinds, score);
    }

    /// <summary>Every photo and video in the timeline and the archive (not the locked folder or the trash).</summary>
    static async Task<(List<string> Ids, HashSet<string> Videos)> ListLibraryAsync(ImmichClient client, CancellationToken ct)
    {
        var ids = new List<string>();
        var videos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filter in new[] { TimelineFilter.All, new TimelineFilter(Visibility: "archive") })
        {
            foreach (var bucket in await client.GetTimeBucketsAsync(filter, ct))
            {
                if (bucket.Count == 0)
                    continue;
                foreach (var asset in await client.GetTimeBucketAsync(bucket.Key, filter, ct))
                {
                    ids.Add(asset.Id);
                    if (!asset.IsImage)
                        videos.Add(asset.Id);
                }
            }
        }
        return (ids.Distinct(StringComparer.Ordinal).ToList(), videos);
    }

    static async Task<byte[]?> FetchAsync(ImmichClient client, string assetId, AssetImageSize size, CancellationToken ct)
    {
        try
        {
            return await client.GetAssetImageAsync(assetId, size, ct);
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            return null; // deleted meanwhile, or no preview
        }
    }

    /// <summary>Waits while it isn't allowed to run; true if it had to wait.</summary>
    async Task<bool> WaitUntilAllowedAsync(int checkedCount, int total, bool onGpu, CancellationToken ct)
    {
        var waited = false;
        while (true)
        {
            var reason = PauseReason();
            if (reason is null)
                return waited;
            waited = true;
            if (Status.State != PrivacyScanState.Paused || Status.Detail != reason)
                ReportRun(new PrivacyScanStatus(PrivacyScanState.Paused, checkedCount, total, Suggestions, onGpu, reason), ct);
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    string? PauseReason()
    {
        var s = _settings.Current;
        if (PowerStatus.IsOnBattery())
            return Loc.T("Paused while on battery");
        if (_isMetered())
            return Loc.T("Paused on a metered connection");
        if (s.PrivacyScanOnlyWhenIdle && UserIdle.Duration() is { } idle && idle < IdleNeeded)
            return Loc.T("Waiting until the PC isn't being used");
        return null;
    }

    /// <summary>
    /// Checks the library once, with the setting off: it stops when it's done instead of watching
    /// for new photos. The same rules apply (home network only, pauses on battery and so on).
    /// </summary>
    public void ScanOnce()
    {
        lock (_gate)
            _once = true;
        Apply();
    }

    /// <summary>Picks up a model that was just downloaded (or removed).</summary>
    public void ModelsChanged() => Apply();

    /// <summary>Stops a one-time check.</summary>
    public void StopOnce()
    {
        lock (_gate)
            _once = false;
        Apply();
    }

    /// <summary>Starts over (e.g. after its results were forgotten).</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _run?.Cancel();
            _run = null;
            _runningWithGpu = null;
            if (Status.OneTime)
                Report(PrivacyScanStatus.Off with { Suggestions = Suggestions }); // its results are gone
        }
        Apply();
    }

    /// <summary>The suggestions were acted on (moved or dismissed): refresh the count.</summary>
    public void Refresh() => Report(Status with { Suggestions = Suggestions });

    /// <summary>
    /// A status from a run. Dropped once that run was stopped: a photo finishing after you turned
    /// the check off must not put "Checking…" back on the page.
    /// </summary>
    void ReportRun(PrivacyScanStatus status, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!ct.IsCancellationRequested)
                Report(status with { OneTime = _once && !_settings.Current.PrivacyScanEnabled });
        }
    }

    void Report(PrivacyScanStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _run?.Cancel();
            _run = null;
        }
    }
}
