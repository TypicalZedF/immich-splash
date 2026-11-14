using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Styling;
using ImmichSplash.App.Services;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.Controls;

/// <summary>
/// A slippy map of where your photos were taken, like the Immich web app's. Nearby photos group
/// into a bubble with a count; single photos show as round thumbnails. Groups are worked out on a
/// fixed grid for each zoom level, so they stay put while you pan and only change when you zoom.
/// Drag to pan (it glides on after a flick), scroll to zoom (smoothly), double-click to zoom in,
/// click a bubble or photo to see those photos. Optionally a heat map shows where most photos are.
/// </summary>
public sealed class MapView : Control
{
    public static readonly StyledProperty<IReadOnlyList<MapMarker>?> MarkersProperty =
        AvaloniaProperty.Register<MapView, IReadOnlyList<MapMarker>?>(nameof(Markers));

    /// <summary>Draw online street detail over the built-in map (off: nothing is fetched from map services).</summary>
    public static readonly StyledProperty<bool> ShowStreetDetailProperty =
        AvaloniaProperty.Register<MapView, bool>(nameof(ShowStreetDetail));

    /// <summary>Follow the app's dark theme (off: always the light map).</summary>
    public static readonly StyledProperty<bool> AllowDarkProperty =
        AvaloniaProperty.Register<MapView, bool>(nameof(AllowDark), true);

    /// <summary>Show a heat map of where photos are instead of bubbles.</summary>
    public static readonly StyledProperty<bool> HeatMapProperty =
        AvaloniaProperty.Register<MapView, bool>(nameof(HeatMap));

    const double TileSize = 256;
    const double MinZoom = 1.5;
    const double MaxZoom = 18;
    const int MaxClusterLevel = 18;
    const double ClusterCellPx = 60;    // grid cell size, in screen pixels at the level's zoom
    const double ThumbRadius = 21;
    const int MaxThumbnails = 400;

    // The built-in map's colors, matching the app's light and dark themes.
    static readonly IBrush Water = new SolidColorBrush(Color.Parse("#D6E4F0"));
    static readonly IBrush WaterDark = new SolidColorBrush(Color.Parse("#141A22"));
    static readonly IBrush LandBrush = new SolidColorBrush(Color.Parse("#F5F5F2"));
    static readonly IBrush LandDark = new SolidColorBrush(Color.Parse("#262A30"));
    static readonly IBrush AttributionLight = new SolidColorBrush(Color.Parse("#B3FFFFFF"));
    static readonly IBrush AttributionDark = new SolidColorBrush(Color.Parse("#99000000"));
    static readonly Color Coast = Color.Parse("#B8C4D0");
    static readonly Color CoastDark = Color.Parse("#3A4250");
    static readonly Color Border = Color.Parse("#B9BEC7");
    static readonly Color BorderDark = Color.Parse("#4A5260");
    static readonly Color State = Color.Parse("#D3D7DD");
    static readonly Color StateDark = Color.Parse("#363C46");
    static readonly Color River = Color.Parse("#C2D6E8");
    static readonly Color RiverDark = Color.Parse("#1E2836");
    static readonly Typeface CountFont = new("Google Sans", FontStyle.Normal, FontWeight.SemiBold);
    static readonly IBrush AccentLight = new SolidColorBrush(Color.Parse("#4250AF"));
    static readonly IBrush AccentDark = new SolidColorBrush(Color.Parse("#ACCBFA"));
    static readonly IPen RingLight = new Pen(Brushes.White, 2);
    static readonly IPen RingDark = new Pen(new SolidColorBrush(Color.Parse("#1B1D22")), 2);
    static readonly IPen ThumbRing = new Pen(Brushes.White, 2.5);

    MapTiles? _tiles;
    (double X, double Y)[] _points = []; // markers projected to 0..1 world coordinates
    double _zoom = 2, _centerX = 0.5, _centerY = 0.45;
    bool _userMoved; // until you move the map, it keeps fitting your photos as it resizes

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public IReadOnlyList<MapMarker>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public bool ShowStreetDetail
    {
        get => GetValue(ShowStreetDetailProperty);
        set => SetValue(ShowStreetDetailProperty, value);
    }

    public bool AllowDark
    {
        get => GetValue(AllowDarkProperty);
        set => SetValue(AllowDarkProperty, value);
    }

    public bool HeatMap
    {
        get => GetValue(HeatMapProperty);
        set => SetValue(HeatMapProperty, value);
    }

    /// <summary>Loads a photo's thumbnail for the round markers (set by the page).</summary>
    public Func<string, CancellationToken, Task<Bitmap?>>? ThumbnailProvider { get; set; }

    /// <summary>A bubble or photo was clicked: these are its photos.</summary>
    public event Action<IReadOnlyList<MapMarker>>? ClusterOpened;

    /// <summary>The visible area changed (for "Show photos in this area").</summary>
    public event Action? ViewChanged;

