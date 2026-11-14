using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Privacy;

namespace ImmichSplash.App.ViewModels;

/// <summary>
/// Utilities → Suggest for the locked folder: the on-device privacy check's settings, progress, and
/// the pictures it flagged, to move to the locked folder, delete, or mark as fine. Pictures are blurred until
/// you choose to see them.
/// </summary>
public sealed partial class PrivacyReviewViewModel : ViewModelBase, IDisposable
{
    const int ThumbnailWidth = 220;

    readonly PhotosViewModel _owner;
    readonly CancellationTokenSource _lifetime = new();
    int _shownSuggestions = -1;
    bool _loading;   // applying saved settings
    bool _reloading; // reading suggestions

    public PrivacyReviewViewModel(PhotosViewModel owner)
    {
        _owner = owner;
        SelectedSort = SortChoices[0];
        var s = AppServices.Settings.Current;
        _loading = true;
        IsEnabled = s.PrivacyScanEnabled;
        UseGpu = s.PrivacyScanUseGpu;
        OnlyWhenIdle = s.PrivacyScanOnlyWhenIdle;
        Thorough = s.PrivacyScanThorough && ClipNsfwClassifier.Download.IsDownloaded;
        _loading = false;
        UpdateThoroughText();
        AppServices.PrivacyScan.StatusChanged += OnStatus;
        Apply(AppServices.PrivacyScan.Status);
        _ = LoadAsync();
        _ = LoadModelsAsync();
    }

    // ---- How it works (shown on the page, so it's clear what the check does and can see) ----

    [ObservableProperty]
    public partial bool ShowDetails { get; set; }

    public string ServerHost => Uri.TryCreate(AppServices.Settings.Current.ServerUrl, UriKind.Absolute, out var uri) ? uri.Host : Tr.T("your Immich server");
    public string DatabasePath => AppPaths.DatabaseFile;
    public string ModelsFolder => OnDeviceModels.Directory;
    /// <summary>The check's code at the exact commit this copy was built from.</summary>
    public string SourceUrl => AppInfo.SourceUrl("src/ImmichSplash.Core/Privacy");

    [ObservableProperty]
    public partial string RunsOn { get; set; } = "";

    [ObservableProperty]
    public partial string TelemetryText { get; set; } = "";

    public ObservableCollection<PrivacyModelViewModel> Models { get; } = [];

    async Task LoadModelsAsync()
    {
        try
        {
            var (files, telemetryOff) = await Task.Run(() => (OnDeviceModels.Files(), OnDeviceModels.TelemetryDisabled));
            TelemetryText = telemetryOff
                ? Tr.T("One detail: the engine that runs the models (ONNX Runtime, made by Microsoft) has a built-in option to report usage statistics to Microsoft through Windows' diagnostic settings. Never your photos or results, only things like which version is running. This app switches that option off before the check starts, so not even that is sent.")
                : Tr.T("Couldn't confirm that ONNX Runtime's usage statistics are switched off; see the log.");
            string Describe(string file) => file switch
            {
                NudeNetDetector.ModelFile => Tr.T("Nudity detector. NudeNet 320n by notAI-tech (based on YOLOv8), AGPL-3.0 license. Looks for exposed body parts in the picture shrunk to 320×320, so bare arms, legs or faces don't count. Flagged as Nudity when it's at least 45% sure, Revealing (underwear, swimwear) at 60%."),
                ClipNsfwClassifier.HeadFile => Tr.T("Thorough check, part 2. LAION's CLIP-based NSFW detector, MIT license. Scores CLIP's description of the picture: Nudity at 90% or more, Revealing at 60%. Only used with the thorough check on."),
                ClipNsfwClassifier.ModelFile => Tr.T("Thorough check, part 1 (downloaded). CLIP ViT-B/32 by OpenAI, MIT license, the copy Immich's own smart search uses. Describes the whole picture, so it understands drawings, art and game scenes."),
                PhotoTextReader.DetectModelFile => Tr.T("Text finder. PaddleOCR PP-OCRv4 detection by PaddlePaddle (via RapidOCR), Apache 2.0 license. Marks where lines of text are."),
                PhotoTextReader.ReadModelFile => Tr.T("Text reader. PaddleOCR PP-OCRv4 recognition, Apache 2.0 license. Reads each line. The text is only checked against the rules below, then thrown away."),
                _ => file,
            };
            Models.Clear();
            foreach (var f in files)
                Models.Add(new PrivacyModelViewModel(f.File, Describe(f.File), f.Bytes > 0 ? Format.Bytes(f.Bytes) : Tr.T("missing"), f.Sha256));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not describe the privacy models", ex);
        }
    }

