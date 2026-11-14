using ImmichSplash.Core;
using ImmichSplash.Core.Platform;

namespace ImmichSplash.App.Services;

/// <summary>
/// Run by the uninstaller (ImmichSplash.exe --uninstall). Removes the autostart entry, the link handler,
/// the stored sign-in, logs, downloaded updates and the video cache in the temp folder. Whether to keep settings and backup history is asked by the uninstaller itself.
/// </summary>
public static class UninstallCleanup
{
    public static void Run()
    {
        try { Autostart.SetEnabled(false); } catch { /* best effort */ }
        try { UrlProtocol.Unregister(); } catch { /* best effort */ }
        try { ExplorerIntegration.Unregister(); } catch { /* best effort */ }
        try { CredentialStore.Delete(); } catch { /* best effort */ }
        try { LockedPinStore.Delete(); } catch { /* best effort */ }
        try { DesktopNotifications.Unregister(); } catch { /* best effort */ }
        try { Directory.Delete(AppPaths.LogsDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(UpdateService.DownloadDirectory, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "ImmichSplash"), recursive: true); } catch { /* best effort */ }
    }
}
