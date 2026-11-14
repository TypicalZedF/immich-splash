using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ImmichSplash.App.Services;
using ImmichSplash.App.ViewModels;
using ImmichSplash.App.Views;
using ImmichSplash.Core;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.App;

public partial class App : Application
{
    IClassicDesktopStyleApplicationLifetime? _desktop;
    TrayController? _tray;
    MainWindow? _window;
    DispatcherTimer? _autoUpdateTimer;
    bool _quitting;
    bool _wasBusy;
    bool _checkForUpdates;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Services.AccessibleNames.Start();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        LinuxTouchpadScroll.Start();
        // Something going wrong while drawing or loading one picture mustn't take the whole app down.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception on the UI thread", e.Exception);
            e.Handled = e.Exception is not (OutOfMemoryException or StackOverflowException);
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            AppServices.Initialize();
            var settings = AppServices.Settings.Current;
            ThemeService.Apply(settings.Theme);

            _tray = new TrayController(this);
            if (Program.Instance is { } instance)
                instance.CommandReceived += (command, argument) => Dispatcher.UIThread.Post(() => OnCommand(command, argument));

            // Launched at login: stay in the tray. Otherwise (or if setup isn't finished) open the window.
            var background = desktop.Args?.Contains(Autostart.BackgroundArgument) == true
                || (Program.StartupAction is "sync" or "pause" && AppServices.Account.IsSignedIn);
            if (!background || !AppServices.Account.IsSignedIn)
                ShowMainWindow();

            // Keep OS integration pointing at the current executable (e.g. after an update or a move).
            if (settings.IsConfigured && settings.LaunchAtStartup)
                Autostart.SetEnabled(true);
            UrlProtocol.Register();

            AppServices.Network.Start();
            AppServices.Engine.Start();
            AppServices.Engine.StatusChanged += OnEngineStatus;
            AppServices.Engine.RunFinished += run =>
            {
                if (BackupNotifier.OnRunFinished(run, AppServices.Settings.Current) is { } notice)
                    DesktopNotifications.Show(notice);
            };
            DesktopNotifications.Clicked += kind => Dispatcher.UIThread.Post(() =>
            {
                ShowMainWindow(kind switch
                {
                    NoticeKind.Failed => ShellPage.Activity,
                    NoticeKind.BackedUp => ShellPage.Overview,
                    _ => null,
                });
                if (kind == NoticeKind.CameraCard)
                    _ = OfferCardAsync();
                // "Sign in to Immich again": straight to signing in (not behind the app lock, which shows first if on).
                if (kind == NoticeKind.SignInNeeded && _window?.DataContext is MainWindowViewModel { IsLocked: false } main && AppServices.Account.IsRejected)
                    _ = main.SignInAgainAsync();
            });
            AppServices.Settings.Changed += OnSettingsChanged;

            AppServices.Updates.QuitApp = Quit;
            AppServices.Updates.Changed += () => Dispatcher.UIThread.Post(TryAutoInstallUpdate);
            _checkForUpdates = settings.CheckForUpdates;
            AppServices.Updates.Start(_checkForUpdates);
            _autoUpdateTimer = new DispatcherTimer(TimeSpan.FromMinutes(10), DispatcherPriority.Background, (_, _) => TryAutoInstallUpdate());
            _autoUpdateTimer.Start();
            ApplyWallpaper(settings);

            if (Program.StartupLink is { } link)
                OAuthCallbacks.Deliver(link);
            StartIntegration();

