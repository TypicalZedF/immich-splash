using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Controls;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Layout;

namespace ImmichSplash.App.ViewModels;

/// <summary>The library sections in the sidebar (like the Immich web app's, with the mobile app's Library group).</summary>
public enum LibraryPage
{
    Timeline,
    Explore,
    Map,
    Sharing,
    Albums,
    Favorites,
    Archive,
    Locked,
    Trash,
    Utilities,
}

/// <summary>What the grid is showing: a title and how to load its groups.</summary>
/// <param name="ShowSizes">Tiles show each file's size (Large files).</param>
/// <param name="OffersArchiveAll">An "Archive all" button in the header (screenshots utility).</param>
public sealed record GridQuery(string Title, Func<ImmichClient, CancellationToken, Task<List<BucketState>>> Load, AlbumInfo? Album = null,
    bool ShowSizes = false, bool OffersArchiveAll = false);

/// <summary>
/// One section of your Immich library (see <see cref="LibraryPage"/>): the timeline, Explore (people
/// and places), the map, sharing, albums, favorites, the archive, the locked folder or the trash, plus
/// search results and anything opened from them. Grids only build what's on screen: months load as
/// their rows scroll into view and thumbnails are released when they scroll away. Select photos to act
/// on them (what's offered depends on the section).
/// </summary>
public sealed partial class PhotosViewModel : ViewModelBase, IDisposable
{
    public const double Gap = 4;

    /// <summary>Height of a month heading in the grid (fixed, so the date rail knows where months are).</summary>
    public const double HeaderHeight = 54;
    public const double MinTile = 80, MaxTile = 360;
    double _targetTile = Math.Clamp(AppServices.Settings.Current.ThumbnailSize, MinTile, MaxTile);
    double _scaling = 1;
    const int CardDecodeWidth = 360;

    readonly CancellationTokenSource _lifetime = new();
    readonly MainWindowViewModel? _main;
    readonly Action<AssetCellViewModel>? _pick;
    readonly HashSet<string> _selected = [];
    CancellationTokenSource? _gridLoad;
    List<BucketState> _buckets = [];
    int _columns;
    double _width;

    public PhotosViewModel(ShellViewModel shell, LibraryPage page) : this(shell.Main, null, page)
    {
    }

    PhotosViewModel(MainWindowViewModel? main, Action<AssetCellViewModel>? pick, LibraryPage page)
    {
        _main = main;
        _pick = pick;
        Page = page;
        if (Client is { } client)
            Thumbnails = new ThumbnailLoader(client);
        _ = LoadPageAsync();
        _ = WatchMemoryAsync();
    }

    /// <summary>The same library for choosing a photo (e.g. a profile picture): clicking a photo picks it instead of opening it.</summary>
    public static PhotosViewModel ForPicking(Action<AssetCellViewModel> pick) => new(null, pick, LibraryPage.Timeline);

    public bool IsPicker => _pick is not null;
    public bool IsNotPicker => !IsPicker;

    internal ImmichClient? Client => AppServices.Account.Client;
    internal ThumbnailLoader? Thumbnails { get; private set; }

    /// <summary>Pixels to decode thumbnails at (tile size × display scaling).</summary>
    internal int DecodeWidth { get; private set; } = 256;

    /// <summary>Pixels to decode a tile of this width at (its width × display scaling).</summary>
    internal int DecodeWidthFor(double tileWidth) => (int)Math.Ceiling(Math.Max(48, tileWidth) * Math.Max(1, _scaling));

    /// <summary>Long side, in pixels, the viewer needs to look sharp (set by the view from its size and scaling).</summary>
    internal int ViewerPixels { get; set; } = 2048;

    /// <summary>False once the server turns out not to offer full-size images (older versions).</summary>
    internal bool FullsizeSupported { get; set; } = true;

    // ---- Page ----

    public LibraryPage Page { get; }

    public bool IsTrash => Page == LibraryPage.Trash && Drill is null;
    public bool IsArchive => Page == LibraryPage.Archive && Drill is null;
    public bool IsLockedFolder => Page == LibraryPage.Locked;

    Task LoadPageAsync() => Page switch
    {
        LibraryPage.Explore => Task.WhenAll(LoadPeopleAsync(), LoadPlacesAsync(), LoadTagsAsync()),
        LibraryPage.Map => LoadMarkersAsync(),
        LibraryPage.Sharing => LoadSharingAsync(),
        LibraryPage.Albums => LoadAlbumsAsync(),
        LibraryPage.Locked => CheckLockedAsync(),
        LibraryPage.Utilities => LoadUtilitiesAsync(),
        _ => LoadGridAsync(),
    };

