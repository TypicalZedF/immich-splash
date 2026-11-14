using ImmichSplash.Core;
using ImmichSplash.Core.Privacy;
using ImmichSplash.Core.Services;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Storage;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.App.Services;

/// <summary>Composition root: the long-lived services shared by the tray and the window.</summary>
public static class AppServices
{
    public static SettingsStore Settings { get; private set; } = null!;
    public static StateDatabase Database { get; private set; } = null!;
    public static ActivityLog Activity { get; private set; } = null!;
    public static AccountService Account { get; private set; } = null!;
    public static SyncEngine Engine { get; private set; } = null!;
    public static OfflineAlbums Offline { get; private set; } = null!;
    public static UpdateService Updates { get; private set; } = null!;
    public static ProfileService Profile { get; private set; } = null!;
    public static WallpaperService Wallpaper { get; private set; } = null!;
    public static NetworkMonitor Network { get; private set; } = null!;
    public static PrivacyScanService PrivacyScan { get; private set; } = null!;

    /// <summary>Albums were created, renamed, deleted or filled (the sidebar reloads its recent albums).</summary>
    public static event Action? AlbumsChanged;

    public static void RaiseAlbumsChanged() => AlbumsChanged?.Invoke();

    public static void Initialize()
    {
        Settings = new SettingsStore(AppPaths.SettingsFile);
        Tr.SetLanguage(Settings.Current.Language);
        Database = new StateDatabase(AppPaths.DatabaseFile);
        Activity = new ActivityLog(Database);
        Account = new AccountService(Settings, Database);
        Account.Restore();
        WindowsNetwork.Register();
        Network = new NetworkMonitor(Settings, Account);
        Engine = new SyncEngine(Settings, Database, Account, Activity) { IsMetered = () => Network.IsMetered };
        Updates = new UpdateService();
        Profile = new ProfileService();
        Wallpaper = new WallpaperService();
        PrivacyScan = new PrivacyScanService(Settings, Database, Account, () => Network.IsMetered, DecodeForPrivacyCheck);
        Offline = new OfflineAlbums(Settings, Account, () => Network.IsMetered);
        Offline.Start();
        Account.SessionChanged += () =>
        {
            if (Account.IsSignedIn)
                _ = Profile.RefreshAsync();
            else
                Profile.Clear();
        };
        if (Account.IsSignedIn)
            _ = Profile.RefreshAsync();
    }

    /// <summary>Decodes a preview for the on-device privacy check (upright, at most 1280 pixels).</summary>
    static BgraImage? DecodeForPrivacyCheck(byte[] data)
    {
        try
        {
            using var decoded = Images.DecodeUpright(data, 1280);
            using var bitmap = decoded.ColorType == SkiaSharp.SKColorType.Bgra8888 && decoded.RowBytes == decoded.Width * 4
                ? decoded.Copy()
                : decoded.Copy(SkiaSharp.SKColorType.Bgra8888);
            return new BgraImage(bitmap.Width, bitmap.Height, bitmap.Bytes);
        }
        catch (Exception ex)
        {
            Log.Info($"Privacy check: couldn't decode a preview: {ex.Message}");
            return null;
        }
    }

    public static async Task ShutdownAsync()
    {
        PrivacyScan?.Dispose();
        Offline?.Dispose();
        Updates?.Dispose();
        Network?.Dispose();
        Wallpaper?.Dispose();
        if (Engine is not null)
            await Engine.DisposeAsync();
        Account?.Dispose();
    }
}
