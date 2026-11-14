using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Settings;

namespace ImmichSplash.App.ViewModels;

public sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    readonly ShellViewModel _shell;
    readonly bool _loading;

    /// <summary>The locked folder's PIN (Security).</summary>
    public LockedPinSettingsViewModel LockedPin { get; } = new();

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _loading = true;
        var s = AppServices.Settings.Current;

        UserName = s.UserName ?? "";
        UserEmail = s.UserEmail ?? "";
        ServerUrl = s.WebUrl?.TrimEnd('/') ?? "";
        ServerVersion = s.ServerVersion ?? "";

        LaunchAtStartup = Autostart.IsEnabled();
        GpuRendering = s.GpuRendering;
        Notifications = s.Notifications;
        NotifyBackupsIndex = (int)s.NotifyBackups;
        NotifyFailures = s.NotifyFailures;
        NotifySignIn = s.NotifySignIn;
        ExplorerIntegration = s.ExplorerIntegration;
        AskAboutCameraCards = s.AskAboutCameraCards;
        _theme = s.Theme;
        LanguageOptions = [new Option<string?>(Tr.T("System ({0})", Tr.Languages.First(l => l.Code == Tr.SystemLanguage).Name), null),
            .. Tr.Languages.Select(l => new Option<string?>(l.Name, l.Code))];
        SelectedLanguage = LanguageOptions.FirstOrDefault(o => o.Value == s.Language) ?? LanguageOptions[0];
        SelectedConcurrency = Concurrency.FirstOrDefault(c => c.Value == s.MaxConcurrentUploads) ?? Concurrency[1];
        SelectedSpeedLimit = SpeedLimits.FirstOrDefault(o => o.Value == s.UploadLimitKBps) ?? SpeedLimits[0];
        SelectedRescan = RescanIntervals.FirstOrDefault(o => o.Value == s.RescanIntervalMinutes) ?? RescanIntervals[2];
        PauseOnBattery = s.PauseOnBattery;
        StackRawWithJpeg = s.StackRawWithJpeg;
        BackupWindowEnabled = s.BackupWindowEnabled;
        BackupWindowStart = TimeChoices.FirstOrDefault(o => o.Value == s.BackupWindowStartMinutes) ?? TimeChoices[2];
        BackupWindowEnd = TimeChoices.FirstOrDefault(o => o.Value == s.BackupWindowEndMinutes) ?? TimeChoices[12];
        CheckForUpdates = s.CheckForUpdates;
        AutoInstallUpdates = s.AutoInstallUpdates;
        _channel = s.UpdateChannel;
        SelectedMetered = MeteredOptions.FirstOrDefault(o => o.Value == s.MeteredBackup) ?? MeteredOptions[1];
        AutoSwitchServer = s.AutoSwitchServer;
        LocalNetworkName = s.LocalNetworkName ?? "";
        LocalServerUrl = s.LocalServerUrl ?? "";
        foreach (var url in s.ExternalServerUrls)
            ExternalServerUrls.Add(url);
        WallpaperEnabled = s.WallpaperEnabled;
        SelectedWallpaperSource = WallpaperSources.FirstOrDefault(o => o.Value == s.WallpaperSource) ?? WallpaperSources[0];
        SelectedWallpaperInterval = WallpaperIntervals.FirstOrDefault(o => o.Value == s.WallpaperIntervalMinutes) ?? WallpaperIntervals[2];
        SelectedWallpaperOrder = WallpaperOrders.FirstOrDefault(o => o.Value == s.WallpaperOrder) ?? WallpaperOrders[0];
        _lockMode = s.AppLock;
        _loading = false;

        ThemeService.Changed += OnThemeChanged;
        AppServices.Updates.Changed += OnUpdatesChanged;
        AppServices.Profile.Changed += OnProfileChanged;
        AppServices.Wallpaper.Changed += OnWallpaperChanged;
        AppServices.Network.Changed += OnNetworkChanged;
        _ = DetectHelloAsync();
        if (WallpaperSupported)
            _ = LoadWallpaperAlbumsAsync();
    }

    public void Dispose()
    {
        ThemeService.Changed -= OnThemeChanged;
        AppServices.Updates.Changed -= OnUpdatesChanged;
        AppServices.Profile.Changed -= OnProfileChanged;
        AppServices.Wallpaper.Changed -= OnWallpaperChanged;
        AppServices.Network.Changed -= OnNetworkChanged;
    }

    // ---- Network: data saving and server addresses ----

    public IReadOnlyList<Option<MeteredBackup>> MeteredOptions { get; } =
    [
        new(Tr.T("Back up everything"), MeteredBackup.Normal),
        new(Tr.T("Photos only"), MeteredBackup.PhotosOnly),
        new(Tr.T("Pause backup"), MeteredBackup.Pause),
    ];

    [ObservableProperty]
    public partial Option<MeteredBackup> SelectedMetered { get; set; }

    partial void OnSelectedMeteredChanged(Option<MeteredBackup> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.MeteredBackup = value.Value);
        AppServices.Engine.Wake();
    }

    [ObservableProperty]
    public partial bool AutoSwitchServer { get; set; }

    partial void OnAutoSwitchServerChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.AutoSwitchServer = value);
        _ = AppServices.Network.CheckAsync();
    }

    [ObservableProperty]
    public partial string LocalNetworkName { get; set; } = "";

    [ObservableProperty]
    public partial string LocalServerUrl { get; set; } = "";

    partial void OnLocalNetworkNameChanged(string value) => NetworkSaved = false;
    partial void OnLocalServerUrlChanged(string value) => NetworkSaved = false;

    public System.Collections.ObjectModel.ObservableCollection<string> ExternalServerUrls { get; } = [];

    [ObservableProperty]
    public partial string NewExternalUrl { get; set; } = "";

    [ObservableProperty]
    public partial string? NetworkError { get; set; }

    [ObservableProperty]
    public partial bool NetworkSaved { get; set; }

    public string NetworkStatus
    {
        get
        {
            var network = AppServices.Network.Network;
            var where = network.Name is { } name ? Tr.T("On “{0}”", name) : Tr.T("Network unknown");
            if (network.IsMetered)
                where += Tr.T(" (metered)");
            var host = AppServices.Account.Client?.ApiUrl is { } api ? (api.IsDefaultPort ? api.Host : $"{api.Host}:{api.Port}") : null;
            var through = host is null ? "" : AppServices.Network.IsUsingLocal ? Tr.T(" · using the local address {0}", host) : Tr.T(" · using {0}", host);
            return where + through;
        }
    }

    void OnNetworkChanged() => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(NetworkStatus)));

    [RelayCommand]
    async Task UseCurrentNetworkAsync()
    {
        var current = await Task.Run(Core.Platform.NetworkStatus.Current);
        if (current.Name is { } name)
        {
            LocalNetworkName = name;
            NetworkError = null;
        }
        else
        {
            NetworkError = Tr.T("Couldn't tell which network you're on. Type its name (your Wi-Fi name) instead.");
        }
    }

    [RelayCommand]
    void AddExternalUrl()
    {
        var text = NewExternalUrl.Trim();
        if (text.Length == 0)
            return;
        if (Core.Services.ServerEndpoints.ToApiUrl(text) is null)
        {
            NetworkError = $"“{text}” isn't a server address. Use something like https://photos.example.com.";
            return;
        }
        if (!ExternalServerUrls.Contains(text, StringComparer.OrdinalIgnoreCase))
            ExternalServerUrls.Add(text);
        NewExternalUrl = "";
        NetworkError = null;
        SaveNetwork();
    }

    [RelayCommand]
    void RemoveExternalUrl(string url)
    {
        ExternalServerUrls.Remove(url);
        SaveNetwork();
    }

    [RelayCommand]
    void MoveExternalUrlUp(string url)
    {
        var index = ExternalServerUrls.IndexOf(url);
        if (index > 0)
            ExternalServerUrls.Move(index, index - 1);
        SaveNetwork();
    }

    [RelayCommand]
    void SaveNetwork()
    {
        NetworkSaved = false;
        if (LocalServerUrl.Trim().Length > 0 && Core.Services.ServerEndpoints.ToApiUrl(LocalServerUrl) is null)
        {
            NetworkError = $"“{LocalServerUrl.Trim()}” isn't a server address. Use something like http://192.168.1.10:2283.";
            return;
        }
        NetworkError = null;
        AppServices.Settings.Update(s =>
        {
            s.LocalNetworkName = string.IsNullOrWhiteSpace(LocalNetworkName) ? null : LocalNetworkName.Trim();
            s.LocalServerUrl = string.IsNullOrWhiteSpace(LocalServerUrl) ? null : LocalServerUrl.Trim();
            s.ExternalServerUrls = ExternalServerUrls.ToList();
        });
        NetworkSaved = true;
        _ = AppServices.Network.CheckAsync();
    }

    [RelayCommand]
    static Task CheckNetworkAsync() => AppServices.Network.CheckAsync();

    // ---- Desktop background ----

    public bool WallpaperSupported => WallpaperService.IsSupported;

    [ObservableProperty]
    public partial bool WallpaperEnabled { get; set; }

    partial void OnWallpaperEnabledChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.WallpaperEnabled = value);
    }

    public IReadOnlyList<Option<WallpaperSource>> WallpaperSources { get; } =
    [
        new(Tr.T("Favorites"), WallpaperSource.Favorites),
        new(Tr.T("An album"), WallpaperSource.Album),
        new(Tr.T("All photos"), WallpaperSource.Library),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlbumWallpaper))]
    public partial Option<WallpaperSource> SelectedWallpaperSource { get; set; }

    public bool IsAlbumWallpaper => SelectedWallpaperSource?.Value == WallpaperSource.Album;

    partial void OnSelectedWallpaperSourceChanged(Option<WallpaperSource> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.WallpaperSource = value.Value);
    }

    public System.Collections.ObjectModel.ObservableCollection<AlbumInfo> WallpaperAlbums { get; } = [];

    [ObservableProperty]
    public partial AlbumInfo? SelectedWallpaperAlbum { get; set; }

    partial void OnSelectedWallpaperAlbumChanged(AlbumInfo? value)
    {
        if (value is null) return;
        AppServices.Settings.Update(s =>
        {
            s.WallpaperAlbumId = value.Id;
            s.WallpaperAlbumName = value.AlbumName;
        });
    }

    async Task LoadWallpaperAlbumsAsync()
    {
        if (AppServices.Account.Client is not { } client)
            return;
        try
        {
            var albums = await client.GetAlbumsAsync(CancellationToken.None);
            foreach (var album in albums.Where(a => a.AssetCount > 0).OrderBy(a => a.AlbumName, StringComparer.CurrentCultureIgnoreCase))
                WallpaperAlbums.Add(album);
            var chosen = AppServices.Settings.Current.WallpaperAlbumId;
            SelectedWallpaperAlbum = WallpaperAlbums.FirstOrDefault(a => a.Id == chosen);
        }
        catch (Exception ex)
        {
            Log.Info($"Albums for the desktop background unavailable: {ex.Message}");
        }
    }

    public IReadOnlyList<Option<int>> WallpaperIntervals { get; } =
    [
        new(Tr.T("Every 15 minutes"), 15),
        new(Tr.T("Every 30 minutes"), 30),
        new(Tr.T("Every hour"), 60),
        new(Tr.T("Every 3 hours"), 180),
        new(Tr.T("Every 6 hours"), 360),
        new(Tr.T("Once a day"), 1440),
    ];

    public IReadOnlyList<Option<WallpaperOrder>> WallpaperOrders { get; } =
    [
        new(Tr.T("Random"), WallpaperOrder.Random),
        new(Tr.T("Oldest first"), WallpaperOrder.OldestFirst),
        new(Tr.T("Newest first"), WallpaperOrder.NewestFirst),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WallpaperOrderHint))]
    public partial Option<WallpaperOrder> SelectedWallpaperOrder { get; set; }

    public string WallpaperOrderHint => SelectedWallpaperOrder?.Value == WallpaperOrder.Random
        ? Tr.T("Landscape photos are picked first, so they fill your screen.")
        : Tr.T("One after another by date, starting again after the last one.");

    partial void OnSelectedWallpaperOrderChanged(Option<WallpaperOrder> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.WallpaperOrder = value.Value);
    }

    [ObservableProperty]
    public partial Option<int> SelectedWallpaperInterval { get; set; }

    partial void OnSelectedWallpaperIntervalChanged(Option<int> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.WallpaperIntervalMinutes = value.Value);
    }

    public string WallpaperStatus => AppServices.Wallpaper.StatusText;
    public bool IsChangingWallpaper => AppServices.Wallpaper.IsChanging;

    void OnWallpaperChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(WallpaperStatus));
        OnPropertyChanged(nameof(IsChangingWallpaper));
    });

    [RelayCommand]
    static Task ChangeWallpaperNowAsync() => AppServices.Wallpaper.ChangeNowAsync();

    // ---- Profile picture (also changes it in Immich) ----

    public bool HasPhoto => AppServices.Profile.Picture is not null;

    [ObservableProperty]
    public partial string? PhotoError { get; set; }

    void OnProfileChanged() => OnPropertyChanged(nameof(HasPhoto));

    [RelayCommand]
    void ChangePhoto()
    {
        PhotoError = null;
        _shell.Main.ShowOverlay(new ProfilePhotoViewModel(_shell.Main));
    }

    [RelayCommand]
    async Task RemovePhotoAsync()
    {
        PhotoError = null;
        try
        {
            await AppServices.Profile.RemovePictureAsync();
        }
        catch (Exception ex)
        {
            PhotoError = Tr.T("Couldn't remove your picture. {0}", NetworkErrors.Describe(ex));
        }
    }

    // ---- Account ----

    public string UserName { get; }
    public string UserEmail { get; }
    public string ServerUrl { get; }
    public string ServerVersion { get; }
    public string UserInitial => UserName.Length > 0 ? char.ToUpperInvariant(UserName[0]).ToString() : "?";

    [RelayCommand]
    void OpenServer()
    {
        if (ServerUrl.Length > 0)
            ShellLauncher.Open(ServerUrl);
    }

    [RelayCommand]
    async Task SignOutAsync()
    {
        var confirmed = await _shell.Main.ConfirmAsync(
            Tr.T("Sign out?"),
            Tr.T("Backup will stop until you sign in again. Your folders and settings are kept, and nothing is removed from Immich."),
            Tr.T("Sign out"), danger: true);
        if (!confirmed)
            return;

        await AppServices.Account.SignOutAsync();
        _shell.Main.ShowOnboarding(resumeSignIn: false);
    }

    // ---- General ----

    public bool AutostartSupported => Autostart.IsSupported;

    public string AutostartDescription => OperatingSystem.IsWindows()
        ? Tr.T("Open in the system tray when you sign in to Windows.")
        : Tr.T("Open in the system tray when you log in.");

    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_loading) return;
        Autostart.SetEnabled(value);
        AppServices.Settings.Update(s => s.LaunchAtStartup = value);
    }

    AppTheme _theme;

    // ---- Graphics ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuStatus))]
    public partial bool GpuRendering { get; set; }

    [ObservableProperty]
    public partial bool Notifications { get; set; }

    partial void OnNotificationsChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.Notifications = value);
    }

    /// <summary>When a finished backup gets a notification (Never, Only big backups, Every backup).</summary>
    public IReadOnlyList<string> BackupNoticeChoices { get; } =
        [Tr.T("Never"), Tr.T("Only big backups"), Tr.T("Every backup")];

    [ObservableProperty]
    public partial int NotifyBackupsIndex { get; set; }

    partial void OnNotifyBackupsIndexChanged(int value)
    {
        if (!_loading && value >= 0)
            AppServices.Settings.Update(s => s.NotifyBackups = (Core.Settings.BackupNotice)value);
    }

    [ObservableProperty]
    public partial bool NotifyFailures { get; set; }

    partial void OnNotifyFailuresChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.NotifyFailures = value);
    }

    [ObservableProperty]
    public partial bool ExplorerIntegration { get; set; }

    partial void OnExplorerIntegrationChanged(bool value)
    {
        if (_loading)
            return;
        AppServices.Settings.Update(s => s.ExplorerIntegration = value);
        App.ApplyExplorerIntegration(value);
    }

    public string ExplorerIntegrationText => OperatingSystem.IsWindows()
        ? Tr.T("Right-click photos or folders in File Explorer (or use Send to) to upload them.")
        : Tr.T("Adds Immich Splash to the Open with list for photos and videos.");

    [ObservableProperty]
    public partial bool AskAboutCameraCards { get; set; }

    partial void OnAskAboutCameraCardsChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s =>
            {
                s.AskAboutCameraCards = value;
                if (value)
                    s.IgnoredCameraCards.Clear(); // turning it back on asks about every card again
            });
    }

    [ObservableProperty]
    public partial bool NotifySignIn { get; set; }

    partial void OnNotifySignInChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.NotifySignIn = value);
    }

    partial void OnGpuRenderingChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.GpuRendering = value);
    }

    /// <summary>What's drawing the app now, and whether a restart is needed for the setting.</summary>
    public string GpuStatus
    {
        get
        {
            var now = Program.IsGpuActive ? Tr.T("Right now the graphics card draws the app.") : Tr.T("Right now the processor draws the app.");
            return GpuRendering != Program.GpuRequested ? now + " " + Tr.T("Restart the app to apply the change.") : now;
        }
    }

    // ---- Language ----

    public IReadOnlyList<Option<string?>> LanguageOptions { get; }

    [ObservableProperty]
    public partial Option<string?> SelectedLanguage { get; set; }

    partial void OnSelectedLanguageChanged(Option<string?> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.Language = value.Value);
        // Rebuild after this change has finished (the rebuild replaces this page).
        Dispatcher.UIThread.Post(() => (Avalonia.Application.Current as App)?.ApplyLanguage(value.Value));
    }

    public bool ThemeSystem { get => _theme == AppTheme.System; set { if (value) ThemeService.Set(AppTheme.System); } }
    public bool ThemeLight { get => _theme == AppTheme.Light; set { if (value) ThemeService.Set(AppTheme.Light); } }
    public bool ThemeDark { get => _theme == AppTheme.Dark; set { if (value) ThemeService.Set(AppTheme.Dark); } }

    void OnThemeChanged(AppTheme theme)
    {
        _theme = theme;
        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeDark));
    }

    // ---- Security: app lock ----

    AppLockMode _lockMode;

    [ObservableProperty]
    public partial bool HelloAvailable { get; set; }

    public bool LockOff { get => _lockMode == AppLockMode.Off; set { if (value) _ = SetLockModeAsync(AppLockMode.Off); } }
    public bool LockHello { get => _lockMode == AppLockMode.WindowsHello; set { if (value) _ = SetLockModeAsync(AppLockMode.WindowsHello); } }
    public bool LockPassword { get => _lockMode == AppLockMode.Password; set { if (value) _ = SetLockModeAsync(AppLockMode.Password); } }

    public bool IsLockEnabled => _lockMode != AppLockMode.Off;

    [ObservableProperty]
    public partial bool IsSettingPassword { get; set; }

    [ObservableProperty]
    public partial string NewPassword { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = "";

    [ObservableProperty]
    public partial string? LockError { get; set; }

    async Task DetectHelloAsync() => HelloAvailable = await WindowsHello.IsAvailableAsync();

    async Task SetLockModeAsync(AppLockMode mode)
    {
        LockError = null;
        IsSettingPassword = false;
        switch (mode)
        {
            case AppLockMode.Off:
                SaveLock(AppLockMode.Off, null);
                break;
            case AppLockMode.WindowsHello:
                // Make sure Hello actually works for this user before relying on it.
                var result = await WindowsHello.VerifyAsync(_shell.Main.GetWindowHandle?.Invoke() ?? IntPtr.Zero, Tr.T("Confirm it's you to turn on the app lock"));
                if (result == HelloResult.Verified)
                    SaveLock(AppLockMode.WindowsHello, null);
                else if (result != HelloResult.Canceled)
                    LockError = OperatingSystem.IsLinux()
                        ? Tr.T("Your Linux login couldn't be checked. It needs a desktop that shows sign-in prompts (like KDE Plasma or GNOME). Or use a password.")
                        : Tr.T("Windows Hello isn't set up on this PC. Set it up in Windows Settings → Accounts → Sign-in options, or use a password.");
                break;
            case AppLockMode.Password:
                NewPassword = "";
                ConfirmPassword = "";
                IsSettingPassword = true;
                break;
        }
        RaiseLockProperties();
    }

    [RelayCommand]
    async Task SavePasswordAsync()
    {
        LockError = null;
        if (NewPassword.Length < 4)
        {
            LockError = Tr.T("Use at least 4 characters.");
            return;
        }
        if (NewPassword != ConfirmPassword)
        {
            LockError = Tr.T("The passwords don't match.");
            return;
        }
        var password = NewPassword;
        var hash = await Task.Run(() => PasswordHasher.Hash(password));
        NewPassword = "";
        ConfirmPassword = "";
        IsSettingPassword = false;
        SaveLock(AppLockMode.Password, hash);
        RaiseLockProperties();
    }

    [RelayCommand]
    void CancelPassword()
    {
        IsSettingPassword = false;
        NewPassword = "";
        ConfirmPassword = "";
        LockError = null;
        RaiseLockProperties();
    }

    [RelayCommand]
    void LockNow() => _shell.Main.Lock();

    void SaveLock(AppLockMode mode, string? hash)
    {
        _lockMode = mode;
        AppServices.Settings.Update(s =>
        {
            s.AppLock = mode;
            s.LockPasswordHash = mode == AppLockMode.Password ? hash : null;
        });
    }

    void RaiseLockProperties()
    {
        OnPropertyChanged(nameof(LockOff));
        OnPropertyChanged(nameof(LockHello));
        OnPropertyChanged(nameof(LockPassword));
        OnPropertyChanged(nameof(IsLockEnabled));
    }

    // ---- Backup ----

    public IReadOnlyList<Option<int>> Concurrency { get; } =
    [
        new(Tr.T("1 at a time"), 1),
        new(Tr.T("2 at a time"), 2),
        new(Tr.T("3 at a time"), 3),
        new(Tr.T("4 at a time"), 4),
        new(Tr.T("6 at a time"), 6),
    ];

    [ObservableProperty]
    public partial Option<int> SelectedConcurrency { get; set; }

    partial void OnSelectedConcurrencyChanged(Option<int> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.MaxConcurrentUploads = value.Value);
    }

    public IReadOnlyList<Option<int>> SpeedLimits { get; } =
    [
        new(Tr.T("Unlimited"), 0),
        new(Tr.T("1 MB/s"), 1024),
        new(Tr.T("2 MB/s"), 2 * 1024),
        new(Tr.T("5 MB/s"), 5 * 1024),
        new(Tr.T("10 MB/s"), 10 * 1024),
        new(Tr.T("25 MB/s"), 25 * 1024),
        new(Tr.T("50 MB/s"), 50 * 1024),
    ];

    [ObservableProperty]
    public partial Option<int> SelectedSpeedLimit { get; set; }

    partial void OnSelectedSpeedLimitChanged(Option<int> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.UploadLimitKBps = value.Value);
    }

    public IReadOnlyList<Option<int>> RescanIntervals { get; } =
    [
        new(Tr.T("Every 15 minutes"), 15),
        new(Tr.T("Every 30 minutes"), 30),
        new(Tr.T("Every hour"), 60),
        new(Tr.T("Every 3 hours"), 180),
        new(Tr.T("Every 6 hours"), 360),
    ];

    [ObservableProperty]
    public partial Option<int> SelectedRescan { get; set; }

    partial void OnSelectedRescanChanged(Option<int> value)
    {
        if (_loading || value is null) return;
        AppServices.Settings.Update(s => s.RescanIntervalMinutes = value.Value);
    }

    [ObservableProperty]
    public partial bool PauseOnBattery { get; set; }

    // ---- Backup hours ----

    /// <summary>Every half hour of the day, in the PC's time format.</summary>
    public IReadOnlyList<Option<int>> TimeChoices { get; } =
        Enumerable.Range(0, 48).Select(i => new Option<int>(Core.Sync.BackupSchedule.Format(i * 30), i * 30)).ToList();

    [ObservableProperty]
    public partial bool BackupWindowEnabled { get; set; }

    [ObservableProperty]
    public partial Option<int>? BackupWindowStart { get; set; }

    [ObservableProperty]
    public partial Option<int>? BackupWindowEnd { get; set; }

    partial void OnBackupWindowEnabledChanged(bool value)
    {
        if (!_loading)
            AppServices.Settings.Update(s => s.BackupWindowEnabled = value);
    }

    partial void OnBackupWindowStartChanged(Option<int>? value)
    {
        if (!_loading && value is not null)
            AppServices.Settings.Update(s => s.BackupWindowStartMinutes = value.Value);
    }

    partial void OnBackupWindowEndChanged(Option<int>? value)
    {
        if (!_loading && value is not null)
            AppServices.Settings.Update(s => s.BackupWindowEndMinutes = value.Value);
    }

    partial void OnPauseOnBatteryChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.PauseOnBattery = value);
    }

    [ObservableProperty]
    public partial bool StackRawWithJpeg { get; set; }

    partial void OnStackRawWithJpegChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.StackRawWithJpeg = value);
    }

    // ---- Troubleshooting ----

    [RelayCommand]
    async Task RecheckAllAsync()
    {
        var confirmed = await _shell.Main.ConfirmAsync(
            Tr.T("Re-check all files?"),
            Tr.T("Every file in your folders will be compared with your Immich library again. Nothing is uploaded twice, but this can take a while for large libraries."),
            Tr.T("Re-check"));
        if (confirmed)
            AppServices.Engine.ResetAndRescan();
    }

    [RelayCommand]
    static void OpenLogs() => ShellLauncher.Open(AppPaths.LogsDir);

    [RelayCommand]
    static void OpenDataFolder() => ShellLauncher.Open(AppPaths.DataDir);

    [ObservableProperty]
    public partial string? DiagnosticsMessage { get; set; }

    [RelayCommand]
    static void ReportBug() => ShellLauncher.Open(Diagnostics.BugReportUrl());

    [RelayCommand]
    static void GiveFeedback() => ShellLauncher.Open(Diagnostics.FeedbackUrl());

    /// <summary>Copies versions, setup and recent warnings (with personal details taken out) for a bug report.</summary>
    [RelayCommand]
    async Task CopyDiagnosticsAsync()
    {
        var text = await Task.Run(Diagnostics.Collect);
        if (_shell.Main.CopyText is { } copy)
        {
            await copy(text);
            DiagnosticsMessage = Tr.T("Copied. Paste it into your bug report.");
        }
    }

    // ---- About & updates ----

    public string Version => Tr.T("Version {0}", AppInfo.Version);
    public bool UpdatesEnabled => AppServices.Updates.IsEnabled;
    public bool CanSelfUpdate => UpdateService.IsInstalledCopy;

    public string UpdateStatus => AppServices.Updates.StatusText;
    public bool HasUpdateStatus => UpdateStatus.Length > 0;
    public bool IsCheckingUpdates => AppServices.Updates.State is UpdateState.Checking or UpdateState.Downloading;
    public bool UpdateReady => AppServices.Updates.State == UpdateState.Ready;
    public bool UpdateAvailable => AppServices.Updates.State == UpdateState.Available;
    public bool CanCheck => !UpdateReady && !UpdateAvailable;

    void OnUpdatesChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(HasUpdateStatus));
        OnPropertyChanged(nameof(IsCheckingUpdates));
        OnPropertyChanged(nameof(UpdateReady));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(CanCheck));
    });

    [RelayCommand]
    static Task CheckForUpdatesAsync() => AppServices.Updates.CheckAsync();

    [RelayCommand]
    static void InstallUpdate() => AppServices.Updates.InstallAndRestart();

    [RelayCommand]
    static void OpenReleasePage() => AppServices.Updates.OpenReleasePage();

    /// <summary>Running as a Flatpak: updates come through Flatpak, not the app.</summary>
    public bool IsFlatpak => Core.Platform.Flatpak.IsRunning;

    /// <summary>Where updates come from, so it's clear nothing else is involved.</summary>
    public string UpdateSourceText => Tr.T(
        "Updates come only from the official GitHub releases of Immich Splash ({0}). Every download is checked against the fingerprint GitHub publishes for it before it's installed.",
        "github.com/" + AppInfo.UpdateRepository);

    /// <summary>
    /// This version's release notes. Betas are replaced by newer ones (their pages go away), so they
    /// get the list of all releases instead.
    /// </summary>
    [RelayCommand]
    static void OpenReleaseNotes() => Core.Platform.ShellLauncher.Open(
        Core.Updates.ReleaseVersion.Parse(AppInfo.Version) is { IsPrerelease: false }
            ? $"{AppInfo.ProjectUrl}/releases/tag/v{AppInfo.Version}"
            : $"{AppInfo.ProjectUrl}/releases");

    [RelayCommand]
    static void OpenProjectPage() => Core.Platform.ShellLauncher.Open(AppInfo.ProjectUrl);

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.CheckForUpdates = value);
    }

    // ---- Update channel ----

    UpdateChannel _channel;

    public bool ChannelStable { get => _channel == UpdateChannel.Stable; set { if (value) SetChannel(UpdateChannel.Stable); } }
    public bool ChannelBeta { get => _channel == UpdateChannel.Beta; set { if (value) SetChannel(UpdateChannel.Beta); } }

    public string ChannelDescription => _channel == UpdateChannel.Beta
        ? Tr.T("You get new features first. Betas can have rough edges. Switching back to Stable takes effect with the next stable version.")
        : Tr.T("Get the beta to try new features before everyone else.");

    void SetChannel(UpdateChannel channel)
    {
        if (_channel == channel)
            return;
        _channel = channel;
        AppServices.Settings.Update(s => s.UpdateChannel = channel);
        OnPropertyChanged(nameof(ChannelStable));
        OnPropertyChanged(nameof(ChannelBeta));
        OnPropertyChanged(nameof(ChannelDescription));
        if (!_loading)
            _ = AppServices.Updates.RecheckAsync();
    }

    [ObservableProperty]
    public partial bool AutoInstallUpdates { get; set; }

    partial void OnAutoInstallUpdatesChanged(bool value)
    {
        if (_loading) return;
        AppServices.Settings.Update(s => s.AutoInstallUpdates = value);
    }
}
