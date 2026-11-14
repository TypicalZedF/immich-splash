using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;

namespace ImmichSplash.App.ViewModels;

/// <summary>
/// Full-window viewer with previous/next across the whole grid. Shows the thumbnail instantly, then the
/// preview, then (on big screens or when zoomed in) the full-resolution image, so photos look sharp.
/// </summary>
public sealed partial class PhotoViewerViewModel : ViewModelBase
{
    /// <summary>Largest image decoded when zoomed in (pixels on the long side).</summary>
    const int ZoomedMaxSize = 6000;

    readonly PhotosViewModel _owner;
    CancellationTokenSource? _cts;
    BucketState _bucket;
    int _index;
    Bitmap? _ownedImage; // the viewer's own sharp image; placeholders belong to the grid
    Bitmap? _placeholderLease; // the grid's thumbnail on show, leased so the grid can't free it meanwhile
    int _loadedSize;     // long side of the best image shown so far
    bool _fullLoaded;

    readonly bool _readOnly;

    /// <param name="readOnly">Just for looking (e.g. from the duplicates review): no favorite, share, trash…</param>
    public PhotoViewerViewModel(PhotosViewModel owner, BucketState bucket, int index, TimelineAsset asset, Bitmap? placeholder, bool readOnly = false)
    {
        _owner = owner;
        _readOnly = readOnly;
        _bucket = bucket;
        _index = index;
        Show(asset, placeholder);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideo), nameof(ShowBigPlay), nameof(DurationText), nameof(IsLocalOnly), nameof(IsInImmich), nameof(CanEdit),
        nameof(DevicePath), nameof(IsOnDevice), nameof(CanCopy), nameof(LocalStatus), nameof(FileName), nameof(CanPlayLive), nameof(IsStack), nameof(StackTip))]
    public partial TimelineAsset Asset { get; set; }

    [ObservableProperty]
    public partial Bitmap? Image { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string DateText { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    [ObservableProperty]
    public partial bool ShowInfo { get; set; }

    [ObservableProperty]
    public partial AssetDetails? Details { get; set; }

    /// <summary>Bumped on every photo change so the view can reset its zoom.</summary>
    [ObservableProperty]
    public partial int Generation { get; set; }

    public bool IsVideo => !Asset.IsImage;

    /// <summary>A live photo: its short video can be played over the photo.</summary>
    public bool CanPlayLive => Asset is { IsImage: true, IsLivePhoto: true, IsLocalOnly: false };

    public bool IsStack => Asset.IsStack;
    public string StackTip => Tr.T("Show the {0} photos in this stack", Asset.StackCount);

    /// <summary>The live photo's video is playing (no controls; back to the photo when it ends).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideoControls))]
    public partial bool IsLiveMotion { get; set; }

    public bool ShowVideoControls => IsVideoStarted && !IsLiveMotion;

    /// <summary>The pointer moved lately (set by the view); while a video plays, the controls fade away when it didn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideoControlsNow))]
    public partial bool ControlsAwake { get; set; } = true;

    /// <summary>The controls are on screen: always while paused or dragging, and while the pointer is active during playback.</summary>
    public bool ShowVideoControlsNow => ShowVideoControls && (ControlsAwake || !IsVideoPlaying || IsScrubbing);

    [RelayCommand]
    void PlayLivePhoto()
    {
        if (IsLiveMotion)
            StopVideo();
        else if (CanPlayLive)
            StartVideo(Asset.LivePhotoVideoId);
    }

    [RelayCommand]
    void OpenStack()
    {
        if (!Asset.IsStack)
            return;
        var asset = Asset;
        Close();
        _owner.OpenStack(asset);
    }
    public string DurationText => Asset.DurationMs is { } ms and > 0 ? AssetCellViewModel.FormatDuration(ms) : "";
    public bool CanEdit => !_readOnly && !_owner.IsPicker && _owner.CanUseNormalActions && !Asset.IsLocalOnly;

    /// <summary>A file on this PC that isn't in Immich yet.</summary>
    public bool IsLocalOnly => Asset.IsLocalOnly;
    public bool IsInImmich => !Asset.IsLocalOnly;

    /// <summary>Where the photo is on this PC (a local-only file, or the file a backed-up photo came from).</summary>
    public string? DevicePath => Asset.LocalPath ?? _owner.DevicePathFor(Asset.Id);
    public bool IsOnDevice => DevicePath is not null;
    public bool CanCopy => Asset.IsImage;

    public string? LocalStatus => Asset switch
    {
        { IsLocalOnly: true, LocalFailed: true } => Tr.T("On this PC · couldn't back up yet (will retry)"),
        { IsLocalOnly: true } => Tr.T("On this PC · waiting to back up"),
        _ => null,
    };

    // Info panel lines
    public string? FileName => Details?.OriginalFileName ?? (Asset.LocalPath is { } path ? Path.GetFileName(path) : null);
    public string? Dimensions => Details is { Width: > 0, Height: > 0 } d
        ? $"{d.Width} × {d.Height}" + (d.FileSize is > 0 ? $"  ·  {Format.Bytes(d.FileSize.Value)}" : "")
        : Details?.FileSize is > 0 ? Format.Bytes(Details.FileSize.Value) : null;
    public string? Camera => Details?.Camera;
    public string? Lens => Details?.Lens;
    public string? Exposure => Details?.Exposure;
    public string? Place => Details?.Place;
    public string? Description => Details?.Description;
    public string? People => Details is { People.Count: > 0 } d ? string.Join(", ", d.People) : null;
    public IReadOnlyList<string> PeopleList => Details?.People ?? [];

    // ---- Public links to this photo ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShared), nameof(SharedText))]
    public partial IReadOnlyList<SharedLinkViewModel> ShareLinks { get; set; } = [];

    public bool IsShared => ShareLinks.Count > 0;
    public string SharedText => ShareLinks.Count == 1 ? Tr.T("Shared") : Tr.T("Shared · {0} links", ShareLinks.Count);

    internal void RefreshSharing() => ShareLinks = _readOnly || Asset.IsLocalOnly ? [] : _owner.LinksFor(Asset.Id);

    /// <summary>The "Shared" chip: shows the links in the info panel.</summary>
    [RelayCommand]
    void ShowSharing()
    {
        if (!ShowInfo)
            ToggleInfo();
    }

    partial void OnDetailsChanged(AssetDetails? value)
    {
        foreach (var name in new[] { nameof(FileName), nameof(Dimensions), nameof(Camera), nameof(Lens), nameof(Exposure), nameof(Place), nameof(Description), nameof(People), nameof(PeopleList) })
            OnPropertyChanged(name);
        OnDetailsEdited();
    }

    void Show(TimelineAsset asset, Bitmap? placeholder)
    {
        Asset = asset;
        IsFavorite = asset.IsFavorite;
        DateText = DateTimeOffset.TryParse(asset.FileCreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToLocalTime() is var local ? $"{local.ToString("D", CultureInfo.CurrentCulture)} · {local.ToString("t", CultureInfo.CurrentCulture)}" : ""
            : "";
        Error = null;
        Details = null;
        RefreshSharing();
        VideoProgress = null;
        VideoError = null;
        StopVideo();
        Generation++;

        // Show the (already loaded) thumbnail instantly, then swap in sharper images.
        Image = placeholder;
        ReleaseOwnedImage();
        ReturnPlaceholder();
        if (placeholder is not null && _owner.Thumbnails?.RetainGridThumbnail(placeholder) == true)
            _placeholderLease = placeholder;
        _loadedSize = 0;
        _fullLoaded = false;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = LoadAsync(asset, _cts.Token);
        // Videos start playing when opened, like the Immich apps (not on a metered connection,
        // unless the video is on this PC).
        if (!asset.IsImage && (!AppServices.Network.IsMetered || asset.IsLocalOnly))
            StartVideo();
        if (ShowInfo && !asset.IsLocalOnly)
            _ = LoadDetailsAsync(asset, _cts.Token);
    }

    async Task LoadAsync(TimelineAsset asset, CancellationToken ct)
    {
        if (_owner.Thumbnails is not { } loader)
            return;
        IsLoading = true;
        try
        {
            var needed = _owner.ViewerPixels;
            if (asset.LocalPath is { } path)
            {
                if (asset.IsImage)
                    await SwapInAsync(asset, () => ThumbnailLoader.LoadLocalForViewerAsync(path, needed, ct), ct);
                return;
            }
            await SwapInAsync(asset, () => loader.LoadForViewerAsync(asset.Id, AssetImageSize.Preview, needed, ct), ct);

            // The preview is ~1440px; on a big or high-DPI screen fetch the full image for a crisp picture.
            // Data saving: not on a metered connection (zooming in still fetches it).
            if (asset.IsImage && _loadedSize < needed * 0.9 && _owner.FullsizeSupported && !AppServices.Network.IsMetered)
                await LoadFullAsync(asset, needed, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(asset, Asset))
                Error = Tr.T("Couldn't load this photo. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            if (ReferenceEquals(asset, Asset))
                IsLoading = false;
        }
    }

    /// <summary>Called by the view when zoomed in: load the full-resolution image once.</summary>
    public void OnZoomedIn()
    {
        if (_fullLoaded || !Asset.IsImage || _cts is not { } cts)
            return;
        var asset = Asset;
        if (asset.LocalPath is { } path)
        {
            _fullLoaded = true;
            _ = Task.Run(async () =>
            {
                try { await SwapInAsync(asset, () => ThumbnailLoader.LoadLocalForViewerAsync(path, ZoomedMaxSize, cts.Token), cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Info($"Full image failed for {path}: {ex.Message}"); }
            });
            return;
        }
        if (!_owner.FullsizeSupported)
            return;
        _fullLoaded = true;
        _ = Task.Run(async () =>
        {
            try { await LoadFullAsync(asset, ZoomedMaxSize, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Info($"Full image failed for {asset.Id}: {ex.Message}"); }
        });
    }

    async Task LoadFullAsync(TimelineAsset asset, int maxSize, CancellationToken ct)
    {
        if (_owner.Thumbnails is not { } loader)
            return;
        _fullLoaded = true;
        try
        {
            await SwapInAsync(asset, () => loader.LoadForViewerAsync(asset.Id, AssetImageSize.Fullsize, maxSize, ct), ct);
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound)
        {
            _owner.FullsizeSupported = false; // older server: the preview is the best there is
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The preview is already showing; a sharper version failing isn't worth an error.
            Log.Info($"Full-size image unavailable for {asset.Id}: {ex.Message}");
        }
    }

    async Task SwapInAsync(TimelineAsset asset, Func<Task<Bitmap>> load, CancellationToken ct)
    {
        var bitmap = await load();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var size = Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            if (ct.IsCancellationRequested || !ReferenceEquals(asset, Asset) || size <= _loadedSize)
            {
                bitmap.Dispose();
                return;
            }
            Image = bitmap;
            ReleaseOwnedImage();
            ReturnPlaceholder();
            _ownedImage = bitmap;
            _loadedSize = size;
        });
    }

    async Task LoadDetailsAsync(TimelineAsset asset, CancellationToken ct)
    {
        if (asset.IsLocalOnly || AppServices.Account.Client is not { } client)
            return;
        try
        {
            var details = await client.GetAssetAsync(asset.Id, ct);
            if (!ct.IsCancellationRequested && ReferenceEquals(asset, Asset))
                Details = details;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Details failed for {asset.Id}: {ex.Message}");
        }
    }

    [RelayCommand]
    Task Next() => StepAsync(+1);

    [RelayCommand]
    Task Previous() => StepAsync(-1);

    async Task StepAsync(int delta)
    {
        try
        {
            var result = await _owner.StepAsync(_bucket, _index, delta, CancellationToken.None);
            if (result is not { } r)
                return;
            _bucket = r.Bucket;
            _index = r.Index;
            Show(r.Asset, null);
        }
        catch (Exception ex)
        {
            Error = NetworkErrors.Describe(ex);
        }
    }

    // ---- Videos: played right here in the viewer ----

    IVideoPlayback? _video;

    /// <summary>The player when mpv draws on the graphics card (the view's MpvVideoView shows it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMpvVideo))]
    public partial Services.Mpv.MpvPlayer? MpvVideo { get; set; }

    public bool HasMpvVideo => MpvVideo is not null;
    long _lastTimeUpdate;

    /// <summary>The playing video's picture (null until the first frame, while the photo preview shows).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideoFrame))]
    public partial Avalonia.Media.Imaging.WriteableBitmap? VideoFrame { get; set; }

    public bool HasVideoFrame => VideoFrame is not null || _mpvFrame;

    bool _mpvFrame;

    /// <summary>Raised when the video's picture changed (the view repaints it).</summary>
    public event Action? VideoFrameUpdated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBigPlay), nameof(ShowVideoControlsNow))]
    public partial bool IsVideoPlaying { get; set; }

    /// <summary>A player is open for this video (controls show).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBigPlay), nameof(ShowVideoControls), nameof(ShowVideoControlsNow))]
    public partial bool IsVideoStarted { get; set; }

    public bool ShowBigPlay => IsVideo && !IsVideoPlaying && !IsVideoLoading;

    /// <summary>What the video is waiting on before its first frame (shown under a spinner), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoLoading), nameof(ShowBigPlay))]
    public partial string? VideoLoadingText { get; set; }

    public bool IsVideoLoading => VideoLoadingText is not null;

    /// <summary>Where the video is, from 0 to 1 (the seek bar; dragging it seeks).</summary>
    [ObservableProperty]
    public partial double VideoPosition { get; set; }

    [ObservableProperty]
    public partial string VideoTimeText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <summary>Why the video can't play here; "Open in video player" is offered instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayHere))]
    public partial string? VideoError { get; set; }

    public bool CanPlayHere => VideoError is null;

    /// <summary>While a video is being fetched for the external player: how far along.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFetchingVideo))]
    public partial string? VideoProgress { get; set; }

    public bool IsFetchingVideo => VideoProgress is not null;

    static string VideoCacheDir => Path.Combine(Path.GetTempPath(), "ImmichSplash", "videos");

    partial void OnIsMutedChanged(bool value)
    {
        if (_video is not null)
            _video.IsMuted = value;
        OnPropertyChanged(nameof(VolumeIcon));
    }

    /// <summary>Plays or pauses (starting the player the first time).</summary>
    [RelayCommand]
    void PlayPause()
    {
        if (Asset.IsImage)
            return;
        if (_video is null)
            StartVideo();
        else
            _video.TogglePlay();
    }

    /// <summary>Kept for compatibility with the Play keyboard shortcut and old bindings.</summary>
    [RelayCommand]
    void Play() => PlayPause();

    [RelayCommand]
    void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    void SkipBack() => _video?.SeekBy(TimeSpan.FromSeconds(-10));

    [RelayCommand]
    void SkipForward() => _video?.SeekBy(TimeSpan.FromSeconds(10));

    /// <param name="motionId">For a live photo: its video, played over the photo.</param>
    void StartVideo(string? motionId = null)
    {
        StopVideo();
        if (Asset.IsImage && motionId is null)
            return;
        VideoError = null;
        if (!Services.Mpv.MpvPlayer.IsAvailable)
        {
            VideoError = Services.Mpv.MpvPlayer.Unavailable;
            return;
        }
        // On the graphics card normally; into memory when the app draws without it.
        var software = !Program.GpuRequested;
        Services.Mpv.MpvPlayer player;
        var fromServer = false;
        try
        {
            if (motionId is null && DevicePath is { } local && File.Exists(local))
                player = Services.Mpv.MpvPlayer.ForFile(local, software);
            else if (AppServices.Account.Client is { } client)
            {
                var (url, headers) = client.VideoPlaybackRequest(motionId ?? Asset.Id);
                player = Services.Mpv.MpvPlayer.ForUrl(url, headers, software);
                fromServer = true;
            }
            else
                return;
        }
        catch (Exception ex)
        {
            Log.Warn("libmpv couldn't start", ex);
            VideoError = Tr.T("This video couldn't be played here.");
            return;
        }
        var asset = Asset;
        _video = player;
        player.IsMuted = IsMuted;
        player.Volume = Volume;
        player.FirstFrame += () =>
        {
            if (!ReferenceEquals(_video, player))
                return;
            _mpvFrame = true;
            OnPropertyChanged(nameof(HasVideoFrame));
            VideoLoadingText = null;
        };
        if (software)
        {
            player.FrameChanged += () => { if (ReferenceEquals(_video, player)) VideoFrame = player.Frame; };
            player.FrameReady += () => { if (ReferenceEquals(_video, player)) VideoFrameUpdated?.Invoke(); };
        }
        else
            MpvVideo = player;
        Hook(player, asset);
        IsLiveMotion = motionId is not null;
        IsVideoStarted = true;
        VideoLoadingText = IsLiveMotion ? null : fromServer ? Tr.T("Loading the video from your server…") : Tr.T("Opening the video…");
        _ = WatchStartAsync(player, fromServer);
        player.Play();
    }

    /// <summary>Follows a player's state, time and failure (either engine).</summary>
    void Hook(IVideoPlayback player, TimelineAsset asset)
    {
        player.StateChanged += () =>
        {
            if (!ReferenceEquals(_video, player))
                return;
            IsVideoPlaying = player.IsPlaying;
            if (player.Ended)
                VideoLoadingText = null;
            if (player.Ended && IsLiveMotion)
                StopVideo(); // back to the photo
        };
        player.TimeChanged += () => { if (ReferenceEquals(_video, player)) UpdateVideoTime(player); };
        player.Failed += () =>
        {
            if (!ReferenceEquals(_video, player))
                return;
            Log.Warn($"Video {asset.Id} couldn't be played in the app");
            var live = IsLiveMotion;
            StopVideo();
            if (live)
                Error = Tr.T("The live photo's video couldn't be played.");
            else
                VideoError = Tr.T("This video couldn't be played here.");
        };
    }

    /// <summary>
    /// Watches the start: if the video is playing but no picture has come (the time moves, the screen
    /// doesn't), says so instead of loading forever. If it's only slow, says why.
    /// </summary>
    async Task WatchStartAsync(IVideoPlayback player, bool fromServer)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (!ReferenceEquals(_video, player) || player.HasFrame)
            return;
        if (player.IsPlaying && player.Time > TimeSpan.FromSeconds(2))
        {
            Log.Warn($"Video {Asset.Id} plays but shows no picture");
            StopVideo();
            VideoError = Tr.T("This video plays, but its picture can't be shown here.");
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (ReferenceEquals(_video, player) && VideoLoadingText is not null)
            VideoLoadingText = fromServer
                ? Tr.T("Still loading… big videos or a slow connection to your server can take a moment.")
                : Tr.T("Still opening… big videos can take a moment.");
    }

    void UpdateVideoTime(IVideoPlayback player)
    {
        if (HoldingPosition(player))
            return; // dragging the bar (or just dropped): the bar follows you, not the video
        // mpv reports the time on every picture; the bar and the clock only need ~8 updates a second
        // (redrawing and re-measuring them 30-60 times a second is what made playback feel laggy).
        var now = Environment.TickCount64;
        if (now - _lastTimeUpdate < 120 && player.IsPlaying)
            return;
        _lastTimeUpdate = now;
        var length = player.Length;
        VideoPosition = length > TimeSpan.Zero ? player.Time / length : 0;
        VideoTimeText = length > TimeSpan.Zero
            ? $"{AssetCellViewModel.FormatDuration((long)player.Time.TotalMilliseconds)} / {AssetCellViewModel.FormatDuration((long)length.TotalMilliseconds)}"
            : AssetCellViewModel.FormatDuration((long)player.Time.TotalMilliseconds);
    }

    void StopVideo()
    {
        IsScrubbing = false;
        ClosePreviewPlayer();
        _previewFailed = false;
        VideoLoadingText = null;
        var video = _video;
        _video = null;
        MpvVideo = null; // the view frees mpv's drawing context, then the player shuts down
        video?.Dispose();
        VideoFrame = null;
        _mpvFrame = false;
        OnPropertyChanged(nameof(HasVideoFrame));
        IsVideoPlaying = false;
        IsVideoStarted = false;
        IsLiveMotion = false;
        VideoPosition = 0;
        VideoTimeText = "";
    }

    /// <summary>
    /// The fallback when the app can't play a video: open it in the PC's video player (Immich videos
    /// are fetched first into a temporary folder that's cleaned up after a day).
    /// </summary>
    [RelayCommand]
    async Task OpenInVideoPlayerAsync()
    {
        if (Asset.IsImage || IsFetchingVideo)
            return;
        if (DevicePath is { } local && File.Exists(local))
        {
            ShellLauncher.Open(local);
            return;
        }
        if (AppServices.Account.Client is not { } client || _cts is not { } cts)
            return;
        var asset = Asset;
        var target = Path.Combine(VideoCacheDir, $"{asset.Id}.mp4");
        try
        {
            if (!File.Exists(target))
            {
                VideoProgress = Tr.T("Getting video…");
                await Task.Run(() => CleanVideoCache(), cts.Token);
                Directory.CreateDirectory(VideoCacheDir);
                var partial = target + ".part";
                await using (var file = File.Create(partial))
                {
                    await client.DownloadVideoAsync(asset.Id, file, p => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(asset, Asset) && IsFetchingVideo)
                            VideoProgress = Tr.T("Getting video… {0}", p.ToString("P0", CultureInfo.CurrentCulture));
                    }), cts.Token);
                }
                File.Move(partial, target, overwrite: true);
            }
            ShellLauncher.Open(target);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not play {asset.Id}", ex);
            if (ReferenceEquals(asset, Asset))
                Error = Tr.T("Couldn't play this video. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            VideoProgress = null;
        }
    }

    static void CleanVideoCache()
    {
        try
        {
            if (!Directory.Exists(VideoCacheDir))
                return;
            foreach (var file in new DirectoryInfo(VideoCacheDir).EnumerateFiles())
                if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    try { file.Delete(); } catch (IOException) { } // still open in a player
        }
        catch (Exception ex)
        {
            Log.Info($"Video cache cleanup: {ex.Message}");
        }
    }

    [RelayCommand]
    void ToggleInfo()
    {
        ShowInfo = !ShowInfo;
        if (ShowInfo && Details is null && _cts is { } cts)
            _ = LoadDetailsAsync(Asset, cts.Token);
    }

    [RelayCommand]
    async Task ToggleFavoriteAsync()
    {
        var value = !IsFavorite;
        IsFavorite = value;
        if (!await _owner.SetFavoriteAsync([Asset.Id], value))
            IsFavorite = !value;
        else
            Asset = Asset with { IsFavorite = value };
    }

    [RelayCommand]
    void Share() => _owner.ShowShare([Asset.Id]);

    [RelayCommand]
    Task CopyImageAsync() => _owner.CopyImageAsync(Asset.Id);

    [RelayCommand]
    void AddToAlbum() => _owner.ShowAlbumPicker([Asset.Id]);

    [RelayCommand]
    Task DownloadAsync() => _owner.DownloadAsync([Asset.Id]);

    [RelayCommand]
    async Task TrashAsync()
    {
        if (await _owner.TrashAsync([Asset.Id]))
            Close();
    }

    void ReturnPlaceholder()
    {
        var leased = _placeholderLease;
        _placeholderLease = null;
        if (leased is not null)
            _owner.Thumbnails?.ReturnGridThumbnail(leased);
    }

    void ReleaseOwnedImage()
    {
        var old = _ownedImage;
        _ownedImage = null;
        if (old is not null && !ReferenceEquals(old, Image))
            old.Dispose();
    }

    [RelayCommand]
    void Close() => _owner.CloseViewer(); // the page then calls Shutdown

    /// <summary>The viewer was closed or replaced (whatever closed it): stop the video and free the pictures.</summary>
    internal void Shutdown()
    {
        StopSlideshow();
        StopVideo();
        _cts?.Cancel();
        Image = null;
        ReturnPlaceholder();
        ReleaseOwnedImage();
    }

    /// <summary>Opens the file on this PC in its default app (how local videos are played).</summary>
    [RelayCommand]
    void OpenFile()
    {
        if (DevicePath is { } path)
            ShellLauncher.Open(path);
    }

    [RelayCommand]
    void ShowInFolder()
    {
        if (DevicePath is { } path)
            ShellLauncher.Reveal(path);
    }

    [RelayCommand]
    void OpenInImmich()
    {
        if (AppServices.Settings.Current.WebUrl is { } web)
            ShellLauncher.Open($"{web.TrimEnd('/')}/photos/{Asset.Id}");
    }
}
