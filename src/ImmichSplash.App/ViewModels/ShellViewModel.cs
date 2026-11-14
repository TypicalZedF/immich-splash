using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Sync;
using Material.Icons;

namespace ImmichSplash.App.ViewModels;

public enum ShellPage
{
    Photos,
    Explore,
    Map,
    Sharing,
    Albums,
    Favorites,
    Archive,
    Locked,
    Trash,
    Utilities,
    Overview,
    Folders,
    Activity,
    Settings,
}

public sealed partial class NavItem(ShellPage page, string title, MaterialIconKind icon, MaterialIconKind activeIcon, Func<ViewModelBase> create) : ObservableObject
{
    readonly Lazy<ViewModelBase> _viewModel = new(create);

    public ShellPage Page { get; } = page;
    public string Title { get; } = title;
    public MaterialIconKind Icon { get; } = icon;
    public MaterialIconKind ActiveIcon { get; } = activeIcon;
    public ViewModelBase ViewModel => _viewModel.Value;
    public bool IsCreated => _viewModel.IsValueCreated;

    /// <summary>Has something to show in a drop-down under it in the sidebar (Albums: the ones you used last).</summary>
    [ObservableProperty]
    public partial bool HasChildren { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronIcon))]
    public partial bool IsExpanded { get; set; } = true;

    public MaterialIconKind ChevronIcon => IsExpanded ? MaterialIconKind.ChevronUp : MaterialIconKind.ChevronDown;

    [RelayCommand]
    void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>One of the albums you used last, listed under Albums in the sidebar.</summary>
public sealed partial class NavAlbum(AlbumInfo album) : ObservableObject
{
    public AlbumInfo Album { get; } = album;
    public string Title => Album.AlbumName;

    [ObservableProperty]
    public partial Bitmap? Cover { get; set; }
}

/// <summary>A group heading in the sidebar ("LIBRARY"); an empty title is just a divider.</summary>
public sealed class NavHeader(string title)
{
    public string Title { get; } = title;
    public bool IsDivider => Title.Length == 0;
}