            if (_window is null)
                DispatcherTimer.RunOnce(TrimIfHidden, TimeSpan.FromSeconds(15));
            desktop.ShutdownRequested += (_, _) => Log.Info("Shutdown requested by the OS");
            desktop.Exit += (_, _) =>
            {
                StopIntegration();
                try
                {
                    AppServices.ShutdownAsync().Wait(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    Log.Warn("Shutdown didn't finish cleanly", ex.InnerException ?? ex); // quitting anyway
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies a new language everywhere: the window is rebuilt and the tray menu recreated.</summary>
    public void ApplyLanguage(string? code)
    {
        Tr.SetLanguage(code);
        _tray?.Dispose();
        _tray = new TrayController(this);
        if (_window?.DataContext is MainWindowViewModel main && !main.IsLocked && main.Content is ShellViewModel)
            main.ShowShell(ShellPage.Settings);
    }

    void OnCommand(SingleInstance.Command command, string? argument)
    {
        switch (command)
        {
            case SingleInstance.Command.Activate:
                ShowMainWindow();
                break;
            case SingleInstance.Command.OpenLink when argument is not null:
                Log.Info($"Link from the browser: {Core.Services.OAuthSession.Describe(argument)}");
                OAuthCallbacks.Deliver(argument);
                ShowMainWindow();
                break;
            case SingleInstance.Command.Quit:
                Quit();
                break;
            case SingleInstance.Command.Upload when argument is not null:
                QueueUploads(argument.Split(SingleInstance.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
                break;
            case SingleInstance.Command.Run when argument is not null:
                RunAction(argument.Trim());
                break;
        }
    }

    (bool, WallpaperSource, string?, int) _wallpaperConfig;

    /// <summary>Restarts the desktop background timer when its settings change.</summary>
    void ApplyWallpaper(AppSettings settings)
    {
        var config = (settings.WallpaperEnabled, settings.WallpaperSource, settings.WallpaperAlbumId, settings.WallpaperIntervalMinutes);
        if (config == _wallpaperConfig)
            return;
        _wallpaperConfig = config;
        AppServices.Wallpaper.Apply(settings);
    }

    void OnSettingsChanged(AppSettings settings) => Dispatcher.UIThread.Post(() =>
    {
        ApplyWallpaper(settings);
        if (settings.CheckForUpdates != _checkForUpdates)
        {
            _checkForUpdates = settings.CheckForUpdates;
            AppServices.Updates.Start(_checkForUpdates);
        }
    });

    /// <summary>When a sync run finishes while only the tray is showing, hand memory back and maybe update.</summary>
    readonly BackupNotifier _notifier = new();

    void OnEngineStatus(SyncStatus status)
    {
        Notice? notice;
        lock (_notifier)
            notice = _notifier.OnStatus(status, AppServices.Settings.Current);
        if (status.State == SyncState.AuthError && notice is null)
            Log.Info("Signed out, no notification (already signed out, or sign-in notifications are off)");
        if (notice is not null)
            DesktopNotifications.Show(notice);
        var busy = status.IsBusy;
        if (_wasBusy && !busy)
        {
            Dispatcher.UIThread.Post(() =>
            {
                TrimIfHidden();
                TryAutoInstallUpdate();
            }, DispatcherPriority.Background);
        }
        _wasBusy = busy;
    }

    /// <summary>Installs a downloaded update automatically, but only when nobody is looking and nothing is uploading.</summary>
    void TryAutoInstallUpdate()
    {
        if (_quitting || _window is not null || AppServices.Updates.State != UpdateState.Ready)
            return;
        if (!AppServices.Settings.Current.AutoInstallUpdates || AppServices.Engine.Status.IsBusy)
            return;
        Log.Info("Installing update automatically while idle");
        AppServices.Updates.InstallAndRestart();
    }

    void TrimIfHidden()
    {
        if (_window is null)
            MemoryTrimmer.Trim();
    }

    public void ShowMainWindow(ShellPage? page = null)
    {
        if (_quitting)
            return;

        if (_window is null)
        {
            var viewModel = new MainWindowViewModel();
            _window = new MainWindow { DataContext = viewModel };
            _window.Closed += (_, _) =>
            {
                viewModel.Dispose();
                _window = null;
                // Only the tray and the engine remain; release UI memory.
                Dispatcher.UIThread.Post(MemoryTrimmer.Trim, DispatcherPriority.Background);
            };
            _window.Show();
        }
        else
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Show();
            // Coax Windows into bringing us to the front when activated from the background.
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Activate();
        }

        if (page is { } p && _window.DataContext is MainWindowViewModel vm)
            vm.Navigate(p);
    }

    public void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;
        Log.Info("Quitting");
        _autoUpdateTimer?.Stop();
        _window?.Close();
        _tray?.Dispose();
        _desktop?.Shutdown();
    }
}