    /// <summary>An album, person, place, search or map area opened from the page; Back returns to the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowAlbums), nameof(ShowExplore), nameof(ShowMap), nameof(ShowSharing),
        nameof(ShowLockedPrompt), nameof(IsDrilledIn), nameof(Heading), nameof(CanShareAlbum), nameof(CanDeleteAlbum), nameof(ShowHeading),
        nameof(IsTrash), nameof(IsArchive), nameof(ShowTrashActions), nameof(ShowLockAction), nameof(ShowUtilities), nameof(ShowArchiveAll))]
    public partial GridQuery? Drill { get; set; }

    public bool IsDrilledIn => Drill is not null || Duplicates is not null || Privacy is not null;

    /// <summary>The profile picture dialog has its own title, so only show ours once you've opened something.</summary>
    public bool ShowHeading => !IsPicker || IsDrilledIn;
    public bool ShowGrid => Drill is not null
        || Page is LibraryPage.Timeline or LibraryPage.Favorites or LibraryPage.Archive or LibraryPage.Trash
        || (Page == LibraryPage.Locked && IsUnlocked);
    public bool ShowAlbums => Drill is null && Page == LibraryPage.Albums;
    public bool ShowExplore => Drill is null && Page == LibraryPage.Explore;
    public bool ShowMap => Drill is null && Page == LibraryPage.Map;
    public bool ShowSharing => Drill is null && Page == LibraryPage.Sharing;
    public bool ShowLockedPrompt => Drill is null && Page == LibraryPage.Locked && !IsUnlocked;
    public bool ShowTrashActions => IsTrash && !IsEmpty && !IsLoading;
    public bool ShowArchiveAll => Drill?.OffersArchiveAll == true && !IsEmpty && !IsLoading;
    public bool ShowLockAction => Page == LibraryPage.Locked && IsUnlocked && Drill is null;
    public bool CanShareAlbum => Drill?.Album is not null && !IsPicker;
    public bool CanDeleteAlbum => Drill?.Album is { } album && !IsPicker && album.IsOwnedBy(AppServices.Settings.Current.UserId);

    public string Heading => Drill?.Title ?? (Duplicates is not null ? Tr.T("Review duplicates") : null)
        ?? (Privacy is not null ? Tr.T("Suggest for the locked folder") : null) ?? Page switch
    {
        LibraryPage.Utilities => Tr.T("Utilities"),
        LibraryPage.Explore => Tr.T("Explore"),
        LibraryPage.Map => Tr.T("Map"),
        LibraryPage.Sharing => Tr.T("Sharing"),
        LibraryPage.Albums => Tr.T("Albums"),
        LibraryPage.Favorites => Tr.T("Favorites"),
        LibraryPage.Archive => Tr.T("Archive"),
        LibraryPage.Locked => Tr.T("Locked folder"),
        LibraryPage.Trash => Tr.T("Trash"),
        _ => Tr.T("Photos"),
    };

    /// <summary>Back on this page after visiting others: refresh lists that other pages can change.</summary>
    internal void OnShownAgain()
    {
        if (Drill is not null || Duplicates is not null || Privacy is not null)
            return;
        _ = Page switch
        {
            LibraryPage.Sharing => LoadSharingAsync(),
            LibraryPage.Albums => LoadAlbumsAsync(),
            LibraryPage.Utilities => LoadUtilitiesAsync(),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>Out of whatever is open (search results, an album, a review…) to the page itself.</summary>
    internal void ReturnToStart()
    {
        if (Viewer is not null)
            return; // the photo viewer covers the page; it has its own close
        for (var i = 0; i < 4 && (Drill is not null || Duplicates is not null || Privacy is not null); i++)
            Back();
    }

    [RelayCommand]
    void Back()
    {
        if (Duplicates is { } duplicates)
        {
            Duplicates = null;
            duplicates.Dispose();
            _ = LoadUtilitiesAsync();
            return;
        }
        if (Privacy is { } privacy)
        {
            Privacy = null;
            privacy.Dispose();
            _ = LoadUtilitiesAsync();
            return;
        }
        if (Drill is null)
            return;
        Drill = null;
        ClearSelection();
        ReleaseRows();
        if (ShowGrid)
            _ = LoadGridAsync();
    }

    void OpenDrill(GridQuery query)
    {
        ClearSelection();
        Drill = query;
        _ = LoadGridAsync();
    }

    // ---- Grid ----

    [ObservableProperty]
    public partial ObservableCollection<object> Rows { get; set; } = [];

    /// <summary>Where each month starts in the grid, for the date rail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSections))]
    public partial IReadOnlyList<TimelineSection> Sections { get; set; } = [];

    public bool HasSections => Sections.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTrashActions), nameof(ShowArchiveAll))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTrashActions), nameof(ShowArchiveAll))]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial PhotoViewerViewModel? Viewer { get; set; }

    // However the viewer goes away (closed, opening the editor, a jump to the timeline, locking), its video must stop.
    partial void OnViewerChanged(PhotoViewerViewModel? oldValue, PhotoViewerViewModel? newValue) => oldValue?.Shutdown();

    [RelayCommand]
    Task Refresh() => Duplicates is { } duplicates ? duplicates.RefreshCommand.ExecuteAsync(null)
        : ShowGrid ? LoadGridAsync() : LoadPageAsync();

    GridQuery CurrentQuery => Drill ?? Page switch
    {
        LibraryPage.Favorites => new GridQuery(Tr.T("Favorites"), (c, ct) => TimelineAsync(c, new TimelineFilter(Favorites: true), ct)),
        LibraryPage.Archive => new GridQuery(Tr.T("Archive"), (c, ct) => TimelineAsync(c, new TimelineFilter(Visibility: "archive"), ct)),
        LibraryPage.Locked => new GridQuery(Tr.T("Locked folder"), (c, ct) => TimelineAsync(c, new TimelineFilter(Visibility: "locked"), ct)),
        LibraryPage.Trash => new GridQuery(Tr.T("Trash"), (c, ct) => TimelineAsync(c, new TimelineFilter(Trashed: true), ct)),
        _ => new GridQuery(Tr.T("Photos"), (c, ct) => IsPicker ? TimelineAsync(c, TimelineFilter.All, ct) : TimelineWithLocalAsync(c, ct)),
    };

    // ---- Thumbnail size (the slider, or Ctrl + scroll) ----

    /// <summary>The photo the grid's top row starts with (asked of the view, to keep your place when resizing).</summary>
    internal Func<(BucketState Bucket, int Index)?>? GetTopAnchor { get; set; }

    /// <summary>Asks the view to scroll so this row is at the top.</summary>
    public event Action<int>? ScrollToRowRequested;

    CancellationTokenSource? _saveTileSize;

    /// <summary>How big thumbnails are, in pixels (remembered).</summary>
    public double ThumbnailSize
    {
        get => _targetTile;
        set
        {
            value = Math.Clamp(value, MinTile, MaxTile);
            if (Math.Abs(value - _targetTile) < 0.5)
                return;
            _targetTile = value;
            OnPropertyChanged();
            if (_width > 0)
                ApplyLayout(immediate: true);
            // Save once the slider settles, not on every step.
            _saveTileSize?.Cancel();
            var cts = _saveTileSize = new CancellationTokenSource();
            var size = (int)value;
            _ = Task.Delay(500, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                    AppServices.Settings.Update(s => s.ThumbnailSize = size);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Called by the view when its width or display scaling changes.</summary>
    public void SetLayout(double width, double scaling)
    {
        if (width <= 0)
            return;
        _width = width;
        _scaling = scaling;
        ApplyLayout(immediate: false);
    }

    /// <summary>
    /// Works out the tile size for the current width and rebuilds the rows if it changed. Dragging the
    /// window's edge changes the width many times a second, so those rebuilds wait for it to settle.
    /// </summary>
    void ApplyLayout(bool immediate)
    {
        var columns = Math.Max(2, (int)((_width + Gap) / (_targetTile + Gap)));
        var tile = TileSize(columns);
        DecodeWidth = (int)Math.Ceiling(tile * Math.Max(1, _scaling));
        if (_columns == columns && tile == _builtTile && _width == _builtWidth)
            return;
        var first = _columns == 0;
        _columns = columns;
        if (first || immediate || Rows.Count == 0)
        {
            RebuildRows(keepPlace: !first);
            return;
        }
        _resizeRebuild?.Cancel();
        var cts = _resizeRebuild = new CancellationTokenSource();
        _ = Task.Delay(140, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => RebuildRows(keepPlace: true));
        }, TaskScheduler.Default);
    }

    CancellationTokenSource? _resizeRebuild;
    double _builtTile, _builtWidth;

    double TileSize(int columns) => Math.Floor((_width - Gap * (columns - 1)) / columns);

    void RebuildRows(bool keepPlace = false)
    {
        if (_columns == 0 || _buckets.Count == 0)
        {
            _rowOffsets = [0];
            Rows = [];
            Sections = [];
            return;
        }

        // Keep the photo at the top of the screen where it is, whatever happens to the rows around it.
        var anchor = keepPlace ? GetTopAnchor?.Invoke() : null;
        var tile = TileSize(_columns);
        _builtTile = tile;
        _builtWidth = _width;
        foreach (var old in Rows.OfType<AssetRowViewModel>())
            old.Unrealize();
        var rows = new List<object>();
        foreach (var bucket in _buckets)
        {
            rows.Add(new MonthHeaderViewModel(bucket.Label, Format.Plural(bucket.Count, "item")));
            rows.AddRange(BuildBucketRows(bucket, tile));
        }
        if (ShowsMemories && _memoryCards.Count > 0)
            rows.Insert(0, new MemoriesRowViewModel(_memoryCards));
        UpdateOffsets(rows);
        Rows = new ObservableCollection<object>(rows);
        if (anchor is { } a && RowIndexOf(a) is >= 0 and var index)
            ScrollToRowRequested?.Invoke(index);
        else
            ApplyPendingScroll();
    }

    /// <summary>
    /// The rows of one group: in the photos' own shapes once they're known, or equal squares as an
    /// estimate until then (a month is only fetched when it comes into view).
    /// </summary>
    List<AssetRowViewModel> BuildBucketRows(BucketState bucket, double tile)
    {
        var rows = new List<AssetRowViewModel>();
        if (bucket.TryGetLoaded(out var assets) && assets.Count > 0)
        {
            bucket.LaidOutFor = (_width, tile);
            foreach (var row in JustifiedLayout.Compute(assets.Select(a => a.Ratio).ToList(), _width, tile, Gap))
                rows.Add(new AssetRowViewModel(this, bucket, row.Start, row.Widths, row.Height));
            return rows;
        }
        bucket.LaidOutFor = null;
        for (var start = 0; start < bucket.Count; start += _columns)
            rows.Add(new AssetRowViewModel(this, bucket, start, Math.Min(_columns, bucket.Count - start), tile));
        return rows;
    }

    int RowIndexOf((BucketState Bucket, int Index) place) =>
        Rows.ToList().FindIndex(r => r is AssetRowViewModel row && row.Bucket == place.Bucket && place.Index >= row.Start && place.Index < row.Start + row.Cells.Count);

    readonly HashSet<BucketState> _layoutQueued = [];

    /// <summary>A group's photos have arrived: lay it out in their shapes (soon, not in the middle of drawing).</summary>
    internal void QueueBucketLayout(BucketState bucket)
    {
        if (!_layoutQueued.Add(bucket))
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _layoutQueued.Remove(bucket);
            RelayoutBucket(bucket);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    void RelayoutBucket(BucketState bucket)
    {
        if (_columns == 0 || !bucket.TryGetLoaded(out _))
            return;
        var tile = TileSize(_columns);
        if (bucket.LaidOutFor is { } laid && laid.Width == _width && laid.Tile == tile)
            return;
        var first = -1;
        var count = 0;
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i] is AssetRowViewModel row && row.Bucket == bucket)
            {
                if (first < 0)
                    first = i;
                count++;
            }
        }
        if (first < 0)
            return; // not in the grid any more

        // If this group is above (or is) the one on screen, putting its rows right moves everything below
        // it: remember the photo at the top and bring it back there.
        var anchor = GetTopAnchor?.Invoke();
        var keepPlace = anchor is { } a && _buckets.IndexOf(bucket) <= _buckets.IndexOf(a.Bucket);

        var replacement = BuildBucketRows(bucket, tile);
        for (var k = 0; k < count; k++)
        {
            (Rows[first] as AssetRowViewModel)?.Unrealize();
            Rows.RemoveAt(first);
        }
        for (var j = 0; j < replacement.Count; j++)
            Rows.Insert(first + j, replacement[j]);
        UpdateOffsets(Rows);
        if (keepPlace && anchor is { } place && RowIndexOf(place) is >= 0 and var index)
            ScrollToRowRequested?.Invoke(index);
    }

    /// <summary>Works out where every row and month starts (for the date rail).</summary>
    void UpdateOffsets(IReadOnlyList<object> rows)
    {
        // Every group's height comes from its actual rows (they differ once photos have their own shapes).
        var sections = new List<TimelineSection>();
        _rowOffsets = new double[rows.Count + 1];
        var group = -1;
        double groupStart = 0;
        void CloseGroup(double end)
        {
            if (group < 0 || group >= _buckets.Count)
                return;
            var bucket = _buckets[group];
            var shortLabel = bucket.Day is null && bucket.Month is { } month ? month.ToString("MMM yyyy", System.Globalization.CultureInfo.CurrentCulture) : bucket.Label;
            sections.Add(new TimelineSection(shortLabel, bucket.Month?.Year, groupStart, end - groupStart));
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is MonthHeaderViewModel)
            {
                CloseGroup(_rowOffsets[i]);
                group++;
                groupStart = _rowOffsets[i];
            }
            _rowOffsets[i + 1] = _rowOffsets[i] + rows[i] switch
            {
                AssetRowViewModel row => row.RowHeight,
                MemoriesRowViewModel => MemoriesHeight,
                _ => HeaderHeight,
            };
        }
        CloseGroup(_rowOffsets[rows.Count]);
        // The rail is for the timeline; one big group (e.g. search results) has no dates to show.
        Sections = sections.Count > 1 && sections.Any(s => s.Year is not null) ? sections : [];
    }

    // Exact positions of the grid's rows. The list only estimates the height of rows it hasn't drawn,
    // so the date rail works from these instead of the scrollbar.
    double[] _rowOffsets = [0];

    /// <summary>Total height of the grid's rows.</summary>
    public double ContentHeight => _rowOffsets[^1];

    /// <summary>Where row <paramref name="index"/> starts.</summary>
    public double RowOffset(int index) => _rowOffsets[Math.Clamp(index, 0, _rowOffsets.Length - 1)];

    /// <summary>The row at a position (and how far into it the position is).</summary>
    public (int Index, double Within) RowAt(double offset)
    {
        if (_rowOffsets.Length < 2)
            return (0, 0);
        var i = Array.BinarySearch(_rowOffsets, offset);
        if (i < 0)
            i = ~i - 1;
        i = Math.Clamp(i, 0, _rowOffsets.Length - 2);
        return (i, offset - _rowOffsets[i]);
    }

    void ReleaseRows()
    {
        foreach (var row in Rows.OfType<AssetRowViewModel>())
            row.Unrealize();
        _rowOffsets = [0];
        Rows = [];
        Sections = [];
        _buckets = [];
    }

    IEnumerable<AssetCellViewModel> VisibleCells => Rows.OfType<AssetRowViewModel>().SelectMany(r => r.Cells);

    // ---- Search ----

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>Searches from outside (the top bar).</summary>
    public void SearchFor(string text)
    {
        SearchText = text;
        Search();
    }

    [RelayCommand]
    void Search()
    {
        var text = SearchText.Trim();
        if (text.Length == 0)
            return;
        OpenDrill(new GridQuery($"“{text}”", async (client, ct) =>
        {
            try
            {
                // Smart search finds "dog on the beach" (needs machine learning on the server)…
                var results = await client.SmartSearchAsync(text, 250, ct);
                return BucketState.FromAssets(results, Tr.T("Best matches"));
            }
            catch (ImmichApiException ex) when (ex.StatusCode is not System.Net.HttpStatusCode.Unauthorized)
            {
                // …and without it, fall back to file names.
                Log.Info($"Smart search unavailable ({ex.Message}); searching file names");
                return BucketState.FromAssets(await client.SearchAsync(text, null, 1000, ct));
            }
        }));
    }

    /// <summary>Photos whose file name contains the text (e.g. "IMG_4410").</summary>
    public void SearchFileName(string text) =>
        OpenDrill(new GridQuery(Tr.T("File names with “{0}”", text), async (client, ct) => BucketState.FromAssets(await client.SearchAsync(text, null, 2000, ct))));

    // ---- Map ----

    [ObservableProperty]
    public partial IReadOnlyList<MapMarker> Markers { get; set; } = [];

    /// <summary>Online street detail over the built-in map (remembered; off by default for privacy).</summary>
    public bool ShowStreetDetail
    {
        get => AppServices.Settings.Current.MapStreetDetail;
        set
        {
            if (value == ShowStreetDetail)
                return;
            AppServices.Settings.Update(s => s.MapStreetDetail = value);
            OnPropertyChanged();
        }
    }

    async Task LoadMarkersAsync()
    {
        if (Client is not { } client)
            return;
        IsLoadingCollection = true;
        CollectionMessage = null;
        try
        {
            Markers = await client.GetMapMarkersAsync(Core.Settings.MapSettings.Filter(AppServices.Settings.Current, DateTimeOffset.Now), _lifetime.Token);
            if (Markers.Count == 0)
                CollectionMessage = Tr.T("No photos with a location yet.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the map", ex);
            CollectionMessage = Tr.T("Couldn't load the map. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsLoadingCollection = false;
        }
    }

    // ---- Notices: small toasts over the page (also over the photo viewer) ----

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>The last message shown (kept for anything that wants to read it).</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    ToastViewModel? _progressToast;

    /// <summary>
    /// Shows a message for a few seconds. "Couldn't…" messages are errors (and stay a little longer).
    /// <paramref name="sticky"/>: a step of something still going on; the next step replaces it, and the
    /// next ordinary message ends it.
    /// </summary>
    internal async void ShowNotice(string text, bool sticky = false)
    {
        Notice = text;
        var kind = text.Contains("couldn't", StringComparison.OrdinalIgnoreCase) || text.Contains("can't", StringComparison.OrdinalIgnoreCase)
            ? ToastKind.Error : ToastKind.Success;
        if (_progressToast is { } step)
        {
            if (sticky)
            {
                step.Text = text; // the next step of the same job
                return;
            }
            Toasts.Remove(step);
            _progressToast = null;
        }
        var toast = new ToastViewModel(text, sticky ? ToastKind.Info : kind, sticky);
        if (sticky)
            _progressToast = toast;
        Toasts.Add(toast);
        while (Toasts.Count > 4)
            Toasts.RemoveAt(0);
        if (sticky)
            return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(kind == ToastKind.Error ? 7 : 3.5), _lifetime.Token);
            Toasts.Remove(toast);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    void DismissToast(ToastViewModel toast)
    {
        Toasts.Remove(toast);
        if (ReferenceEquals(toast, _progressToast))
            _progressToast = null;
    }

    // ---- Viewer ----

    public void OpenViewer(AssetCellViewModel cell)
    {
        if (cell.Asset is null)
            return;
        if (_pick is not null)
        {
            if (cell.Asset.IsImage)
                _pick(cell);
            return;
        }
        Viewer = new PhotoViewerViewModel(this, cell.Bucket, cell.Index, cell.Asset, cell.Thumbnail);
    }

    internal void CloseViewer() => Viewer = null;

    /// <summary>Opens the photos of a stack (RAW+JPEG, a burst…) as their own page.</summary>
    internal void OpenStack(TimelineAsset asset)
    {
        if (asset.StackId is not { } id)
            return;
        OpenDrill(new GridQuery(Tr.T("Stack of {0}", asset.StackCount),
            async (client, ct) => BucketState.FromAssets(await client.GetStackAsync(id, ct))));
    }

    /// <summary>Finds the asset before/after a position, crossing into neighbouring groups.</summary>
    internal async Task<(BucketState Bucket, int Index, TimelineAsset Asset)?> StepAsync(BucketState bucket, int index, int delta, CancellationToken ct)
    {
        var assets = await bucket.GetAssetsAsync(ct);
        var next = index + delta;
        if (next >= 0 && next < assets.Count)
            return (bucket, next, assets[next]);

        if (bucket.Position >= _buckets.Count || !ReferenceEquals(_buckets[bucket.Position], bucket))
            return null; // a stand-alone set of photos (duplicates, suggestions): the ends are the ends
        var neighbour = bucket.Position + delta;
        while (neighbour >= 0 && neighbour < _buckets.Count)
        {
            var other = _buckets[neighbour];
            var otherAssets = await other.GetAssetsAsync(ct);
            if (otherAssets.Count > 0)
            {
                var i = delta > 0 ? 0 : otherAssets.Count - 1;
                return (other, i, otherAssets[i]);
            }
            neighbour += delta;
        }
        return null;
    }

    public void Dispose()
    {
        CommitRecentAlbum();
        _lifetime.Cancel();
        ReleaseRows();
        foreach (var card in Albums.Cast<LibraryCardViewModel>().Concat(People).Concat(Places))
            card.Dispose();
        Duplicates?.Dispose();
        foreach (var card in _memoryCards)
            card.Dispose();
        Privacy?.Dispose();
        if (_privacyHooked)
            AppServices.PrivacyScan.StatusChanged -= OnPrivacyStatus;
        Thumbnails?.Dispose();
    }
}
