using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ImmichSplash.App.Controls;
using ImmichSplash.App.ViewModels;
using ImmichSplash.Core;

namespace ImmichSplash.App.Views;

public partial class PhotosView : UserControl
{
    readonly ListBox _grid;
    readonly TimelineScrubber _scrubber;
    readonly MapView _map;
    readonly Panel _stage;
    readonly Image _viewerImage;
    readonly Image _videoSurface;
    ScrollViewer? _scroll;
    PhotosViewModel? _vm;
    PhotoViewerViewModel? _viewer;

    // Viewer zoom: scale and offset of the photo inside the stage.
    double _zoom = 1;
    Vector _pan;
    Point? _panFrom;

    public PhotosView()
    {
        InitializeComponent();

        _grid = this.FindControl<ListBox>("Grid")!;
        _scrubber = this.FindControl<TimelineScrubber>("Scrubber")!;
        _map = this.FindControl<MapView>("Map")!;
        _stage = this.FindControl<Panel>("ViewerStage")!;
        _viewerImage = this.FindControl<Image>("ViewerImage")!;
        _videoSurface = this.FindControl<Image>("VideoSurface")!;

        // Rows load their month and thumbnails only while on screen, and let go when scrolled away.
        _grid.ContainerPrepared += (_, e) => (e.Container.DataContext as AssetRowViewModel)?.Realize();
        _grid.ContainerClearing += (_, e) => (e.Container.DataContext as AssetRowViewModel)?.Unrealize();
        _grid.SizeChanged += (_, e) => UpdateLayout(e.NewSize.Width);
        _grid.TemplateApplied += (_, _) => HookScroll();

        // The date rail scrolls the grid (and the wheel over it scrolls the photos, as you'd expect).
        _scrubber.SeekRequested += SeekTo;
        _scrubber.PointerWheelChanged += (_, e) =>
        {
            if (_scroll is null)
                return;
            _pinned = null;
            _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(_scroll.Offset.Y - e.Delta.Y * 120, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
            e.Handled = true;
        };

        // Map: zoom buttons, "show photos in this area", clicking a bubble.
        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => _map.ZoomBy(2);
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => _map.ZoomBy(0.5);
        this.FindControl<Button>("FitButton")!.Click += (_, _) => _map.FitToMarkers();
        this.FindControl<Button>("ShowAreaButton")!.Click += (_, _) => _vm?.ShowMapPhotos(_map.VisibleMarkers());
        _map.ClusterOpened += markers => _vm?.ShowMapPhotos(markers);
        // The description saves when you leave its box (Enter saves too).
        this.FindControl<TextBox>("DescriptionBox")!.LostFocus += (_, _) => _vm?.Viewer?.SaveDescriptionCommand.Execute(null);
        DispatcherTimer? settle = null;
        string? lastInView = null;
        _map.ViewChanged += () =>
        {
            settle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
            {
                settle!.Stop();
                var visible = _map.VisibleMarkers();
                UpdateAreaButton(visible.Count);
                // Same photos in view (a small nudge): keep the panel as it is instead of rebuilding it.
                var key = visible.Count == 0 ? "" : $"{visible.Count}:{visible[0].Id}:{visible[^1].Id}";
                if (key == lastInView)
                    return;
                lastInView = key;
                _vm?.OnMapViewChanged(visible);
            });
            settle.Stop();
            settle.Start();
        };
        // Single photos on the map show as round thumbnails.
        _map.ThumbnailProvider = (id, ct) => _vm?.Thumbnails is { } loader
            ? loader.LoadAsync(id, Core.Api.AssetImageSize.Thumbnail, 96, ct)
            : Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null);
        // The map's side panel is a small photo grid: rows load while on screen, like the main grid.
        var panel = this.FindControl<ListBox>("MapPanelGrid")!;
        panel.ContainerPrepared += (_, e) => (e.Container.DataContext as AssetRowViewModel)?.Realize();
        panel.ContainerClearing += (_, e) => (e.Container.DataContext as AssetRowViewModel)?.Unrealize();
        panel.SizeChanged += (_, e) =>
        {
            if (_vm is not null && e.NewSize.Width > 0)
                _vm.MapPanelWidth = e.NewSize.Width - 14; // room for the scrollbar
        };

