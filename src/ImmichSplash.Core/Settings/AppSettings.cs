namespace ImmichSplash.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Which releases the app updates to.</summary>
public enum UpdateChannel
{
    Stable,

    /// <summary>Also pre-releases, built from the beta branch: new features first, maybe rough edges.</summary>
    Beta,
}

/// <summary>What backup does on a metered connection (e.g. a phone hotspot or a capped plan).</summary>
/// <summary>A past search: what was searched for and, for a person, place or album, which one.</summary>
public sealed class SearchHistoryEntry
{
    public SearchKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? Id { get; set; }
    public string? Extra { get; set; }
}

public enum SearchKind { Text, FileName, Person, Place, Album }

public enum MapDateRange { All, Day, Week, Month, Year, ThreeYears, Custom }

public enum MeteredBackup
{
    Normal,
    PhotosOnly,
    Pause,
}

/// <summary>In what order desktop backgrounds are shown.</summary>
public enum WallpaperOrder
{
    Random,
    OldestFirst,
    NewestFirst,
}

/// <summary>Where desktop backgrounds come from.</summary>
public enum WallpaperSource
{
    Favorites,
    Album,
    Library,
}

public enum AppLockMode
{
    Off,
    WindowsHello,
    Password,
}

/// <summary>A local folder that is backed up to Immich.</summary>
public sealed class SyncFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IncludeSubfolders { get; set; } = true;
    public bool IncludePhotos { get; set; } = true;
    public bool IncludeVideos { get; set; } = true;

    /// <summary>When set, uploaded assets from this folder are added to an album with this name.</summary>
    public string? AlbumName { get; set; }

    /// <summary>Glob patterns relative to the folder, e.g. "*.tmp" or "Screenshots".</summary>
    public List<string> ExcludePatterns { get; set; } = [];

    public string DisplayName
    {
        get
        {
            var trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }
}

public sealed class AppSettings
{
    // Account / server
    public string? ServerUrl { get; set; }
    public string? WebUrl { get; set; }
    public string? ServerVersion { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }

    // Backup
    public List<SyncFolder> Folders { get; set; } = [];
    public bool Paused { get; set; }

    /// <summary>When paused for a limited time, backup resumes automatically at this moment.</summary>
    public DateTimeOffset? PausedUntil { get; set; }

    public int MaxConcurrentUploads { get; set; } = 2;

    /// <summary>Upload speed limit in KB/s. 0 means unlimited.</summary>
    public int UploadLimitKBps { get; set; }

    public bool PauseOnBattery { get; set; }

    /// <summary>After backing up, stack a RAW photo with the JPEG (or HEIC) of the same name, like a camera shot both.</summary>
    public bool StackRawWithJpeg { get; set; }

    /// <summary>Only upload between these times (minutes after midnight; the window may cross midnight).</summary>
    public bool BackupWindowEnabled { get; set; }
    public int BackupWindowStartMinutes { get; set; } = 60;
    public int BackupWindowEndMinutes { get; set; } = 6 * 60;

    /// <summary>Data saving: what to do on metered connections.</summary>
    public MeteredBackup MeteredBackup { get; set; } = MeteredBackup.PhotosOnly;

    // Server addresses, like the mobile app's automatic URL switching: a local address used on your
    // home network and external addresses (tried in order) everywhere else. ServerUrl stays the
    // address you signed in with, the last resort.
    public bool AutoSwitchServer { get; set; }
    public string? LocalNetworkName { get; set; }
    public string? LocalServerUrl { get; set; }
    public List<string> ExternalServerUrls { get; set; } = [];
    public int RescanIntervalMinutes { get; set; } = 60;