    [RelayCommand]
    void ToggleDetails() => ShowDetails = !ShowDetails;

    [RelayCommand]
    void OpenModelsFolder() => Core.Platform.ShellLauncher.Open(ModelsFolder);

    [RelayCommand]
    void OpenSource() => Core.Platform.ShellLauncher.Open(SourceUrl);

    /// <summary>Deletes every stored result (and your "not sensitive" choices); the check starts over if it's on.</summary>
    [RelayCommand]
    async Task ForgetResultsAsync()
    {
        if (!await _owner.ConfirmAsync(Tr.T("Forget all results?"),
                Tr.T("Deletes what the check found and your \"not sensitive\" choices from this PC. Your photos aren't touched. If the check is on, it starts over."),
                Tr.T("Forget results"), danger: true))
            return;
        AppServices.Database.ClearPrivacyScans();
        foreach (var item in _all)
            item.Dispose();
        _all.Clear();
        BuildKindFilters();
        ApplyFilter();
        AppServices.PrivacyScan.Restart();
        _owner.ShowNotice(Tr.T("Results forgotten"));
    }

    /// <summary>The suggestions shown (after the type and confidence filters).</summary>
    public ObservableCollection<PrivacySuggestionViewModel> Items { get; } = [];

    /// <summary>Every suggestion waiting for a decision.</summary>
    readonly List<PrivacySuggestionViewModel> _all = [];

    // ---- Filters: by kind (several can be on; none = all) and by how sure the check is ----

    public ObservableCollection<PrivacyKindFilter> KindFilters { get; } = [];

    /// <summary>Only show suggestions at least this sure (0–100).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinConfidenceText))]
    public partial double MinConfidence { get; set; }

    public string MinConfidenceText => MinConfidence <= 0 ? Tr.T("Any") : Tr.T("{0}% or more", ((int)MinConfidence).ToString(System.Globalization.CultureInfo.CurrentCulture));

    // Dragging the slider changes the value many times a second; the tiles catch up once input and
    // drawing have (one update for however many steps went by), always ending on the final value.
    bool _filterQueued;