        // While a video plays, the controls (and the mouse pointer) go away when the pointer rests, like in any player.
        var layer = this.FindControl<Panel>("ViewerLayer")!;
        layer.AddHandler(PointerMovedEvent, (_, _) => WakeVideoControls(), RoutingStrategies.Tunnel, handledEventsToo: true);
        layer.AddHandler(PointerPressedEvent, (_, _) => WakeVideoControls(), RoutingStrategies.Tunnel, handledEventsToo: true);
        // Playing video: the controls fade out soon after the pointer stops, and at once when it leaves the viewer.
        _controlsIdle = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(1400), Avalonia.Threading.DispatcherPriority.Background, (_, _) => HideVideoControls());
        layer.PointerExited += (_, _) => HideVideoControls();

        // The "..." button opens its menu on a click.
        var more = this.FindControl<Button>("ViewerMoreButton")!;
        more.Click += (_, _) => more.ContextMenu?.Open(more);

        // The video's progress bar: drag it, and see pictures from the video where you point.
        var seek = this.FindControl<VideoSeekBar>("VideoSeek")!;
        seek.ScrubStarted += f => _viewer?.ScrubStarted(f);
        seek.ScrubMoved += f => _viewer?.ScrubMoved(f);
        seek.ScrubCompleted += f => _viewer?.ScrubCompleted(f);
        seek.Hovered += (fraction, x) =>
        {
            _viewer?.HoverVideo(fraction);
            if (fraction is not null && this.FindControl<Border>("VideoPreview") is { } preview)
            {
                // Centred over the pointer, but inside the bar.
                var width = 176 + 8;
                // Sits just above the bar whatever shape the video is (tall or wide).
                var height = (this.FindControl<Border>("VideoPreviewBox")?.Height ?? 99) + 30; // picture + time + padding
                preview.Margin = new Thickness(Math.Clamp(x - width / 2, 0, Math.Max(0, seek.Bounds.Width - width)), -(height + 8), 0, 0);
            }
        };

        // Viewer: scroll to zoom, drag to move, double-click to toggle zoom.
        _stage.PointerWheelChanged += OnStageWheel;
        _stage.PointerPressed += OnStagePressed;
        _stage.PointerMoved += OnStageMoved;
        _stage.PointerReleased += (_, e) => { _panFrom = null; e.Pointer.Capture(null); };
        _stage.SizeChanged += (_, _) => UpdateViewerPixels();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    void HookScroll()
    {
        _scroll = _grid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is null)
            return;
        _scroll.ScrollChanged += (_, _) =>
        {
            UpdateScrubber();
            Realign();
        };
        _scroll.SizeChanged += (_, _) => UpdateScrubber();
        // Smooth wheel scrolling (and Ctrl + wheel to resize thumbnails), before the list jumps by itself.
        _grid.AddHandler(PointerWheelChangedEvent, OnGridWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Middle-click autoscroll, like a web browser.
        _grid.AddHandler(PointerPressedEvent, OnGridPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _grid.AddHandler(PointerMovedEvent, OnGridMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _grid.AddHandler(PointerReleasedEvent, OnGridReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // ---- Middle-click autoscroll ----
    // Click the wheel, then move the pointer up or down: the further from where you clicked, the
    // faster it scrolls. Click again (or let go after holding the wheel down and moving) to stop.

    Point? _autoAnchor;
    Point _autoPointer;
    DateTime _autoStarted;
    bool _autoHeld;
    TimeSpan? _autoFrame;

    void OnGridPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_grid);
        if (_autoAnchor is not null)
        {
            StopAutoScroll(e.Pointer);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsMiddleButtonPressed || _scroll is null)
            return;
        e.Handled = true;
        _pinned = null;
        _scrollTarget = null;
        _autoAnchor = _autoPointer = point.Position;
        _autoStarted = DateTime.UtcNow;
        _autoHeld = true;
        e.Pointer.Capture(_grid);
        _grid.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        _autoFrame = TimeSpan.Zero;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAutoScrollFrame);
    }

    void OnGridMoved(object? sender, PointerEventArgs e)
    {
        if (_autoAnchor is null)
            return;
        _autoPointer = e.GetPosition(_grid);
        e.Handled = true;
    }

    void OnGridReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_autoAnchor is not { } anchor || e.InitialPressMouseButton != MouseButton.Middle)
            return;
        e.Handled = true;
        // Held down and moved: letting go stops. A quick click keeps it going until the next click.
        if (_autoHeld && ((DateTime.UtcNow - _autoStarted).TotalMilliseconds > 350 || Math.Abs(_autoPointer.Y - anchor.Y) > 12))
            StopAutoScroll(e.Pointer);
        _autoHeld = false;
    }

    void StopAutoScroll(IPointer? pointer)
    {
        _autoAnchor = null;
        _autoFrame = null;
        _grid.Cursor = Cursor.Default;
        pointer?.Capture(null);
    }

    void OnAutoScrollFrame(TimeSpan time)
    {
        if (_autoAnchor is not { } anchor || _scroll is null)
        {
            _autoFrame = null;
            return;
        }
        var dt = _autoFrame is { } last && last > TimeSpan.Zero ? Math.Clamp((time - last).TotalSeconds, 0.001, 0.05) : 1 / 60.0;
        _autoFrame = time;
        var distance = _autoPointer.Y - anchor.Y;
        const double deadZone = 10;
        if (Math.Abs(distance) > deadZone)
        {
            // Speeds up faster than the distance: a little for reading, a lot for flying through years.
            var speed = Math.Sign(distance) * Math.Pow(Math.Abs(distance) - deadZone, 1.6) * 2.2; // pixels per second
            var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(_scroll.Offset.Y + speed * dt, 0, max));
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAutoScrollFrame);
    }

    // ---- Smooth scrolling ----
    // The wheel sets where to go; the grid glides there over a few frames instead of jumping.

    double? _scrollTarget;
    TimeSpan? _scrollFrame;

    void OnGridWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_scroll is null || _vm is null)
            return;
        if (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return; // sideways: e.g. the memories strip
        e.Handled = true;
        _pinned = null;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _vm.ThumbnailSize *= Math.Pow(1.12, e.Delta.Y);
            return;
        }
        var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var from = _scrollTarget ?? _scroll.Offset.Y;
        _scrollTarget = Math.Clamp(from - e.Delta.Y * 150, 0, max);
        if (_scrollFrame is null)
        {
            _scrollFrame = TimeSpan.Zero;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnScrollFrame);
        }
    }

    void OnScrollFrame(TimeSpan time)
    {
        if (_scroll is null || _scrollTarget is not { } target)
        {
            _scrollFrame = null;
            return;
        }
        var dt = _scrollFrame is { } last && last > TimeSpan.Zero ? Math.Clamp((time - last).TotalSeconds, 0.001, 0.05) : 1 / 60.0;
        _scrollFrame = time;
        var current = _scroll.Offset.Y;
        var diff = target - current;
        if (Math.Abs(diff) < 0.5)
        {
            _scroll.Offset = new Vector(_scroll.Offset.X, target);
            _scrollTarget = null;
            _scrollFrame = null;
            return;
        }
        // Ease out: a fixed share of the remaining distance each frame, whatever the frame rate.
        _scroll.Offset = new Vector(_scroll.Offset.X, current + diff * (1 - Math.Pow(0.0002, dt)));
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnScrollFrame);
    }

    /// <summary>The photo at the top of the grid, so resizing thumbnails keeps your place.</summary>
    (BucketState Bucket, int Index)? TopAnchor()
    {
        if (_scroll is null)
            return null;
        foreach (var container in _grid.GetRealizedContainers().OrderBy(c => _grid.IndexFromContainer(c)))
        {
            if (container.TranslatePoint(default, _scroll) is not { } top || top.Y + container.Bounds.Height <= 1)
                continue;
            if (container.DataContext is AssetRowViewModel row)
                return (row.Bucket, row.Start);
        }
        return null;
    }

    /// <summary>With the date rail showing, the grid's own scrollbar would just crowd it.</summary>
    void UpdateScrollBar(bool force = false)
    {
        var rail = _vm?.HasSections == true;
        if (rail == _railShown && !force)
            return; // (the rail's months change on every resize; only showing/hiding it matters here)
        _railShown = rail;
        ScrollViewer.SetVerticalScrollBarVisibility(_grid, rail ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _grid.Padding = new Thickness(0, 0, rail ? 2 : 12, 24);
        if (_grid.Bounds.Width > 0)
            UpdateLayout(_grid.Bounds.Width);
    }

    bool _railShown;

    void UpdateScrubber()
    {
        if (_scroll is not null && _vm is not null && CurrentOffset() is { } offset)
            _scrubber.SetScroll(offset, _vm.ContentHeight, _scroll.Viewport.Height);
    }

    /// <summary>
    /// The exact position in the grid, from the row actually at the top of the screen. (The scrollbar's
    /// numbers are estimates for rows that haven't been drawn yet, so they drift on long timelines.)
    /// </summary>
    double? CurrentOffset()
    {
        if (_vm is null || _scroll is null)
            return null;
        if (_scroll.Offset.Y <= 0.5)
            return 0;
        foreach (var container in _grid.GetRealizedContainers())
        {
            if (container.TranslatePoint(default, _scroll) is not { } top)
                continue;
            if (top.Y <= 0.5 && top.Y + container.Bounds.Height > 0.5)
                return _vm.RowOffset(_grid.IndexFromContainer(container)) - top.Y;
        }
        return null;
    }

    // ---- Keeping a position you asked for ----
    // The list only estimates the height of rows it hasn't drawn, so while rows around a seek target
    // are drawn and the estimates settle, the content would drift away from where the date rail put
    // it. For a moment after a seek (or any other jump), keep correcting to the exact position, until
    // you scroll yourself.

    double? _pinned;
    DateTime _pinUntil;
    bool _realigning;

    void Pin(double exactOffset)
    {
        _pinned = exactOffset;
        _pinUntil = DateTime.UtcNow.AddMilliseconds(1500);
    }

    void Realign()
    {
        if (_pinned is not { } pinned || _scroll is null || _vm is null || _realigning)
            return;
        if (DateTime.UtcNow > _pinUntil)
        {
            _pinned = null;
            return;
        }
        if (CurrentOffset() is not { } current)
            return;
        var error = pinned - current;
        if (Math.Abs(error) < 1)
            return;
        _realigning = true;
        try
        {
            var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(_scroll.Offset.Y + error, 0, max));
        }
        finally
        {
            _realigning = false;
        }
    }

    /// <summary>Scrolls so that an exact grid position is at the top: bring its row into view, then line it up.</summary>
    void SeekTo(double offset)
    {
        if (_vm is null || _scroll is null)
            return;
        Pin(offset);
        var (index, within) = _vm.RowAt(offset);
        _grid.ScrollIntoView(index);
        _grid.UpdateLayout();
        if (_grid.ContainerFromIndex(index) is { } container && container.TranslatePoint(default, _scroll) is { } top)
        {
            var target = _scroll.Offset.Y + top.Y + within;
            _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(target, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
        }
        UpdateScrubber();
    }

    void UpdateLayout(double width)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        // Leave room for the list padding (which also holds the scrollbar when there's no date rail).
        _vm?.SetLayout(width - _grid.Padding.Right, scaling);
    }

    void UpdateViewerPixels()
    {
        if (_vm is null)
            return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var longSide = Math.Max(_stage.Bounds.Width, _stage.Bounds.Height) * scaling;
        if (longSide > 0)
            _vm.ViewerPixels = (int)Math.Ceiling(longSide);
    }

    void UpdateAreaButton() => UpdateAreaButton(_map.VisibleMarkers().Count);

    void UpdateAreaButton(int count)
    {
        if (this.FindControl<TextBlock>("ShowAreaText") is not { } text)
            return;
        text.Text = count == 0 ? Tr.T("No photos in this area") : Tr.T("Show {0} in this area", Format.Plural(count, Tr.T("photo"), Tr.T("photos")));
        if (this.FindControl<Button>("ShowAreaButton") is { } button)
            button.IsEnabled = count > 0;
    }

    void OnScrollToRow(int index)
    {
        _scrollTarget = null;
        // After the new rows are laid out.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_vm is not null)
                SeekTo(_vm.RowOffset(index));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.ScrollToRowRequested -= OnScrollToRow;
        }
        _vm = DataContext as PhotosViewModel;
        if (_vm is null)
        {
            SetViewer(null);
            return;
        }
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.GetTopAnchor = TopAnchor;
        _vm.ScrollToRowRequested -= OnScrollToRow;
        _vm.ScrollToRowRequested += OnScrollToRow;
        SetViewer(_vm.Viewer); // a viewer may already be open (this view can be recreated)
        UpdateScrollBar(force: true);
        if (_grid.Bounds.Width > 0)
            UpdateLayout(_grid.Bounds.Width);
    }

    void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PhotosViewModel.Viewer):
                SetViewer(_vm?.Viewer);
                break;
            case nameof(PhotosViewModel.Rows):
                _scrollTarget = null;
                _scroll?.ScrollToHome();
                break;
            case nameof(PhotosViewModel.HasSections):
                UpdateScrollBar();
                break;
            case nameof(PhotosViewModel.Markers) or nameof(PhotosViewModel.ShowMap):
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAreaButton, Avalonia.Threading.DispatcherPriority.Background);
                break;
        }
    }

    void SetViewer(PhotoViewerViewModel? viewer)
    {
        if (_viewer is not null)
        {
            _viewer.PropertyChanged -= OnViewerChanged;
            _viewer.VideoFrameUpdated -= OnVideoFrame;
            _viewer.PreviewFrameUpdated -= OnPreviewFrame;
        }
        _viewer = viewer;
        ResetZoom();
        if (viewer is null)
            return;
        viewer.PropertyChanged += OnViewerChanged;
        viewer.VideoFrameUpdated += OnVideoFrame;
        viewer.PreviewFrameUpdated += OnPreviewFrame;
        UpdateViewerPixels();
        this.FindControl<Panel>("ViewerLayer")?.Focus();
    }

    /// <summary>The video bitmap is redrawn in place; tell the image to repaint.</summary>
    void OnVideoFrame() => _videoSurface.InvalidateVisual();

    /// <summary>The viewer's right-click menu: only for photos you can act on (not files only on this PC, not read-only reviews).</summary>
    void OnViewerMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is ContextMenu { DataContext: not ViewModels.PhotoViewerViewModel { CanEdit: true } })
            e.Cancel = true;
    }

    /// <summary>A tile's right-click menu: not for files only on this PC, nor while picking a profile picture.</summary>
    void OnTileMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is ContextMenu { DataContext: not ViewModels.AssetCellViewModel { HasMenu: true } })
            e.Cancel = true;
    }

    Avalonia.Threading.DispatcherTimer? _controlsIdle;

    void WakeVideoControls()
    {
        if (_viewer is null)
            return;
        _viewer.ControlsAwake = true;
        this.FindControl<Panel>("ViewerLayer")!.Cursor = null;
        _controlsIdle?.Stop();
        _controlsIdle?.Start();
    }

    void HideVideoControls()
    {
        _controlsIdle?.Stop();
        if (_viewer is null || !_viewer.IsVideoPlaying)
            return;
        // Not while the pointer is on the controls or the bar is being dragged: look again a moment later.
        if (_viewer.IsScrubbing || this.FindControl<Border>("VideoControls")?.IsPointerOver == true)
        {
            _controlsIdle?.Start();
            return;
        }
        _viewer.ControlsAwake = false;
        this.FindControl<Panel>("ViewerLayer")!.Cursor = new Cursor(StandardCursorType.None);
    }

    /// <summary>The seek bar's preview picture was redrawn in place: repaint it.</summary>
    void OnPreviewFrame() => this.FindControl<Image>("VideoPreviewImage")?.InvalidateVisual();

    /// <summary>Sizes the preview to the video's shape (wide or tall), at most 176 by 132 pixels.</summary>
    void FitPreviewBox()
    {
        if (_viewer?.PreviewFrame is not { } frame || frame.PixelSize.Height <= 0 || this.FindControl<Border>("VideoPreviewBox") is not { } box)
            return;
        var aspect = frame.PixelSize.Width / (double)frame.PixelSize.Height;
        var height = Math.Min(132, 176 / aspect);
        box.Height = Math.Round(height);
        box.Width = Math.Round(Math.Min(176, height * aspect));
        if (this.FindControl<Border>("VideoPreview") is { } preview)
            preview.Margin = new Thickness(preview.Margin.Left, -(box.Height + 38), 0, 0);
    }

    void OnViewerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoViewerViewModel.PreviewFrame))
            FitPreviewBox();
        if (e.PropertyName == nameof(PhotoViewerViewModel.Generation))
            ResetZoom();
    }

    // ---- Viewer zoom ----

    void ResetZoom()
    {
        _zoom = 1;
        _pan = default;
        ApplyZoom();
    }

    void ZoomAt(double zoom, Point at)
    {
        zoom = Math.Clamp(zoom, 1, 8);
        // Keep the point under the pointer still.
        var center = new Point(_stage.Bounds.Width / 2, _stage.Bounds.Height / 2);
        var rel = at - center - _pan;
        _pan = (Vector)(at - center) - rel * (zoom / _zoom);
        _zoom = zoom;
        if (_zoom > 1.05)
            _viewer?.OnZoomedIn();
        ApplyZoom();
    }

    void ApplyZoom()
    {
        if (_zoom <= 1.001)
        {
            _zoom = 1;
            _pan = default;
        }
        // Don't let the photo slide out of view.
        var maxX = _stage.Bounds.Width * (_zoom - 1) / 2;
        var maxY = _stage.Bounds.Height * (_zoom - 1) / 2;
        _pan = new Vector(Math.Clamp(_pan.X, -maxX, maxX), Math.Clamp(_pan.Y, -maxY, maxY));
        _viewerImage.RenderTransform = new MatrixTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan));
        _stage.Cursor = _zoom > 1 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
    }

    void OnStageWheel(object? sender, PointerWheelEventArgs e)
    {
        ZoomAt(_zoom * Math.Pow(1.25, e.Delta.Y), e.GetPosition(_stage));
        e.Handled = true;
    }

    void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_stage).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            if (_zoom > 1)
                ResetZoom();
            else
                ZoomAt(2.5, e.GetPosition(_stage));
            return;
        }
        if (_zoom > 1)
        {
            _panFrom = e.GetPosition(_stage);
            e.Pointer.Capture(_stage);
        }
    }

    void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (_panFrom is not { } from)
            return;
        var to = e.GetPosition(_stage);
        _pan += to - from;
        _panFrom = to;
        ApplyZoom();
    }

    // ---- Keyboard ----

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _pinned = null;
        if (_vm is null)
            return;
        if (e.Key == Key.Escape && _vm.Panel is { } panel)
        {
            _vm.Panel = null;
            e.Handled = true;
            return;
        }
        if (_vm.Viewer is { } viewer)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    viewer.CloseCommand.Execute(null);
                    break;
                case Key.Right:
                    viewer.NextCommand.Execute(null);
                    break;
                case Key.Left:
                    viewer.PreviousCommand.Execute(null);
                    break;
                case Key.F when viewer.CanEdit:
                    viewer.ToggleFavoriteCommand.Execute(null);
                    break;
                case Key.I:
                    viewer.ToggleInfoCommand.Execute(null);
                    break;
                case Key.Space or Key.Enter when viewer.IsVideo:
                    viewer.PlayPauseCommand.Execute(null);
                    break;
                case Key.M when viewer.IsVideo:
                    viewer.ToggleMuteCommand.Execute(null);
                    break;
                case Key.Up when viewer.IsVideo:
                    viewer.ChangeVolume(+5);
                    break;
                case Key.Down when viewer.IsVideo:
                    viewer.ChangeVolume(-5);
                    break;
                case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && !viewer.IsVideo:
                    viewer.CopyImageCommand.Execute(null);
                    break;
                case Key.Delete when viewer.CanEdit:
                    viewer.TrashCommand.Execute(null);
                    break;
                default:
                    return;
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _vm.IsSelecting)
        {
            _vm.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Source is not TextBox && _vm.ShowGrid && _vm.IsNotPicker)
        {
            _vm.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
    }
}