/// <summary>Signed-in layout, modeled on the Immich web app: top bar, sidebar, storage and server status.</summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    readonly CancellationTokenSource _lifetime = new();

    public ShellViewModel(MainWindowViewModel main)
    {
        Main = main;
        NavItem Library(ShellPage page, string title, MaterialIconKind icon, MaterialIconKind active, LibraryPage section) =>
            new(page, title, icon, active, () =>
            {
                var photos = new PhotosViewModel(this, section);
                photos.LeaveRequested = () => Navigate(ShellPage.Photos);
                return photos;
            });

        // Like the Immich web app's sidebar, with the mobile app's Library group, plus this PC's backup.
        NavEntries = new ObservableCollection<object>
        {
            Library(ShellPage.Photos, Tr.T("Photos"), MaterialIconKind.ImageMultipleOutline, MaterialIconKind.ImageMultiple, LibraryPage.Timeline),
            Library(ShellPage.Explore, Tr.T("Explore"), MaterialIconKind.CompassOutline, MaterialIconKind.Compass, LibraryPage.Explore),
            Library(ShellPage.Map, Tr.T("Map"), MaterialIconKind.MapOutline, MaterialIconKind.Map, LibraryPage.Map),
            Library(ShellPage.Sharing, Tr.T("Sharing"), MaterialIconKind.AccountMultipleOutline, MaterialIconKind.AccountMultiple, LibraryPage.Sharing),
            Library(ShellPage.Albums, Tr.T("Albums"), MaterialIconKind.ImageAlbum, MaterialIconKind.ImageAlbum, LibraryPage.Albums),
            new NavHeader(Tr.T("LIBRARY")),
            Library(ShellPage.Favorites, Tr.T("Favorites"), MaterialIconKind.HeartOutline, MaterialIconKind.Heart, LibraryPage.Favorites),
            Library(ShellPage.Archive, Tr.T("Archive"), MaterialIconKind.ArchiveOutline, MaterialIconKind.Archive, LibraryPage.Archive),
            Library(ShellPage.Locked, Tr.T("Locked folder"), MaterialIconKind.LockOutline, MaterialIconKind.Lock, LibraryPage.Locked),
            Library(ShellPage.Utilities, Tr.T("Utilities"), MaterialIconKind.Toolbox, MaterialIconKind.Toolbox, LibraryPage.Utilities),
            Library(ShellPage.Trash, Tr.T("Trash"), MaterialIconKind.TrashCanOutline, MaterialIconKind.TrashCan, LibraryPage.Trash),
            new NavHeader(Tr.T("THIS PC")),
            new NavItem(ShellPage.Overview, Tr.T("Backup"), MaterialIconKind.CloudUploadOutline, MaterialIconKind.CloudUpload, () => new OverviewViewModel(this)),
            new NavItem(ShellPage.Folders, Tr.T("Folders"), MaterialIconKind.FolderOutline, MaterialIconKind.Folder, () => new FoldersViewModel(this)),
            new NavItem(ShellPage.Activity, Tr.T("Activity"), MaterialIconKind.History, MaterialIconKind.History, () => new ActivityViewModel(this)),
        };
        // Settings live in the top bar (like the web and mobile apps' account menu), not the sidebar.
        NavItems = [.. NavEntries.OfType<NavItem>(),
            new NavItem(ShellPage.Settings, Tr.T("Settings"), MaterialIconKind.CogOutline, MaterialIconKind.Cog, () => new SettingsViewModel(this))];
        SelectedNav = NavItems[0];

        var settings = AppServices.Settings.Current;
        LoadAccount(settings);
        IsLockEnabled = settings.AppLock != AppLockMode.Off;
        UpdateThemeIcon();
        ApplyStatus(AppServices.Engine.Status);

        AppServices.Engine.StatusChanged += OnStatusChanged;
        AppServices.Settings.Changed += OnSettingsChanged;
        AppServices.AlbumsChanged += OnAlbumsChanged;
        AlbumsNav.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavItem.IsExpanded))
                RebuildRecentAlbums();
        };
        _ = LoadAlbumCacheAsync();
        ThemeService.Changed += OnThemeChanged;
        _ = LoadStorageAsync();
    }

    public MainWindowViewModel Main { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>The sidebar: pages and group headings.</summary>
    public ObservableCollection<object> NavEntries { get; }

    [ObservableProperty]
    public partial NavItem SelectedNav { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    /// <summary>
    /// What the sidebar highlights. Settings isn't in the sidebar, so while it's open nothing is
    /// highlighted; the sidebar letting go of its selection must not close the page you're on.
    /// </summary>
    public object? SidebarSelection
    {
        get
        {
            // An album opened from the recent list keeps its own highlight, not the Albums page's.
            if (ReferenceEquals(SelectedNav, AlbumsNav) && AlbumsNav is { IsCreated: true, ViewModel: PhotosViewModel { Drill.Album: { } open } }
                && NavEntries.OfType<NavAlbum>().FirstOrDefault(a => a.Album.Id == open.Id) is { } recentEntry)
                return recentEntry;
            return NavEntries.Contains(SelectedNav) ? SelectedNav : null;
        }
        set
        {
            if (value is NavAlbum recent)
            {
                OpenAlbumFromSidebar(recent.Album);
                OnPropertyChanged();
            }
            else if (value is NavItem item && !ReferenceEquals(item, SelectedNav))
                SelectedNav = item;
            else
                OnPropertyChanged(); // headers can't be selected; keep the highlight where it was
        }
    }

    partial void OnSelectedNavChanged(NavItem oldValue, NavItem newValue)
    {
        OnPropertyChanged(nameof(SidebarSelection));
        if (newValue is null)
            return;
        // Leaving the locked folder locks it again, like the Immich apps.
        if (oldValue is { IsCreated: true, ViewModel: PhotosViewModel left })
            left.CommitRecentAlbum();
        if (oldValue is { IsCreated: true, ViewModel: PhotosViewModel { IsLockedFolder: true } locked })
            _ = locked.LockAsync();
        // Coming back to a page: show what changed meanwhile (e.g. a link shared from Photos).
        if (newValue is { IsCreated: true, ViewModel: PhotosViewModel shown })
            shown.OnShownAgain();
        if (ReferenceEquals(newValue, AlbumsNav) && newValue.ViewModel is PhotosViewModel albumsPage)
            WatchAlbumsPage(albumsPage); // albums opened from the page itself highlight their recent entry too
        // The search box describes the results on screen; on another page it's just stale text.
        CloseSuggestions();
        if (_searchPage is not null && !ReferenceEquals(newValue.ViewModel, _searchPage))
        {
            StopWatchingResults();
            SetSearchTextQuietly("");
        }
        CurrentPage = newValue?.ViewModel;
    }

    // ---- Search (top bar, like the web app) ----

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [RelayCommand]
    void Search()
    {
        // Enter: the highlighted suggestion, or a smart search for the text.
        if (SelectedSuggestion is { } highlighted)
        {
            Choose(highlighted);
            return;
        }
        var text = SearchText.Trim();
        if (text.Length > 0)
            Run(new SearchHistoryEntry { Kind = SearchKind.Text, Text = text });
    }

    [RelayCommand]
    void ShowBackup() => Navigate(ShellPage.Overview);

    public void Navigate(ShellPage page) => SelectedNav = NavItems.First(n => n.Page == page);

    /// <summary>The current page's sidebar entry was clicked again: back to the page's start.</summary>
    public void ReturnToStart()
    {
        CloseSuggestions();
        if (SelectedNav is { IsCreated: true, ViewModel: PhotosViewModel photos })
            photos.ReturnToStart();
    }

    // ---- Top bar ----

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string UserEmail { get; set; } = "";

    [ObservableProperty]
    public partial string UserInitial { get; set; } = "";

    [ObservableProperty]
    public partial string ServerHost { get; set; } = "";

    [ObservableProperty]
    public partial string ServerVersion { get; set; } = "";

    [ObservableProperty]
    public partial MaterialIconKind ThemeIcon { get; set; }

    [ObservableProperty]
    public partial string ThemeTip { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLockEnabled { get; set; }

    void LoadAccount(AppSettings settings)
    {
        UserName = string.IsNullOrWhiteSpace(settings.UserName) ? settings.UserEmail ?? "" : settings.UserName;
        UserEmail = settings.UserEmail ?? "";
        UserInitial = UserName.Length > 0 ? char.ToUpperInvariant(UserName[0]).ToString() : "?";
        ServerHost = Uri.TryCreate(settings.WebUrl, UriKind.Absolute, out var web) ? web.Host : "";
        ServerVersion = settings.ServerVersion ?? "";
    }

    void UpdateThemeIcon()
    {
        var dark = ThemeService.IsDark;
        ThemeIcon = dark ? MaterialIconKind.WeatherSunny : MaterialIconKind.WeatherNight;
        ThemeTip = dark ? Tr.T("Switch to light theme") : Tr.T("Switch to dark theme");
    }

    [RelayCommand]
    void ToggleTheme() => ThemeService.Toggle();

    [RelayCommand]
    void LockNow() => Main.Lock();

    [RelayCommand]
    void OpenWeb()
    {
        if (AppServices.Settings.Current.WebUrl is { } url)
            ShellLauncher.Open(url);
    }

    [RelayCommand]
    void OpenAccount() => Navigate(ShellPage.Settings);

    [RelayCommand]
    void OpenSettings() => Navigate(ShellPage.Settings);

    void OnThemeChanged(AppTheme _) => UpdateThemeIcon();

    // ---- Sidebar footer: storage + server status (as in Immich) ----

    [ObservableProperty]
    public partial bool HasStorage { get; set; }

    [ObservableProperty]
    public partial double StoragePercent { get; set; }

    [ObservableProperty]
    public partial string StorageText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsServerOnline { get; set; } = true;

    [ObservableProperty]
    public partial string ServerStatusText { get; set; } = Tr.T("Online");

    async Task LoadStorageAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (AppServices.Account.Client is { } client)
            {
                try
                {
                    var storage = await client.GetStorageAsync(_lifetime.Token);
                    StoragePercent = storage.Fraction * 100;
                    StorageText = Tr.T("{0} of {1} used", Format.Bytes(storage.Used), Format.Bytes(storage.Total));
                    HasStorage = storage.Total > 0;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // e.g. an API key without the server.storage permission: just hide the meter.
                    Log.Info($"Storage info unavailable: {ex.Message}");
                    HasStorage = false;
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---- Status ----

    [ObservableProperty]
    public partial StatusView Status { get; set; } = null!;

    void OnStatusChanged(SyncStatus status) => Dispatcher.UIThread.Post(() => ApplyStatus(status));

    // ---- The sidebar's drop-down of the albums you used last ----

    const int RecentAlbumCount = 3;
    readonly Dictionary<string, AlbumInfo> _albumCache = [];
    string _recentKey = "";
    ThumbnailLoader? _navThumbnails;

    NavItem AlbumsNav => NavItems.First(n => n.Page == ShellPage.Albums);

    void OnAlbumsChanged() => Dispatcher.UIThread.Post(() => _ = LoadAlbumCacheAsync());

    async Task LoadAlbumCacheAsync()
    {
        if (AppServices.Account.Client is not { } client)
            return;
        try
        {
            var albums = await client.GetAlbumsAsync(_lifetime.Token);
            _albumCache.Clear();
            foreach (var album in albums)
                _albumCache[album.Id] = album;
            _navThumbnails = new ThumbnailLoader(client);
            _recentKey = "";
            RebuildRecentAlbums();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Recent albums unavailable: {ex.Message}");
        }
    }

    void RebuildRecentAlbums()
    {
        var recent = AppServices.Settings.Current.RecentAlbumIds.Where(_albumCache.ContainsKey).Take(RecentAlbumCount).ToList();
        AlbumsNav.HasChildren = recent.Count > 0; // no chevron when there is nothing to show
        // Only which albums are listed matters, not their order: opening one of them puts it first in the settings,
        // and moving it to the top under the pointer would turn picking from the list into a chase.
        var key = AlbumsNav.IsExpanded ? string.Join(",", recent.Order(StringComparer.Ordinal)) : "-";
        if (key == _recentKey)
            return;
        _recentKey = key;
        foreach (var old in NavEntries.OfType<NavAlbum>().ToList())
        {
            NavEntries.Remove(old);
            old.Cover?.Dispose();
        }
        if (!AlbumsNav.IsExpanded)
            return;
        var at = NavEntries.IndexOf(AlbumsNav) + 1;
        foreach (var id in recent)
        {
            var entry = new NavAlbum(_albumCache[id]);
            NavEntries.Insert(at++, entry);
            _ = LoadNavCoverAsync(entry);
        }
        OnPropertyChanged(nameof(SidebarSelection));
    }

    async Task LoadNavCoverAsync(NavAlbum entry)
    {
        if (_navThumbnails is not { } loader || entry.Album.AlbumThumbnailAssetId is not { } cover)
            return;
        try
        {
            entry.Cover = await loader.LoadAsync(cover, AssetImageSize.Thumbnail, 64, _lifetime.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Sidebar cover failed: {ex.Message}");
        }
    }

    void OpenAlbumFromSidebar(AlbumInfo album)
    {
        Navigate(ShellPage.Albums);
        if (AlbumsNav.ViewModel is PhotosViewModel albums)
        {
            WatchAlbumsPage(albums);
            albums.OpenAlbum(album);
        }
        OnPropertyChanged(nameof(SidebarSelection));
    }

    bool _watchingAlbumsPage;

    /// <summary>Going back from the album (or opening another) moves the sidebar highlight along with it.</summary>
    void WatchAlbumsPage(PhotosViewModel albums)
    {
        if (_watchingAlbumsPage)
            return;
        _watchingAlbumsPage = true;
        albums.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PhotosViewModel.Drill))
                OnPropertyChanged(nameof(SidebarSelection));
        };
    }

    void OnSettingsChanged(AppSettings settings) => Dispatcher.UIThread.Post(() =>
    {
        RebuildRecentAlbums();
        LoadAccount(settings);
        IsLockEnabled = settings.AppLock != AppLockMode.Off;
        ApplyStatus(AppServices.Engine.Status);
    });

    void ApplyStatus(SyncStatus status)
    {
        var view = StatusPresenter.Describe(status, AppServices.Settings.Current);
        if (view != Status)
            Status = view;

        IsServerOnline = status.State is not (SyncState.Offline or SyncState.AuthError);
        ServerStatusText = status.State switch
        {
            SyncState.Offline => Tr.T("Offline"),
            SyncState.AuthError => Tr.T("Signed out"),
            _ => Tr.T("Online"),
        };
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        AppServices.Engine.StatusChanged -= OnStatusChanged;
        AppServices.Settings.Changed -= OnSettingsChanged;
        AppServices.AlbumsChanged -= OnAlbumsChanged;
        ThemeService.Changed -= OnThemeChanged;
        foreach (var item in NavItems.Where(n => n.IsCreated))
            (item.ViewModel as IDisposable)?.Dispose();
    }
}