    partial void OnMinConfidenceChanged(double value)
    {
        if (_filterQueued)
            return;
        _filterQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _filterQueued = false;
            ApplyFilter();
        }, DispatcherPriority.Background);
    }

    [ObservableProperty]
    public partial string FilterSummary { get; set; } = "";

    // ---- Album filter and sort order ----

    /// <summary>"All albums", "Not in an album", then each album that holds a suggestion (with how many).</summary>
    public ObservableCollection<PrivacyAlbumChoice> AlbumChoices { get; } = [];

    [ObservableProperty]
    public partial PrivacyAlbumChoice? SelectedAlbum { get; set; }

    partial void OnSelectedAlbumChanged(PrivacyAlbumChoice? value)
    {
        if (!_rebuildingAlbums)
            ApplyFilter();
    }

    public bool HasAlbumChoices => AlbumChoices.Count > 2;

    public IReadOnlyList<PrivacySortChoice> SortChoices { get; } =
    [
        new(PrivacySort.MostSure, Tr.T("Most sure first")),
        new(PrivacySort.Newest, Tr.T("Newest first")),
        new(PrivacySort.Oldest, Tr.T("Oldest first")),
        new(PrivacySort.Album, Tr.T("By album")),
    ];

    [ObservableProperty]
    public partial PrivacySortChoice? SelectedSort { get; set; }

    partial void OnSelectedSortChanged(PrivacySortChoice? value) => ApplyFilter();

    /// <summary>Album names by photo, read from the server once per visit (the check itself never looks at albums).</summary>
    Dictionary<string, List<string>>? _albumsByAsset;
    bool _rebuildingAlbums;

    async Task LoadAlbumsAndDatesAsync(IReadOnlyList<PrivacySuggestionViewModel> items)
    {
        if (_owner.Client is not { } client || items.Count == 0)
            return;
        var ct = _lifetime.Token;
        try
        {
            if (_albumsByAsset is null)
            {
                var map = new Dictionary<string, List<string>>();
                var albums = await client.GetAlbumsAsync(ct);
                using var gate = new SemaphoreSlim(4);
                await Task.WhenAll(albums.Where(a => a.AssetCount > 0).Select(async album =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var assets = await client.GetAllTimelineAssetsAsync(new TimelineFilter(AlbumId: album.Id), ct);
                        lock (map)
                            foreach (var asset in assets)
                                (map.TryGetValue(asset.Id, out var names) ? names : map[asset.Id] = []).Add(album.AlbumName);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Info($"Album {album.Id} skipped for the privacy filters: {ex.Message}");
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
                _albumsByAsset = map;
            }
            foreach (var item in _all)
                item.SetAlbums(_albumsByAsset.TryGetValue(item.Id, out var names) ? names : []);
            BuildAlbumChoices();
            ApplyFilter();

            // Dates, a few at a time, for the date sort.
            using var dates = new SemaphoreSlim(6);
            await Task.WhenAll(items.Where(i => i.Taken is null).Select(async item =>
            {
                await dates.WaitAsync(ct);
                try
                {
                    var details = await client.GetAssetAsync(item.Id, ct);
                    if (DateTimeOffset.TryParse(details.FileCreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal, out var taken))
                        item.Taken = taken;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Info($"Date unavailable for {item.Id}: {ex.Message}");
                }
                finally
                {
                    dates.Release();
                }
            }));
            if (SelectedSort?.Sort is PrivacySort.Newest or PrivacySort.Oldest)
                ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Albums for the privacy filters unavailable: {ex.Message}");
        }
    }

    void BuildAlbumChoices()
    {
        var selected = SelectedAlbum;
        _rebuildingAlbums = true;
        AlbumChoices.Clear();
        AlbumChoices.Add(new PrivacyAlbumChoice(null, Tr.T("All albums")));
        AlbumChoices.Add(new PrivacyAlbumChoice("", Tr.T("Not in an album ({0})", _all.Count(i => i.AlbumNames.Count == 0).ToString("N0"))));
        foreach (var group in _all.SelectMany(i => i.AlbumNames).GroupBy(n => n).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            AlbumChoices.Add(new PrivacyAlbumChoice(group.Key, $"{group.Key} ({group.Count():N0})"));
        // The chosen album stays chosen while it still has suggestions; otherwise back to all of them.
        var stillThere = selected?.Name is not { } name
            || (name.Length == 0 ? _all.Any(i => i.AlbumNames.Count == 0) : _all.Any(i => i.AlbumNames.Contains(name)));
        SelectedAlbum = (stillThere ? AlbumChoices.FirstOrDefault(c => c.Name == selected?.Name) : null) ?? AlbumChoices[0];
        _rebuildingAlbums = false;
        OnPropertyChanged(nameof(HasAlbumChoices));
    }

    internal void ApplyFilter()
    {
        var kinds = KindFilters.Where(f => f.IsOn).Aggregate(SensitiveKind.None, (all, f) => all | f.Kind);
        var album = SelectedAlbum?.Name;
        IEnumerable<PrivacySuggestionViewModel> filtered = _all.Where(i => (kinds == SensitiveKind.None || (i.Kinds & kinds) != 0)
            && i.Score * 100 >= MinConfidence - 0.001
            && (album is null || (album.Length == 0 ? i.AlbumNames.Count == 0 : i.AlbumNames.Contains(album))));
        filtered = (SelectedSort?.Sort ?? PrivacySort.MostSure) switch
        {
            PrivacySort.Newest => filtered.OrderByDescending(i => i.Taken ?? DateTimeOffset.MinValue),
            PrivacySort.Oldest => filtered.OrderBy(i => i.Taken ?? DateTimeOffset.MaxValue),
            PrivacySort.Album => filtered.OrderBy(i => i.AlbumNames.Count == 0 ? 1 : 0)
                .ThenBy(i => i.AlbumNames.FirstOrDefault() ?? "", StringComparer.CurrentCultureIgnoreCase),
            _ => filtered, // already most sure first
        };
        var shown = filtered.ToList();
        var visible = shown.ToHashSet();
        foreach (var hidden in Items.Where(i => i.IsSelected && !visible.Contains(i)))
            hidden.IsSelected = false; // actions only ever apply to what you can see
        Sync(shown, visible);
        Count = Items.Count;
        FilterSummary = Items.Count == _all.Count ? "" : Tr.T("{0} of {1} shown", Items.Count.ToString("N0"), _all.Count.ToString("N0"));
        UpdateSelection();
    }

    /// <summary>
    /// Makes <see cref="Items"/> match <paramref name="shown"/> (which keeps the order of <c>_all</c>) by
    /// removing and inserting only what changed, so tiles that stay aren't rebuilt.
    /// </summary>
    void Sync(List<PrivacySuggestionViewModel> shown, HashSet<PrivacySuggestionViewModel> visible)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!visible.Contains(Items[i]))
                Items.RemoveAt(i);
        for (var i = 0; i < shown.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], shown[i]))
                continue;
            var at = Items.IndexOf(shown[i]);
            if (at < 0)
                Items.Insert(i, shown[i]);
            else
                Items.Move(at, i);
        }
    }

    /// <summary>One chip per kind that's actually among the suggestions, with how many.</summary>
    void BuildKindFilters()
    {
        var on = KindFilters.Where(f => f.IsOn).Select(f => f.Kind).ToHashSet();
        KindFilters.Clear();
        foreach (var kind in Enum.GetValues<SensitiveKind>().Where(k => k != SensitiveKind.None))
        {
            var count = _all.Count(i => i.Kinds.HasFlag(kind));
            if (count > 0)
                KindFilters.Add(new PrivacyKindFilter(this, kind, PrivacySuggestionViewModel.Reasons(kind).First(), count, on.Contains(kind)));
        }
        OnPropertyChanged(nameof(HasKindFilters));
        OnPropertyChanged(nameof(ShowConfidenceFilter));
    }

    public bool HasKindFilters => KindFilters.Count > 1;

    /// <summary>The confidence filter shows once there's something to filter (or it's in use).</summary>
    public bool ShowConfidenceFilter => _all.Count > 1 || MinConfidence > 0 || SelectedAlbum?.Name is not null;

    [RelayCommand]
    void ClearFilters()
    {
        foreach (var f in KindFilters)
            f.SetQuietly(false);
        MinConfidence = 0;
        _rebuildingAlbums = true;
        SelectedAlbum = AlbumChoices.FirstOrDefault();
        _rebuildingAlbums = false;
        ApplyFilter();
    }

    // ---- Settings ----

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool UseGpu { get; set; }

    [ObservableProperty]
    public partial bool OnlyWhenIdle { get; set; }

    public bool OffersGpu => OperatingSystem.IsWindows();

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.PrivacyScanEnabled = value);
    }

    partial void OnUseGpuChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.PrivacyScanUseGpu = value);
    }

    partial void OnOnlyWhenIdleChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.PrivacyScanOnlyWhenIdle = value);
    }

    // ---- Thorough check: CLIP, downloaded once when turned on ----

    CancellationTokenSource? _download;

    [ObservableProperty]
    public partial bool Thorough { get; set; }

    [ObservableProperty]
    public partial string ThoroughText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveThorough))]
    public partial bool IsDownloadingThorough { get; set; }

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    public bool CanRemoveThorough => !Thorough && !IsDownloadingThorough && ClipNsfwClassifier.Download.IsDownloaded;

    partial void OnThoroughChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemoveThorough));
        if (_loading)
            return;
        if (value && !ClipNsfwClassifier.Download.IsDownloaded)
        {
            _ = DownloadThoroughAsync();
            return;
        }
        AppServices.Settings.Update(s => s.PrivacyScanThorough = value);
        UpdateThoroughText();
    }

    async Task DownloadThoroughAsync()
    {
        var download = ClipNsfwClassifier.Download;
        if (!await _owner.ConfirmAsync(Tr.T("Download the thorough check?"),
                Tr.T("It needs CLIP, a {0} model, downloaded once from huggingface.co (Immich's published copy, the same one its smart search uses). Only the model is downloaded: nothing about your photos is sent. Checking takes longer, especially without a graphics card, and your library is checked again.",
                    Format.Bytes(download.Bytes)),
                Tr.T("Download"), danger: false))
        {
            SetThoroughQuietly(false);
            return;
        }
        var cts = _download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        IsDownloadingThorough = true;
        DownloadProgress = 0;
        UpdateThoroughText();
        try
        {
            var progress = new Progress<double>(p => DownloadProgress = p);
            await Task.Run(() => download.DownloadAsync(progress, cts.Token), cts.Token);
            AppServices.Settings.Update(s => s.PrivacyScanThorough = true);
            AppServices.PrivacyScan.ModelsChanged();
            _ = LoadModelsAsync();
            _owner.ShowNotice(Tr.T("Thorough check ready"));
        }
        catch (OperationCanceledException)
        {
            SetThoroughQuietly(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Thorough check download failed", ex);
            SetThoroughQuietly(false);
            _owner.ShowNotice(Tr.T("Couldn't download the thorough check: {0}", NetworkErrors.Describe(ex)), sticky: true);
        }
        finally
        {
            _download = null;
            IsDownloadingThorough = false;
            UpdateThoroughText();
        }
    }

    [RelayCommand]
    void CancelDownload() => _download?.Cancel();

    /// <summary>Deletes the downloaded model (the thorough check is off).</summary>
    [RelayCommand]
    void RemoveThorough()
    {
        try
        {
            ClipNsfwClassifier.Download.Delete();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove the thorough check's model", ex);
        }
        AppServices.PrivacyScan.ModelsChanged();
        _ = LoadModelsAsync();
        UpdateThoroughText();
        OnPropertyChanged(nameof(CanRemoveThorough));
    }

    void SetThoroughQuietly(bool value)
    {
        _loading = true;
        Thorough = value;
        _loading = false;
    }

    void UpdateThoroughText() => ThoroughText = IsDownloadingThorough
        ? Tr.T("Downloading CLIP ({0})…", Format.Bytes(ClipNsfwClassifier.Download.Bytes))
        : Thorough
            ? Tr.T("Also understands drawings, art and game screenshots. Slower; best with a graphics card.")
            : ClipNsfwClassifier.Download.IsDownloaded
                ? Tr.T("Off. The model is still downloaded ({0}).", Format.Bytes(ClipNsfwClassifier.Download.Bytes))
                : Tr.T("Better with drawings, art and game screenshots. Needs a one-time {0} download; best with a graphics card.", Format.Bytes(ClipNsfwClassifier.Download.Bytes));

    // ---- Progress ----

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    /// <summary>A one-time check is under way (starting, checking or paused).</summary>
    [ObservableProperty]
    public partial bool IsOneTimeRunning { get; set; }

    /// <summary>Check the library just this once, with the setting off.</summary>
    [RelayCommand]
    void ScanOnce() => AppServices.PrivacyScan.ScanOnce();

    [RelayCommand]
    void StopOnce() => AppServices.PrivacyScan.StopOnce();

    void OnStatus(PrivacyScanStatus status) => Dispatcher.UIThread.Post(() =>
    {
        Apply(status);
        if (status.Suggestions != _shownSuggestions && !_reloading)
            _ = LoadAsync();
    });

    void Apply(PrivacyScanStatus status)
    {
        RunsOn = Tr.T("Entirely on this PC, on {0}.", status.State is PrivacyScanState.Off or PrivacyScanState.Unavailable
            ? (OperatingSystem.IsWindows() && AppServices.Settings.Current.PrivacyScanUseGpu
                ? Tr.T("the graphics card when it runs (DirectML), or the processor if the graphics card can't")
                : Tr.T("the processor when it runs"))
            : status.OnGpu ? Tr.T("the graphics card (DirectML)") : Tr.T("the processor"));
        StatusText = Describe(status);
        ShowProgress = status.State is PrivacyScanState.Scanning or PrivacyScanState.Paused && status.Total > 0;
        Progress = status.Total > 0 ? status.Checked / (double)status.Total : 0;
        IsWorking = status.State is PrivacyScanState.Starting or PrivacyScanState.Scanning;
        IsOneTimeRunning = status.OneTime && status.State is PrivacyScanState.Starting or PrivacyScanState.Scanning or PrivacyScanState.Paused or PrivacyScanState.Error;
    }

    internal static string Describe(PrivacyScanStatus status)
    {
        var where = status.OnGpu ? Tr.T("on the graphics card") : Tr.T("on the processor");
        return status.State switch
        {
            PrivacyScanState.Off => Tr.T("Off. Turn it on to keep your library checked, or check it once."),
            PrivacyScanState.Unavailable => status.Detail ?? Tr.T("Not available on this PC."),
            PrivacyScanState.Starting => Tr.T("Starting…"),
            PrivacyScanState.Scanning => Tr.T("Checking {0} of {1} ({2})", status.Checked.ToString("N0"), status.Total.ToString("N0"), where)
                + (status.Remaining is { } left ? " · " + Format.TimeLeft(left) : ""),
            PrivacyScanState.Paused => Tr.T("{0} · {1} of {2} checked", status.Detail ?? Tr.T("Paused"), status.Checked.ToString("N0"), status.Total.ToString("N0")),
            PrivacyScanState.Done when status.OneTime => Tr.T("All {0} photos and videos checked, once. New ones aren't checked until you check again.", status.Total.ToString("N0")),
            PrivacyScanState.Done => Tr.T("All {0} photos and videos checked. New ones are checked as they arrive.", status.Total.ToString("N0")),
            PrivacyScanState.Error => Tr.T("Stopped: {0} Trying again soon.", status.Detail ?? ""),
            _ => "",
        };
    }

    // ---- Suggestions ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(ShowEmpty))]
    public partial int Count { get; set; }

    public bool HasItems => Count > 0;
    public bool ShowEmpty => Count == 0;

    /// <summary>Show every picture unblurred (each also unblurs while the pointer is over it).</summary>
    [ObservableProperty]
    public partial bool ShowPictures { get; set; }

    partial void OnShowPicturesChanged(bool value)
    {
        foreach (var item in Items)
            item.OnRevealChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText))]
    public partial int SelectedCount { get; set; }

    public bool HasSelection => SelectedCount > 0;
    public string SelectionText => Tr.T("{0} selected", SelectedCount.ToString("N0"));

    async Task LoadAsync()
    {
        _reloading = true;
        try
        {
            var rows = await Task.Run(AppServices.Database.GetPrivacySuggestions);
            _shownSuggestions = rows.Count;
            var known = _all.ToDictionary(i => i.Id);
            var keep = new List<PrivacySuggestionViewModel>();
            foreach (var (id, kinds, score) in rows)
                keep.Add(known.Remove(id, out var existing) ? existing : new PrivacySuggestionViewModel(this, id, (SensitiveKind)kinds, score));
            foreach (var gone in known.Values)
                gone.Dispose();
            _all.Clear();
            _all.AddRange(keep);
            BuildKindFilters();
            if (_albumsByAsset is { } byAsset)
            {
                foreach (var item in _all)
                    item.SetAlbums(byAsset.TryGetValue(item.Id, out var names) ? names : []);
                BuildAlbumChoices();
            }
            ApplyFilter();
            _ = LoadAlbumsAndDatesAsync(_all.ToList());
            if (_owner.Thumbnails is { } loader)
                foreach (var item in _all.ToList())
                    _ = item.LoadThumbnailAsync(loader, ThumbnailWidth, _lifetime.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load privacy suggestions", ex);
        }
        finally
        {
            _reloading = false;
        }
    }

    internal void UpdateSelection() => SelectedCount = Items.Count(i => i.IsSelected);

    IReadOnlyList<PrivacySuggestionViewModel> Chosen => HasSelection ? Items.Where(i => i.IsSelected).ToList() : Items.ToList();

    [RelayCommand]
    void SelectAll()
    {
        var select = SelectedCount < Items.Count;
        foreach (var item in Items)
            item.IsSelected = select;
        UpdateSelection();
    }

    /// <summary>Moves the selected pictures (or all of them) to the locked folder.</summary>
    [RelayCommand]
    async Task MoveToLockedAsync()
    {
        if (_owner.Client is not { } client || Items.Count == 0)
            return;
        var chosen = Chosen;
        try
        {
            if (!(await client.GetAuthStatusAsync(CancellationToken.None)).HasPin)
            {
                _owner.ShowNotice(Tr.T("Open the Locked folder first to create a PIN, then try again."));
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't check the PIN: {ex.Message}");
        }
        var what = Format.Plural(chosen.Count, "photo");
        if (!await _owner.ConfirmAsync(Tr.T("Move {0} to the locked folder?", what),
                Tr.T("They're hidden from your timeline, albums, search and shared links, and removed from albums. You'll see them in the locked folder after entering your PIN."),
                Tr.T("Move to locked folder"), danger: false))
            return;
        try
        {
            var ids = chosen.Select(i => i.Id).ToList();
            await client.SetVisibilityAsync(ids, "locked", CancellationToken.None);
            AppServices.Database.SetPrivacyDecision(ids, 1);
            Remove(chosen);
            _owner.ShowNotice(Tr.T("Moved {0} to the locked folder", what));
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't move to the locked folder. {0}", NetworkErrors.Describe(ex)));
        }
    }

    /// <summary>The selected pictures (or all of them) are fine: don't suggest them again.</summary>
    [RelayCommand]
    void NotSensitive()
    {
        var chosen = Chosen;
        if (chosen.Count == 0)
            return;
        AppServices.Database.SetPrivacyDecision(chosen.Select(i => i.Id).ToList(), 2);
        Remove(chosen);
        _owner.ShowNotice(Tr.T("Won't suggest {0} again", Format.Plural(chosen.Count, "photo")));
    }

    /// <summary>Moves the selected pictures (or all of them) to the trash in Immich.</summary>
    [RelayCommand]
    async Task DeleteAsync()
    {
        if (_owner.Client is not { } client || Items.Count == 0)
            return;
        var chosen = Chosen;
        var what = Format.Plural(chosen.Count, "photo");
        if (!await _owner.ConfirmAsync(Tr.T("Move {0} to the trash?", what),
                Tr.T("They're removed from your library on every device. You can restore them from the trash in Immich for a while."),
                Tr.T("Move to trash"), danger: true))
            return;
        try
        {
            var ids = chosen.Select(i => i.Id).ToList();
            await client.TrashAssetsAsync(ids, CancellationToken.None);
            AppServices.Database.SetPrivacyDecision(ids, 3);
            Remove(chosen);
            _owner.ShowNotice(Tr.T("Moved {0} to the trash", what));
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't move to the trash. {0}", NetworkErrors.Describe(ex)));
        }
    }

    void Remove(IReadOnlyList<PrivacySuggestionViewModel> items)
    {
        foreach (var item in items)
        {
            Items.Remove(item);
            _all.Remove(item);
            item.Dispose();
        }
        _shownSuggestions = _all.Count;
        BuildKindFilters();
        if (_albumsByAsset is not null)
            BuildAlbumChoices(); // counts change; an album with nothing left falls back to all albums
        ApplyFilter();
        AppServices.PrivacyScan.Refresh();
    }

    internal void Open(PrivacySuggestionViewModel item)
    {
        var shown = Items.ToList();
        _owner.OpenAssets(shown.Select(i => new TimelineAsset(i.Id, true, false, 1, null, null)).ToList(), shown.IndexOf(item));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        AppServices.PrivacyScan.StatusChanged -= OnStatus;
        foreach (var item in _all)
            item.Dispose();
    }
}

public enum PrivacySort { MostSure, Newest, Oldest, Album }

public sealed record PrivacySortChoice(PrivacySort Sort, string Label);

/// <summary>An album choice for the review page's filter: null = all, "" = not in an album.</summary>
public sealed record PrivacyAlbumChoice(string? Name, string Label);

/// <summary>A filter chip on the review page: one kind of private thing, and how many were found.</summary>
public sealed partial class PrivacyKindFilter(PrivacyReviewViewModel owner, SensitiveKind kind, string label, int count, bool isOn) : ViewModelBase
{
    bool _quiet;

    public SensitiveKind Kind { get; } = kind;
    public string Label { get; } = $"{label} ({count:N0})";

    [ObservableProperty]
    public partial bool IsOn { get; set; } = isOn;

    partial void OnIsOnChanged(bool value)
    {
        if (!_quiet)
            owner.ApplyFilter();
    }

    internal void SetQuietly(bool value)
    {
        _quiet = true;
        IsOn = value;
        _quiet = false;
    }
}

/// <summary>One bundled model, as described on the page.</summary>
public sealed class PrivacyModelViewModel(string file, string description, string size, string sha256)
{
    public string File { get; } = file;
    public string Description { get; } = description;
    public string Size { get; } = size;
    public string Sha256 { get; } = sha256;
    public string Fingerprint => $"{File} · {Size} · SHA-256 {Sha256}";
}

public sealed partial class PrivacySuggestionViewModel(PrivacyReviewViewModel owner, string id, SensitiveKind kinds, double score) : ViewModelBase, IDisposable
{
    public string Id { get; } = id;
    public SensitiveKind Kinds { get; } = kinds;
    public double Score { get; } = score;

    /// <summary>The albums it's in (empty when it's in none, or before they're known).</summary>
    public IReadOnlyList<string> AlbumNames { get; private set; } = [];

    public string AlbumText => AlbumNames.Count == 0 ? "" : string.Join(", ", AlbumNames);
    public bool HasAlbums => AlbumNames.Count > 0;

    internal void SetAlbums(IReadOnlyList<string> names)
    {
        AlbumNames = names;
        OnPropertyChanged(nameof(AlbumText));
        OnPropertyChanged(nameof(HasAlbums));
    }

    /// <summary>When it was taken (filled in after the page shows, for the date sort).</summary>
    public DateTimeOffset? Taken { get; set; }

    /// <summary>Why it was flagged, e.g. "Payment card · Text".</summary>
    public string Reason => string.Join(" · ", Reasons(Kinds));

    /// <summary>How sure the check is, e.g. "92%".</summary>
    public string ConfidenceText => Tr.T("{0}% sure", ((int)Math.Round(Score * 100)).ToString(System.Globalization.CultureInfo.CurrentCulture));

    internal static IEnumerable<string> Reasons(SensitiveKind kinds)
    {
        if (kinds.HasFlag(SensitiveKind.Explicit)) yield return Tr.T("Nudity");
        if (kinds.HasFlag(SensitiveKind.Suggestive)) yield return Tr.T("Revealing");
        if (kinds.HasFlag(SensitiveKind.PaymentCard)) yield return Tr.T("Payment card");
        if (kinds.HasFlag(SensitiveKind.IdDocument)) yield return Tr.T("ID document");
        if (kinds.HasFlag(SensitiveKind.Financial)) yield return Tr.T("Bank or tax details");
        if (kinds.HasFlag(SensitiveKind.Credentials)) yield return Tr.T("Passwords or codes");
        if (kinds.HasFlag(SensitiveKind.Medical)) yield return Tr.T("Medical");
    }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    /// <summary>A tiny version, stretched: blurry enough to hide what's in it.</summary>
    [ObservableProperty]
    public partial Bitmap? Blurred { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.UpdateSelection();

    /// <summary>Shown unblurred because "Show pictures" is on.</summary>
    public bool Revealed => owner.ShowPictures;

    internal void OnRevealChanged() => OnPropertyChanged(nameof(Revealed));

    [RelayCommand]
    void ToggleSelect() => IsSelected = !IsSelected;

    [RelayCommand]
    void Open() => owner.Open(this);

    internal async Task LoadThumbnailAsync(ThumbnailLoader loader, int width, CancellationToken ct)
    {
        if (Thumbnail is not null)
            return;
        try
        {
            var blurred = await loader.LoadAsync(Id, AssetImageSize.Thumbnail, 12, ct);
            var bitmap = await loader.LoadAsync(Id, AssetImageSize.Thumbnail, width, ct);
            if (ct.IsCancellationRequested)
            {
                blurred?.Dispose();
                bitmap?.Dispose();
                return;
            }
            Blurred = blurred;
            Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Thumbnail failed for {Id}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
        Blurred?.Dispose();
        Blurred = null;
    }
}
