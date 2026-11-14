using System.Runtime.InteropServices;
using Avalonia;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Services;

namespace ImmichSplash.App;

public static class Program
{
    /// <summary>Held for the lifetime of the process; null in design/preview tools.</summary>
    public static SingleInstance? Instance { get; private set; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Set when Windows launched us to deliver an SSO callback link and no other copy was running.</summary>
    public static string? StartupLink { get; private set; }
    public static IReadOnlyList<string> StartupUploads { get; private set; } = [];
    public static string? StartupAction { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Windows ties notifications (and the taskbar button) to an app ID; an app that isn't packaged has to say which.
        if (OperatingSystem.IsWindows())
            SetCurrentProcessExplicitAppUserModelID(DesktopNotifications.WindowsAppId);

        // Commands used by the installer/uninstaller.
        if (args.Contains("--quit"))
        {
            SingleInstance.RequestQuit(TimeSpan.FromSeconds(15));
            return 0;
        }
        if (args.Contains("--test-notification"))
        {
            // Support: check that desktop notifications work on this PC.
            DesktopNotifications.Show(new Core.Sync.Notice(Core.Sync.NoticeKind.BackedUp, Core.AppInfo.Name, "Notifications work."));
            Thread.Sleep(3000);
            return 0;
        }
        if (args.Contains("--uninstall"))
        {
            SingleInstance.RequestQuit(TimeSpan.FromSeconds(15));
            UninstallCleanup.Run();
            return 0;
        }

        var link = args.FirstOrDefault(OAuthSession.IsCallback);
        // From the file manager ("Upload to Immich", "Send to") and the taskbar jump list.
        var uploads = ExplorerIntegration.UploadPaths(args);
        var actionAt = Array.IndexOf(args, App.ActionArgument);
        var action = actionAt >= 0 && actionAt + 1 < args.Length ? args[actionAt + 1] : null;
        void HandOver()
        {
            if (link is not null)
                SingleInstance.Send(SingleInstance.Command.OpenLink, link);
            else if (uploads.Count > 0)
                SingleInstance.Send(SingleInstance.Command.Upload, string.Join(SingleInstance.PathSeparator, uploads));
            else if (action is not null)
                SingleInstance.Send(SingleInstance.Command.Run, action);
            else if (!args.Contains(Autostart.BackgroundArgument))
                SingleInstance.Send(SingleInstance.Command.Activate);
        }

        Instance = SingleInstance.TryAcquire();
        if (Instance is null)
        {
            // Already running: hand over the SSO link or files, or bring the window forward (unless this is the silent autostart launch).
            HandOver();
            return 0;
        }
        // Safety net on Linux, where the lock above has missed a running copy before (each launch in its
        // own session): if one is already listening, hand over to it and stop.
        if (!OperatingSystem.IsWindows() && SingleInstance.IsAnotherListening())
        {
            Log.Info("Another copy is already running; handing over to it");
            HandOver();
            Instance.Dispose();
            return 0;
        }
        StartupLink = link;
        StartupUploads = uploads;
        StartupAction = action;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        Log.Info($"Starting {AppInfo.Name} {AppInfo.Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        try
        {
            Instance.StartListening();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            throw;
        }
        finally
        {
            Instance.Dispose();
        }
    }

    /// <summary>Whether this run draws with the graphics card (Settings → Graphics acceleration; read at startup).</summary>
    /// <summary>The app draws with the graphics card (set at start from the settings; the screenshot tool turns it off).</summary>
    public static bool GpuRequested { get; set; } = true;

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        try
        {
            GpuRequested = new Core.Settings.SettingsStore(AppPaths.SettingsFile).Current.GpuRendering;
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't read the graphics setting: {ex.Message}");
        }
        var gpu = GpuRequested;
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Graphics card first (Direct3D through ANGLE, then OpenGL), the processor as the last resort.
            .With(new Win32PlatformOptions
            {
                RenderingMode = gpu
                    ? [Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software]
                    : [Win32RenderingMode.Software],
                CompositionMode = gpu
                    ? [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface]
                    : [Win32CompositionMode.RedirectionSurface],
            })
            .With(new X11PlatformOptions
            {
                RenderingMode = gpu ? [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software] : [X11RenderingMode.Software],
            })
            // Room on the graphics card for many thumbnails, so scrolling back doesn't re-upload them.
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 512L * 1024 * 1024 })
            .LogToTrace();
    }

    /// <summary>How the app is being drawn right now (set once the window is up), for Settings.</summary>
    public static bool IsGpuActive { get; internal set; }
}