    // App
    public bool LaunchAtStartup { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Draw the app with the graphics card (smoother). Off: the processor draws it. Applies after a restart.</summary>
    public bool GpuRendering { get; set; } = true;

    /// <summary>Desktop notifications at all; the kinds below choose which.</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>When a finished backup is worth a notification.</summary>
    public BackupNotice NotifyBackups { get; set; } = BackupNotice.BigBackups;

    /// <summary>Notify when files can't be backed up.</summary>
    public bool NotifyFailures { get; set; } = true;

    /// <summary>Notify when the server signs the app out.</summary>
    public bool NotifySignIn { get; set; } = true;

    /// <summary>Photo grid thumbnail size in pixels (the zoom slider in Photos).</summary>
    public int ThumbnailSize { get; set; } = 168;

    /// <summary>How the Albums page is arranged (remembered).</summary>
    public string AlbumSort { get; set; } = "MostRecentPhoto";
    public bool AlbumSortDescending { get; set; } = true;
    public bool AlbumGroupByYear { get; set; } = true;
    public bool AlbumListView { get; set; }

    /// <summary>The video player's volume, 0 to 100 (remembered).</summary>
    public int VideoVolume { get; set; } = 100;

    /// <summary>The albums you opened or added to most recently (ids, newest first), for the sidebar.</summary>
    public List<string> RecentAlbumIds { get; set; } = [];

    /// <summary>Where the main window was when it was last closed (null: use the default size, centred).</summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>App language code (e.g. "de"); null follows the system language.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Show online street detail on the map. Off: only the built-in map is drawn and nothing is
    /// requested from map services (which would otherwise learn roughly where your photos are).
    /// </summary>
    public bool MapStreetDetail { get; set; }

    /// <summary>Recent searches (newest first), shown under the search box.</summary>
    public List<SearchHistoryEntry> SearchHistory { get; set; } = [];

    // Map settings (like the web app's): which photos, and how they're shown.
    public bool MapAllowDark { get; set; } = true;
    public bool MapOnlyFavorites { get; set; }
    public bool MapIncludeArchived { get; set; }
    public bool MapIncludePartners { get; set; }
    public bool MapIncludeSharedAlbums { get; set; }
    public bool MapShowPanel { get; set; }
    public bool MapHeat { get; set; }
    public MapDateRange MapDateRange { get; set; } = MapDateRange.All;
    public DateTime? MapDateFrom { get; set; }
    public DateTime? MapDateTo { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Look through the library on this PC for private pictures (nudity, cards, IDs, documents…) and
    /// suggest moving them to the locked folder. Off until you turn it on; nothing leaves the PC.
    /// </summary>
    public bool PrivacyScanEnabled { get; set; }

    /// <summary>Use the graphics card for the privacy check (much faster); falls back to the CPU.</summary>
    public bool PrivacyScanUseGpu { get; set; } = true;

    /// <summary>Only check while the PC isn't being used, so it never slows down games or calls.</summary>
    public bool PrivacyScanOnlyWhenIdle { get; set; }


    /// <summary>
    /// Also use the thorough nudity check (CLIP, downloaded once), which understands drawings, art and
    /// game screenshots. Slower; best with a graphics card.
    /// </summary>
    public bool PrivacyScanThorough { get; set; }

    // Security: ask for Windows Hello or a password before showing the window (backup keeps running).
    public AppLockMode AppLock { get; set; } = AppLockMode.Off;

    /// <summary>PBKDF2 hash of the app lock password (never the password itself).</summary>
    public string? LockPasswordHash { get; set; }

    public bool AutoInstallUpdates { get; set; } = true;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>Set by first-time setup: ask how updates should work the next time the main window shows.</summary>
    public bool UpdatePreferencesPending { get; set; }

    // Desktop background: a photo from Immich, changed every so often (like a digital picture frame).
    public bool WallpaperEnabled { get; set; }
    public WallpaperSource WallpaperSource { get; set; } = WallpaperSource.Favorites;
    public string? WallpaperAlbumId { get; set; }
    public string? WallpaperAlbumName { get; set; }
    public int WallpaperIntervalMinutes { get; set; } = 60;
    public WallpaperOrder WallpaperOrder { get; set; } = WallpaperOrder.Random;

    /// <summary>In order: where it got to ("source|month|photo id"), so it carries on after a restart.</summary>
    public string? WallpaperPosition { get; set; }

    public bool IsConfigured => !string.IsNullOrEmpty(ServerUrl) && !string.IsNullOrEmpty(UserId);
}

/// <summary>Which finished backups get a notification.</summary>
public enum BackupNotice
{
    Never,
    /// <summary>Only big ones (many files, or a long run), so a photo or two stays quiet.</summary>
    BigBackups,
    /// <summary>Every backup that uploaded something.</summary>
    Every,
}