    bool IsDark => AllowDark && ActualThemeVariant == ThemeVariant.Dark;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tiles = new MapTiles();
        _tiles.TileLoaded += InvalidateVisual;
        _thumbLife = new CancellationTokenSource();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _rebuildTimer?.Stop();
        _base?.Dispose();
        _base = null;
        _heat?.Dispose();
        _heat = null;
        _tiles?.Dispose();
        _tiles = null;
        _thumbLife?.Cancel();
        ClearThumbnails();
        _animating = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkersProperty)
        {
            _points = (Markers ?? []).Select(m => Project(m.Lat, m.Lon)).ToArray();
            _levels.Clear();
            _heat?.Dispose();
            _heat = null;
            ClearThumbnails();
            _userMoved = false;
            FitToMarkers();
        }
        else if (change.Property == BoundsProperty && !_userMoved)
        {
            FitToMarkers();
        }
        else if (change.Property == ShowStreetDetailProperty || change.Property == HeatMapProperty || change.Property == AllowDarkProperty)
        {
            InvalidateVisual();
        }
    }

    static (double X, double Y) Project(double lat, double lon)
    {
        lat = Math.Clamp(lat, -85.05112878, 85.05112878);
        var sin = Math.Sin(lat * Math.PI / 180);
        return ((lon + 180) / 360, 0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI));
    }

    double WorldSize => TileSize * Math.Pow(2, _zoom);

    Point ToScreen(double x, double y) =>
        new((x - _centerX) * WorldSize + Bounds.Width / 2, (y - _centerY) * WorldSize + Bounds.Height / 2);

    (double X, double Y) ToWorld(Point p) =>
        (_centerX + (p.X - Bounds.Width / 2) / WorldSize, _centerY + (p.Y - Bounds.Height / 2) / WorldSize);

    /// <summary>Screen position of a world point, using the copy of the world closest to the center.</summary>
    Point ScreenOfWorld(double x, double y)
    {
        var dx = x - _centerX;
        dx -= Math.Round(dx);
        return new Point(dx * WorldSize + Bounds.Width / 2, (y - _centerY) * WorldSize + Bounds.Height / 2);
    }

    Point ScreenOf(int i) => ScreenOfWorld(_points[i].X, _points[i].Y);

    /// <summary>Zooms to show where most photos are (ignoring a few far-away outliers).</summary>
    public void FitToMarkers()
    {
        StopAnimation();
        if (_points.Length == 0 || Bounds.Width <= 0)
        {
            InvalidateVisual();
            return;
        }
        var xs = _points.Select(p => p.X).Order().ToArray();
        var ys = _points.Select(p => p.Y).Order().ToArray();
        var lo = (int)(xs.Length * 0.02);
        var hi = Math.Max(lo, xs.Length - 1 - lo);
        double minX = xs[lo], maxX = xs[hi], minY = ys[lo], maxY = ys[hi];
        _centerX = (minX + maxX) / 2;
        _centerY = (minY + maxY) / 2;
        var spanX = Math.Max(maxX - minX, 1e-6) * TileSize;
        var spanY = Math.Max(maxY - minY, 1e-6) * TileSize;
        var zoom = Math.Log2(Math.Min(Bounds.Width * 0.75 / spanX, Bounds.Height * 0.7 / spanY));
        _zoom = Math.Clamp(zoom, MinZoom, 13);
        ClampCenter();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Shows a place at a zoom level (used by the screenshot tool).</summary>
    public void CenterOn(double lat, double lon, double zoom)
    {
        StopAnimation();
        (_centerX, _centerY) = Project(lat, lon);
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _userMoved = true;
        ClampCenter();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Zoom buttons: an animated step around the middle.</summary>
    public void ZoomBy(double factor) => AnimateZoom(_targetZoom ?? _zoom, Math.Log2(factor), new Point(Bounds.Width / 2, Bounds.Height / 2));

    void ZoomAt(double zoom, Point anchor)
    {
        _userMoved = true;
        var (wx, wy) = ToWorld(anchor);
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        // Keep the point under the pointer still.
        _centerX = wx - (anchor.X - Bounds.Width / 2) / WorldSize;
        _centerY = wy - (anchor.Y - Bounds.Height / 2) / WorldSize;
        ClampCenter();
        InvalidateVisual();
    }

    void ClampCenter()
    {
        _centerX = ((_centerX % 1) + 1) % 1;
        var half = Bounds.Height / 2 / WorldSize;
        _centerY = half >= 0.5 ? 0.5 : Math.Clamp(_centerY, half, 1 - half);
    }

    /// <summary>Photos currently on screen.</summary>
    public IReadOnlyList<MapMarker> VisibleMarkers()
    {
        var markers = Markers ?? [];
        var result = new List<MapMarker>();
        for (var i = 0; i < _points.Length; i++)
        {
            var p = ScreenOf(i);
            if (p.X >= 0 && p.Y >= 0 && p.X <= Bounds.Width && p.Y <= Bounds.Height)
                result.Add(markers[i]);
        }
        return result;
    }

    // ---- Smooth zooming and gliding ----
    // Wheel and button zooms ease towards their target instead of jumping; a flick keeps the map
    // moving and slows it down. Both run once per screen refresh.

    double? _targetZoom;
    Point _zoomAnchor;
    Vector _velocity;         // pixels per second, for gliding after a drag
    bool _animating;
    TimeSpan? _lastFrame;

    void AnimateZoom(double from, double delta, Point anchor)
    {
        _velocity = default;
        _targetZoom = Math.Clamp(from + delta, MinZoom, MaxZoom);
        _zoomAnchor = anchor;
        StartAnimation();
    }

    void StartAnimation()
    {
        if (_animating)
            return;
        _animating = true;
        _lastFrame = null;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    void StopAnimation()
    {
        _animating = false;
        _targetZoom = null;
        _velocity = default;
    }

    void OnFrame(TimeSpan time)
    {
        if (!_animating)
            return;
        var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0.001, 0.05) : 1 / 60.0;
        _lastFrame = time;
        var moving = false;
        if (_targetZoom is { } target)
        {
            var diff = target - _zoom;
            if (Math.Abs(diff) < 0.003)
            {
                ZoomAt(target, _zoomAnchor);
                _targetZoom = null;
            }
            else
            {
                // Ease out: cover a fixed share of the remaining way each frame (independent of frame rate).
                ZoomAt(_zoom + diff * (1 - Math.Pow(0.0005, dt)), _zoomAnchor);
                moving = true;
            }
        }
        if (_velocity.Length > 20)
        {
            _centerX -= _velocity.X * dt / WorldSize;
            _centerY -= _velocity.Y * dt / WorldSize;
            ClampCenter();
            _velocity *= Math.Pow(0.04, dt); // friction
            InvalidateVisual();
            moving = true;
        }
        else
        {
            _velocity = default;
        }
        if (moving)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
            return;
        }
        _animating = false;
        ViewChanged?.Invoke();
    }

    // ---- The built-in map, cached ----
    // Drawing ~120k points of coastline, borders and rivers every frame made dragging laggy. Instead
    // the map is drawn once into an image a bit larger than the screen; panning just slides it, and
    // zooming scales it until the pointer stops, then it's redrawn sharp.

    /// <summary>Extra room drawn around the screen, so short drags need no redraw.</summary>
    const double CacheMargin = 0.35;

    RenderTargetBitmap? _base;
    double _baseZoom, _baseCx, _baseCy, _baseScaling;
    Size _baseSize;
    bool _baseDark;
    DispatcherTimer? _rebuildTimer;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 1 || bounds.Height < 1)
            return;
        var dark = IsDark;
        context.FillRectangle(dark ? WaterDark : Water, bounds);
        DrawBaseMap(context, dark);
        var street = ShowStreetDetail && _tiles is { AllFailed: false };
        if (street)
            DrawTiles(context, dark);
        if (HeatMap)
            DrawHeat(context);
        DrawClusters(context, dark);

        // Credit the map data, as its licence asks.
        var credit = street ? _tiles!.Provider.Attribution : WorldMap.Attribution;
        var text = new FormattedText(credit, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5,
            dark ? Brushes.White : Brushes.Black);
        var box = new Rect(Bounds.Width - text.Width - 12, Bounds.Height - text.Height - 6, text.Width + 12, text.Height + 6);
        context.FillRectangle(dark ? AttributionDark : AttributionLight, box);
        context.DrawText(text, new Point(box.X + 6, box.Y + 3));
    }

    void DrawBaseMap(DrawingContext context, bool dark)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var valid = _base is not null && _baseDark == dark && _baseSize == Bounds.Size && _baseScaling == scaling;
        if (valid)
        {
            // Where the cached image's middle is now, and how much it's been scaled since.
            var scale = Math.Pow(2, _zoom - _baseZoom);
            var dx = _baseCx - _centerX;
            dx -= Math.Round(dx);
            var middle = new Point(Bounds.Width / 2 + dx * WorldSize, Bounds.Height / 2 + (_baseCy - _centerY) * WorldSize);
            var w = Bounds.Width * (1 + 2 * CacheMargin) * scale;
            var h = Bounds.Height * (1 + 2 * CacheMargin) * scale;
            var dest = new Rect(middle.X - w / 2, middle.Y - h / 2, w, h);
            var covers = dest.Contains(new Rect(Bounds.Size));
            if (covers && Math.Abs(scale - 1) < 0.001)
            {
                context.DrawImage(_base!, new Rect(_base!.Size), dest);
                return;
            }
            if (covers || Math.Abs(scale - 1) >= 0.001)
            {
                // Zooming (or about to run off the edge): show the scaled image now, redraw sharp shortly.
                context.DrawImage(_base!, new Rect(_base!.Size), dest);
                ScheduleRebuild();
                if (covers)
                    return;
            }
        }
        RebuildBase(dark, scaling);
        context.DrawImage(_base!, new Rect(_base!.Size),
            new Rect(-Bounds.Width * CacheMargin, -Bounds.Height * CacheMargin,
                Bounds.Width * (1 + 2 * CacheMargin), Bounds.Height * (1 + 2 * CacheMargin)));
    }

    void ScheduleRebuild()
    {
        _rebuildTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background, (_, _) =>
        {
            _rebuildTimer!.Stop();
            _base?.Dispose();
            _base = null;
            _heat?.Dispose();
            _heat = null;
            InvalidateVisual();
        });
        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    }

    void RebuildBase(bool dark, double scaling)
    {
        _rebuildTimer?.Stop();
        var width = Bounds.Width * (1 + 2 * CacheMargin);
        var height = Bounds.Height * (1 + 2 * CacheMargin);
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(width * scaling)), Math.Max(1, (int)Math.Ceiling(height * scaling)));
        if (_base is null || _base.PixelSize != pixels)
        {
            _base?.Dispose();
            // Plain pixels with the display scale applied below, rather than the bitmap's DPI: on Linux the
            // renderer drew such a bitmap at the wrong size, so the land drifted away from the photos.
            _base = new RenderTargetBitmap(pixels, new Vector(96, 96));
        }
        using (var ctx = _base.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateScale(pixels.Width / width, pixels.Height / height)))
        {
            ctx.FillRectangle(dark ? WaterDark : Water, new Rect(0, 0, width, height));
            DrawWorld(ctx, dark, width, height);
        }
        _baseZoom = _zoom;
        _baseCx = _centerX;
        _baseCy = _centerY;
        _baseDark = dark;
        _baseSize = Bounds.Size;
        _baseScaling = scaling;
    }

    /// <summary>
    /// The built-in map for an area of <paramref name="width"/> × <paramref name="height"/> around the
    /// current center, every copy of the world in it: land and coastlines, lakes, major rivers, state
    /// lines (once zoomed in a little) and country borders.
    /// </summary>
    void DrawWorld(DrawingContext context, bool dark, double width, double height)
    {
        if (WorldMap.Land is not { } land)
            return;
        var size = WorldSize;
        var origin = new Point((0 - _centerX) * size + width / 2, (0 - _centerY) * size + height / 2);
        // Lines stay the same width on screen whatever the zoom.
        Pen Line(Color color, double w) => new(new SolidColorBrush(color), w / size);
        var coast = Line(dark ? CoastDark : Coast, 1.2);
        var border = Line(dark ? BorderDark : Border, 1.0);
        var state = new Pen(new SolidColorBrush(dark ? StateDark : State), 0.9 / size, new DashStyle([3, 2], 0));
        var river = Line(dark ? RiverDark : River, 1.0);
        var water = dark ? WaterDark : Water;
        var showStates = _zoom >= 2.5;
        var showRivers = _zoom >= 3.5;
        // Shapes may reach a little past ±180°, so include a copy either side.
        var first = (int)Math.Floor(-origin.X / size) - 1;
        var last = (int)Math.Floor((width - origin.X) / size) + 1;
        var landFill = dark ? LandDark : LandBrush;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var copy = first; copy <= last; copy++)
        {
            // The part of this copy of the world that's on screen (in 0–1 map units), with room for line widths.
            var left = origin.X + copy * size;
            var view = new Rect(-left / size, -origin.Y / size, width / size, height / size).Inflate(4 / size);
            using (context.PushTransform(Matrix.CreateScale(size, size) * Matrix.CreateTranslation(left, origin.Y)))
            {
                Draw(land, landFill, coast);
                Draw(WorldMap.Lakes, water, coast);
                if (showRivers)
                    Draw(WorldMap.Rivers, null, river);
                if (showStates)
                    Draw(WorldMap.States, null, state);
                Draw(WorldMap.Borders, null, border);
            }

            void Draw(WorldMap.Layer? layer, IBrush? fill, IPen pen)
            {
                if (layer is null)
                    return;
                foreach (var (bounds, shape) in layer.Pieces)
                    if (bounds.Intersects(view))
                        context.DrawGeometry(fill, pen, shape);
            }
        }
        if (watch.ElapsedMilliseconds > 60)
            ImmichSplash.Core.Log.Info($"Map redraw took {watch.ElapsedMilliseconds} ms at zoom {_zoom:0.0}");
    }

    void DrawTiles(DrawingContext context, bool dark)
    {
        if (_tiles is null)
            return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        // Pick the level whose pixels best match the screen's (high-resolution tiles are 512px).
        var z = (int)Math.Clamp(Math.Round(_zoom + Math.Log2(scaling) - Math.Log2(_tiles.Provider.TilePixels / 256.0)), 0, 19);
        var n = 1 << z;
        var tileOnScreen = WorldSize / n;
        var origin = ToScreen(0, 0);
        var firstX = (int)Math.Floor(-origin.X / tileOnScreen);
        var firstY = Math.Max(0, (int)Math.Floor(-origin.Y / tileOnScreen));
        var lastX = (int)Math.Floor((Bounds.Width - origin.X) / tileOnScreen);
        var lastY = Math.Min(n - 1, (int)Math.Floor((Bounds.Height - origin.Y) / tileOnScreen));
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
        {
            for (var ty = firstY; ty <= lastY; ty++)
            {
                for (var tx = firstX; tx <= lastX; tx++)
                {
                    var wrapped = ((tx % n) + n) % n;
                    var dest = new Rect(origin.X + tx * tileOnScreen, origin.Y + ty * tileOnScreen, tileOnScreen + 0.5, tileOnScreen + 0.5);
                    if (_tiles.Get(z, wrapped, ty, dark) is { } tile)
                        context.DrawImage(tile, new Rect(tile.Size), dest);
                }
            }
        }
    }

    // ---- Groups of photos, stable per zoom level ----
    // Each zoom level has a fixed grid laid over the whole world; photos in the same cell form a
    // group (neighbouring groups that would overlap are folded together). Because the grid belongs
    // to the world, not the screen, panning never changes the groups.

    sealed class Cluster
    {
        public double X, Y;            // middle of the group, world coordinates
        public int[] Members = [];
        public double CellX, CellY;    // for deterministic folding
    }

    readonly Dictionary<int, Cluster[]> _levels = [];
    readonly List<(Cluster Cluster, Point At, double Radius)> _visible = [];

    int Level => Math.Clamp((int)Math.Round(_zoom), 0, MaxClusterLevel);

    Cluster[] ClustersAt(int level)
    {
        if (_levels.TryGetValue(level, out var cached))
            return cached;
        var cell = ClusterCellPx / (TileSize * Math.Pow(2, level));
        var cells = new Dictionary<(long, long), (double SumX, double SumY, List<int> Members)>();
        for (var i = 0; i < _points.Length; i++)
        {
            var (x, y) = _points[i];
            var key = ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell));
            if (!cells.TryGetValue(key, out var c))
                c = (0, 0, []);
            c.Members.Add(i);
            cells[key] = (c.SumX + x, c.SumY + y, c.Members);
        }
        // Fold groups whose middles ended up close (photos either side of a cell edge), biggest first.
        var groups = cells
            .Select(kv => new Cluster { X = kv.Value.SumX / kv.Value.Members.Count, Y = kv.Value.SumY / kv.Value.Members.Count, Members = [.. kv.Value.Members], CellX = kv.Key.Item1, CellY = kv.Key.Item2 })
            .OrderByDescending(c => c.Members.Length).ThenBy(c => c.CellX).ThenBy(c => c.CellY)
            .ToList();
        var placed = new Dictionary<(long, long), List<Cluster>>();
        var result = new List<Cluster>();
        var reach = cell * 0.75;
        foreach (var g in groups)
        {
            var key = ((long)Math.Floor(g.X / cell), (long)Math.Floor(g.Y / cell));
            Cluster? into = null;
            for (var dx = -1; dx <= 1 && into is null; dx++)
                for (var dy = -1; dy <= 1 && into is null; dy++)
                    if (placed.TryGetValue((key.Item1 + dx, key.Item2 + dy), out var near))
                        into = near.FirstOrDefault(n => Math.Abs(n.X - g.X) < reach && Math.Abs(n.Y - g.Y) < reach);
            if (into is null)
            {
                result.Add(g);
                if (!placed.TryGetValue(key, out var list))
                    placed[key] = list = [];
                list.Add(g);
                continue;
            }
            var total = into.Members.Length + g.Members.Length;
            into.X = (into.X * into.Members.Length + g.X * g.Members.Length) / total;
            into.Y = (into.Y * into.Members.Length + g.Y * g.Members.Length) / total;
            into.Members = [.. into.Members, .. g.Members];
        }
        var array = result.ToArray();
        _levels[level] = array;
        return array;
    }

    readonly Dictionary<string, FormattedText> _labels = [];

    FormattedText Label(int count, bool dark)
    {
        var text = count >= 10000 ? $"{count / 1000.0:0}k" : count.ToString("N0", CultureInfo.CurrentCulture);
        var key = (dark ? "d" : "l") + text;
        if (!_labels.TryGetValue(key, out var label))
            _labels[key] = label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CountFont, 12,
                dark ? Brushes.Black : Brushes.White);
        return label;
    }

    void DrawClusters(DrawingContext context, bool dark)
    {
        _visible.Clear();
        if (_points.Length == 0)
            return;
        var markers = Markers ?? [];
        var accent = dark ? AccentDark : AccentLight;
        var ring = dark ? RingDark : RingLight;
        var wanted = 0;
        foreach (var c in ClustersAt(Level))
        {
            var at = ScreenOfWorld(c.X, c.Y);
            if (at.X < -40 || at.Y < -40 || at.X > Bounds.Width + 40 || at.Y > Bounds.Height + 40)
                continue;
            if (c.Members.Length == 1)
            {
                // One photo: its thumbnail, like the web app (a dot until it has loaded).
                var id = markers[c.Members[0]].Id;
                _visible.Add((c, at, ThumbRadius));
                if (Thumbnail(id, ref wanted) is { } thumb)
                {
                    var rect = new Rect(at.X - ThumbRadius - 2, at.Y - ThumbRadius - 2, ThumbRadius * 2 + 4, ThumbRadius * 2 + 4);
                    context.DrawImage(thumb, new Rect(thumb.Size), rect);
                }
                else
                {
                    context.DrawEllipse(accent, ring, at, 8, 8);
                }
                continue;
            }
            var label = Label(c.Members.Length, dark);
            var radius = Math.Max(14 + 2.5 * Math.Log10(c.Members.Length), label.Width / 2 + 8);
            _visible.Add((c, at, radius));
            // Over the heat map the bubbles are see-through, so the colours under them still show.
            using (HeatMap ? context.PushOpacity(0.72) : default(DrawingContext.PushedState?))
            {
                context.DrawEllipse(accent, ring, at, radius, radius);
                context.DrawText(label, new Point(at.X - label.Width / 2, at.Y - label.Height / 2));
            }
        }
    }

    // ---- Thumbnails for single photos ----

    readonly Dictionary<string, LinkedListNode<(string Id, Bitmap Bitmap)>> _thumbs = [];
    readonly LinkedList<(string Id, Bitmap Bitmap)> _thumbOrder = new();
    readonly HashSet<string> _thumbPending = [];
    CancellationTokenSource? _thumbLife;

    Bitmap? Thumbnail(string id, ref int wanted)
    {
        if (_thumbs.TryGetValue(id, out var node))
        {
            _thumbOrder.Remove(node);
            _thumbOrder.AddFirst(node);
            return node.Value.Bitmap;
        }
        // Don't flood the server: a handful of new ones per frame.
        if (ThumbnailProvider is { } load && _thumbLife is { } life && wanted < 24 && _thumbPending.Add(id))
        {
            wanted++;
            _ = LoadThumbnailAsync(load, id, life.Token);
        }
        return null;
    }

    async Task LoadThumbnailAsync(Func<string, CancellationToken, Task<Bitmap?>> load, string id, CancellationToken ct)
    {
        try
        {
            var loaded = await load(id, ct);
            if (loaded is null || ct.IsCancellationRequested)
            {
                loaded?.Dispose();
                return;
            }
            var bitmap = RoundThumbnail(loaded);
            loaded.Dispose();
            _thumbs[id] = _thumbOrder.AddFirst((id, bitmap));
            while (_thumbOrder.Count > MaxThumbnails && _thumbOrder.Last is { } oldest)
            {
                _thumbOrder.RemoveLast();
                _thumbs.Remove(oldest.Value.Id);
                oldest.Value.Bitmap.Dispose();
            }
            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ImmichSplash.Core.Log.Info($"Map thumbnail {id}: {ex.Message}");
        }
        finally
        {
            _thumbPending.Remove(id);
        }
    }

    /// <summary>The photo cut into a circle with its white ring, at screen resolution, ready to draw as is.</summary>
    Bitmap RoundThumbnail(Bitmap photo)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var outer = ThumbRadius + 2;
        var px = (int)Math.Ceiling(outer * 2 * scaling);
        var round = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96, 96));
        using (var ctx = round.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateScale(px / (outer * 2), px / (outer * 2))))
        {
            var middle = new Point(outer, outer);
            var rect = new Rect(outer - ThumbRadius, outer - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2);
            using (ctx.PushGeometryClip(new EllipseGeometry(rect)))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                var side = Math.Min(photo.Size.Width, photo.Size.Height);
                var src = new Rect((photo.Size.Width - side) / 2, (photo.Size.Height - side) / 2, side, side);
                ctx.DrawImage(photo, src, rect);
            }
            ctx.DrawEllipse(null, ThumbRing, middle, ThumbRadius, ThumbRadius);
        }
        return round;
    }

    void ClearThumbnails()
    {
        foreach (var (_, bitmap) in _thumbOrder)
            bitmap.Dispose();
        _thumbOrder.Clear();
        _thumbs.Clear();
        _thumbPending.Clear();
    }

    // ---- Heat map ----
    // Every photo adds a soft spot to a grid a quarter of the screen's resolution; the total is
    // coloured from blue (a few photos) to red (lots). Like the base map, it's drawn a bit larger
    // than the screen and slid while you pan, then redrawn once you stop.

    const double HeatMargin = 0.25;
    const int HeatScale = 4;
    WriteableBitmap? _heat;
    double _heatZoom, _heatCx, _heatCy;
    Size _heatSize;
    static readonly float[] HeatKernel = MakeKernel(HeatRadius, 3.2);
    const int HeatRadius = 10;

    static float[] MakeKernel(int r, double sigma)
    {
        var size = 2 * r + 1;
        var k = new float[size * size];
        for (var y = -r; y <= r; y++)
            for (var x = -r; x <= r; x++)
                k[(y + r) * size + x + r] = (float)Math.Exp(-(x * x + y * y) / (2 * sigma * sigma));
        return k;
    }

    void DrawHeat(DrawingContext context)
    {
        if (_points.Length == 0)
            return;
        var width = Bounds.Width * (1 + 2 * HeatMargin);
        var height = Bounds.Height * (1 + 2 * HeatMargin);
        if (_heat is not null && _heatSize == Bounds.Size)
        {
            var scale = Math.Pow(2, _zoom - _heatZoom);
            var dx = _heatCx - _centerX;
            dx -= Math.Round(dx);
            var middle = new Point(Bounds.Width / 2 + dx * WorldSize, Bounds.Height / 2 + (_heatCy - _centerY) * WorldSize);
            var dest = new Rect(middle.X - width * scale / 2, middle.Y - height * scale / 2, width * scale, height * scale);
            var covers = dest.Contains(new Rect(Bounds.Size));
            if (covers)
            {
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    context.DrawImage(_heat, new Rect(_heat.Size), dest);
                if (Math.Abs(scale - 1) >= 0.001)
                    ScheduleRebuild();
                return;
            }
        }
        BuildHeat(width, height);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(_heat!, new Rect(_heat!.Size), new Rect(-Bounds.Width * HeatMargin, -Bounds.Height * HeatMargin, width, height));
    }

    unsafe void BuildHeat(double width, double height)
    {
        var w = Math.Max(1, (int)Math.Ceiling(width / HeatScale));
        var h = Math.Max(1, (int)Math.Ceiling(height / HeatScale));
        var grid = new float[w * h];
        const int r = HeatRadius;
        var size = 2 * r + 1;
        var offsetX = Bounds.Width * HeatMargin;
        var offsetY = Bounds.Height * HeatMargin;
        for (var i = 0; i < _points.Length; i++)
        {
            var p = ScreenOf(i);
            var cx = (int)((p.X + offsetX) / HeatScale);
            var cy = (int)((p.Y + offsetY) / HeatScale);
            if (cx < -r || cy < -r || cx >= w + r || cy >= h + r)
                continue;
            for (var ky = -r; ky <= r; ky++)
            {
                var y = cy + ky;
                if (y < 0 || y >= h)
                    continue;
                var row = y * w;
                var krow = (ky + r) * size + r;
                for (var kx = -r; kx <= r; kx++)
                {
                    var x = cx + kx;
                    if (x >= 0 && x < w)
                        grid[row + x] += HeatKernel[krow + kx];
                }
            }
        }
        // Scale so a few very dense spots don't wash out the rest.
        var values = grid.Where(v => v > 0.05f).ToArray();
        Array.Sort(values);
        var top = values.Length == 0 ? 1f : Math.Max(1f, values[(int)(values.Length * 0.98)]);
        var logTop = MathF.Log(1 + top);

        _heat?.Dispose();
        _heat = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = _heat.Lock())
        {
            for (var y = 0; y < h; y++)
            {
                var line = (uint*)((byte*)fb.Address + y * fb.RowBytes);
                for (var x = 0; x < w; x++)
                {
                    var v = grid[y * w + x];
                    line[x] = v <= 0.02f ? 0u : HeatColor(Math.Min(1f, MathF.Log(1 + v) / logTop));
                }
            }
        }
        _heatZoom = _zoom;
        _heatCx = _centerX;
        _heatCy = _centerY;
        _heatSize = Bounds.Size;
    }

    // Blue → cyan → green → yellow → red, more opaque where denser.
    static readonly (float At, byte R, byte G, byte B, float A)[] HeatStops =
    [
        (0f, 40, 90, 255, 0f), (0.12f, 40, 110, 255, 0.35f), (0.35f, 0, 200, 255, 0.55f),
        (0.55f, 40, 220, 100, 0.65f), (0.75f, 255, 215, 0, 0.75f), (1f, 255, 45, 30, 0.85f),
    ];

    static uint HeatColor(float v)
    {
        var i = 1;
        while (i < HeatStops.Length - 1 && v > HeatStops[i].At)
            i++;
        var (a0, r0, g0, b0, al0) = HeatStops[i - 1];
        var (a1, r1, g1, b1, al1) = HeatStops[i];
        var t = Math.Clamp((v - a0) / Math.Max(1e-6f, a1 - a0), 0f, 1f);
        var alpha = al0 + (al1 - al0) * t;
        byte Mix(byte c0, byte c1) => (byte)((c0 + (c1 - c0) * t) * alpha); // premultiplied
        return ((uint)(alpha * 255) << 24) | ((uint)Mix(r0, r1) << 16) | ((uint)Mix(g0, g1) << 8) | Mix(b0, b1);
    }

    // ---- Mouse and touch ----
    // One finger works like the mouse (drag, flick, tap, double-tap); two fingers pinch to zoom and move the map together.

    Point? _pressAt, _lastDrag;
    bool _dragged, _touchPress;
    DateTime _lastMove;
    readonly Dictionary<IPointer, Point> _touches = [];
    double _pinchDistance;
    Point _pinchMid;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        StopAnimation();
        var p = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
        _touchPress = e.Pointer.Type == PointerType.Touch;
        if (_touchPress)
        {
            _touches[e.Pointer] = p;
            if (_touches.Count >= 2)
            {
                StartPinch();
                return;
            }
        }
        _pressAt = _lastDrag = p;
        _lastMove = DateTime.UtcNow;
        _dragged = false;
        if (e.ClickCount == 2)
        {
            AnimateZoom(_zoom, 1, p);
            _pressAt = null;
        }
    }

    void StartPinch()
    {
        var (a, b) = PinchFingers();
        _pinchDistance = Math.Max(1, Distance(a, b));
        _pinchMid = (a + b) / 2;
        _pressAt = _lastDrag = null; // no tap or glide from here
        _velocity = default;
    }

    (Point A, Point B) PinchFingers()
    {
        using var fingers = _touches.Values.GetEnumerator();
        fingers.MoveNext();
        var a = fingers.Current;
        fingers.MoveNext();
        return (a, fingers.Current);
    }

    static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_touches.ContainsKey(e.Pointer))
        {
            _touches[e.Pointer] = p;
            if (_touches.Count >= 2)
            {
                var (a, b) = PinchFingers();
                var distance = Math.Max(1, Distance(a, b));
                var mid = (a + b) / 2;
                // Move with the fingers, then zoom around the point between them.
                _centerX -= (mid.X - _pinchMid.X) / WorldSize;
                _centerY -= (mid.Y - _pinchMid.Y) / WorldSize;
                ZoomAt(_zoom + Math.Log2(distance / _pinchDistance), mid);
                _pinchDistance = distance;
                _pinchMid = mid;
                e.Handled = true;
                return;
            }
        }
        if (_lastDrag is { } last && _pressAt is { } start)
        {
            // Fingers wobble more than a mouse; a tap shouldn't turn into a drag.
            if (!_dragged && Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) > (_touchPress ? 12 : 4))
                _dragged = true;
            if (_dragged)
            {
                _userMoved = true;
                var now = DateTime.UtcNow;
                var dt = Math.Max(0.001, (now - _lastMove).TotalSeconds);
                var step = new Vector(p.X - last.X, p.Y - last.Y);
                // Smoothed speed, for gliding on after letting go.
                _velocity = _velocity * 0.6 + step / dt * 0.4;
                _lastMove = now;
                _centerX -= step.X / WorldSize;
                _centerY -= step.Y / WorldSize;
                ClampCenter();
                _lastDrag = p;
                InvalidateVisual();
            }
        }
        if (e.Pointer.Type != PointerType.Touch)
            Cursor = HitCluster(p, 3) is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        var wasPinching = _touches.Count >= 2;
        _touches.Remove(e.Pointer);
        e.Pointer.Capture(null);
        if (wasPinching)
        {
            if (_touches.Count == 1)
            {
                // One finger left on the map: carry on moving it from where it is, without a jump.
                _pressAt = _lastDrag = _touches.Values.First();
                _lastMove = DateTime.UtcNow;
                _dragged = true;
            }
            else if (_touches.Count >= 2)
                StartPinch();
            if (_touches.Count < 2)
                ViewChanged?.Invoke();
            return;
        }
        if (_pressAt is not null && !_dragged && HitCluster(p, _touchPress ? 14 : 3) is { } cluster)
        {
            ClusterOpened?.Invoke(cluster.Members.Select(i => (Markers ?? [])[i]).ToList());
        }
        else if (_dragged)
        {
            // A flick glides on; a drag that stopped before letting go doesn't.
            if ((DateTime.UtcNow - _lastMove).TotalMilliseconds < 60 && _velocity.Length > 300)
            {
                _velocity = new Vector(Math.Clamp(_velocity.X, -4000, 4000), Math.Clamp(_velocity.Y, -4000, 4000));
                StartAnimation();
            }
            else
            {
                _velocity = default;
                ViewChanged?.Invoke();
            }
        }
        _pressAt = _lastDrag = null;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _touches.Remove(e.Pointer);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        AnimateZoom(_targetZoom ?? _zoom, e.Delta.Y * 0.6, e.GetPosition(this));
        e.Handled = true;
    }

    /// <param name="slop">How far outside a marker still counts (more for a finger than a mouse).</param>
    Cluster? HitCluster(Point p, double slop)
    {
        Cluster? best = null;
        var bestDistance = double.MaxValue;
        foreach (var (c, at, radius) in _visible)
        {
            var d = Math.Sqrt((at.X - p.X) * (at.X - p.X) + (at.Y - p.Y) * (at.Y - p.Y));
            if (d <= radius + slop && d < bestDistance)
            {
                best = c;
                bestDistance = d;
            }
        }
        return best;
    }
}
