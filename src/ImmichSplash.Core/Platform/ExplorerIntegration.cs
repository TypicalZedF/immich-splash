using System.Diagnostics;
using Microsoft.Win32;

namespace ImmichSplash.Core.Platform;

/// <summary>
/// "Upload to Immich" in the file manager. Windows: a right-click entry on files and folders (under "Show more
/// options" on Windows 11), plus a "Send to" shortcut, which hands over many files at once. Linux: an "Open with"
/// entry for photos and videos. Per-user; no admin rights needed.
/// </summary>
public static class ExplorerIntegration
{
    public const string UploadArgument = "--upload";
    const string Verb = "ImmichSplash.Upload";
    const string LinuxDesktopFile = "immich-splash-upload.desktop";

    static string? Executable =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;

    static string SendToShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), $"{AppInfo.Name}.lnk");

    /// <param name="createShortcut">Writes the "Send to" shortcut (the app has the COM code for .lnk files).</param>
    public static void Register(string menuText, Func<string, bool>? createShortcut)
    {
        var exe = Executable;
        if (exe is null)
            return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var target in new[] { "*", "Directory" })
                {
                    using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{target}\shell\{Verb}");
                    key.SetValue("MUIVerb", menuText);
                    key.SetValue("Icon", $"\"{exe}\",0");
                    // One copy per selected item; the running app gathers them into one upload.
                    key.SetValue("MultiSelectModel", "Player");
                    using var command = key.CreateSubKey("command");
                    command.SetValue("", $"\"{exe}\" {UploadArgument} \"%1\"");
                }
                if (!File.Exists(SendToShortcut))
                    createShortcut?.Invoke(SendToShortcut);
            }
            else if (OperatingSystem.IsLinux() && !Flatpak.IsRunning)
            {
                var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications");
                Directory.CreateDirectory(apps);
                File.WriteAllText(Path.Combine(apps, LinuxDesktopFile), $"""
                    [Desktop Entry]
                    Type=Application
                    Name={menuText}
                    Exec="{exe}" {UploadArgument} %F
                    NoDisplay=true
                    MimeType=image/jpeg;image/png;image/heic;image/heif;image/webp;image/gif;image/tiff;image/avif;image/x-adobe-dng;video/mp4;video/quicktime;video/x-matroska;video/webm;inode/directory;
                    """);
                Process.Start(new ProcessStartInfo("update-desktop-database", $"\"{apps}\"") { UseShellExecute = false })?.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not add Upload to Immich to the file manager", ex);
        }
    }

    public static void Unregister()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var target in new[] { "*", "Directory" })
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{target}\shell\{Verb}", throwOnMissingSubKey: false);
                if (File.Exists(SendToShortcut))
                    File.Delete(SendToShortcut);
            }
            else if (OperatingSystem.IsLinux())
            {
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications", LinuxDesktopFile);
                if (File.Exists(file))
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove Upload to Immich from the file manager", ex);
        }
    }

    /// <summary>The paths after <see cref="UploadArgument"/> on the command line (each may be a file or a folder).</summary>
    public static IReadOnlyList<string> UploadPaths(IReadOnlyList<string> args)
    {
        var at = args.ToList().IndexOf(UploadArgument);
        return at < 0 ? [] : args.Skip(at + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal) && (File.Exists(a) || Directory.Exists(a))).ToList();
    }
}
