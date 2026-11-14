using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImmichSplash.App.Services;
using ImmichSplash.App.ViewModels;

namespace ImmichSplash.App.Views;

public partial class MainWindow : Window
{
    /// <summary>With an app lock set up, the window locks itself after this long without input.</summary>
    static readonly TimeSpan IdleLockAfter = TimeSpan.FromMinutes(5);

    readonly DispatcherTimer _idleTimer;
    DateTime _lastInput = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();

        var icon = new Uri("avares://ImmichSplash/Assets/app.ico");
        if (AssetLoader.Exists(icon))
            Icon = new WindowIcon(AssetLoader.Open(icon));

        RestoreBounds();

        Closing += (_, _) => SaveBounds();

        Opened += async (_, _) =>
        {
            // Find out whether the graphics card actually ended up drawing (it falls back to the processor).
            try
            {
                if (Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
                    Program.IsGpuActive = await compositor.TryGetCompositionGpuInterop() is not null;
            }
            catch (Exception ex)
            {
                Core.Log.Info($"Couldn't tell how the app is drawn: {ex.Message}");
            }
        };
        AddHandler(PointerMovedEvent, (_, _) => _lastInput = DateTime.UtcNow, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Drop photos, videos or folders anywhere on the window to upload them once.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDragOver(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, (_, _) => _lastInput = DateTime.UtcNow, RoutingStrategies.Tunnel, handledEventsToo: true);
        _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm && DateTime.UtcNow - _lastInput > IdleLockAfter)
                vm.Lock();
        });
        _idleTimer.Start();
        Closed += (_, _) => _idleTimer.Stop();

        // A locked window asks for Windows Hello only when you come back to it, never in the background.
        Activated += (_, _) => (CurrentLock)?.OnWindowActivated();
        Deactivated += (_, _) => (CurrentLock)?.OnWindowDeactivated();
    }

    /// <summary>Opens at the size and place the window had when it was last closed.</summary>
    void RestoreBounds()
    {
        try
        {
            var settings = AppServices.Settings.Current;
            if (settings.WindowWidth is not { } width || settings.WindowHeight is not { } height)
                return;
            Width = Math.Max(MinWidth, width);
            Height = Math.Max(MinHeight, height);
            if (settings.WindowX is { } x && settings.WindowY is { } y)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(x, y);
                // A screen that's gone (unplugged monitor) must not leave the window out of reach.
                Opened += (_, _) =>
                {
                    var grip = new PixelPoint(Position.X + 100, Position.Y + 20);
                    if (!Screens.All.Any(screen => screen.WorkingArea.Contains(grip)) && Screens.Primary is { } primary)
                    {
                        var area = primary.WorkingArea;
                        Position = new PixelPoint(area.X + Math.Max(0, (area.Width - (int)(Width * primary.Scaling)) / 2),
                            area.Y + Math.Max(0, (area.Height - (int)(Height * primary.Scaling)) / 2));
                    }
                };
            }
            if (settings.WindowMaximized)
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            Core.Log.Info($"Couldn't restore the window size: {ex.Message}");
        }
    }

    /// <summary>Remembers the window's size and place (and whether it was maximized) for next time.</summary>
    void SaveBounds()
    {
        try
        {
            var state = WindowState;
            if (state == WindowState.Minimized)
                return;
            var size = ClientSize;
            var position = Position;
            AppServices.Settings.Update(s =>
            {
                s.WindowMaximized = state == WindowState.Maximized;
                // Maximized: keep the last normal size, so un-maximizing later goes back to it.
                if (state == WindowState.Normal && size.Width > 0 && size.Height > 0)
                {
                    s.WindowWidth = size.Width;
                    s.WindowHeight = size.Height;
                    s.WindowX = position.X;
                    s.WindowY = position.Y;
                }
            });
        }
        catch (Exception ex)
        {
            Core.Log.Info($"Couldn't save the window size: {ex.Message}");
        }
    }

    LockViewModel? CurrentLock => (DataContext as MainWindowViewModel)?.Content as LockViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PickFolder = PickFolderAsync;
            vm.PickImage = PickImageAsync;
            vm.PickFiles = PickFilesAsync;
            vm.PickSaveFile = PickSaveFileAsync;
            vm.CopyText = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
            ImageClipboard.Clipboard = Clipboard;
            vm.GetWindowHandle = () => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel { Dialog: { } dialog })
        {
            dialog.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DataContext is MainWindowViewModel { Overlay: ProfilePhotoViewModel photo } && !photo.IsSaving)
        {
            photo.CloseCommand.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    async Task<string?> PickImageAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Tr.T("Images")) { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    async Task<IReadOnlyList<string>> PickFilesAsync(string title, string pattern)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(pattern) { Patterns = [pattern] }],
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
    }

    async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    MainWindowViewModel? Main => DataContext as MainWindowViewModel;

    void SetDragOver(bool value)
    {
        if (Main is { } main)
            main.IsDragOver = value;
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepts = Main?.CanAcceptDrop == true && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragOver(accepts);
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        SetDragOver(false);
        var paths = e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        if (paths.Count > 0)
            Main?.OfferUpload(paths);
        e.Handled = true;
    }
}
