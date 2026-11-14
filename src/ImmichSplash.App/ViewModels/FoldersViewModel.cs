using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Sync;
using Material.Icons;

namespace ImmichSplash.App.ViewModels;

public sealed partial class FoldersViewModel : ViewModelBase, IDisposable
{
    readonly ShellViewModel _shell;
    DateTime _lastStatsRefresh;

    public FoldersViewModel(ShellViewModel shell)
    {
        _shell = shell;
        foreach (var folder in AppServices.Settings.Current.Folders)
            Folders.Add(new FolderItemViewModel(this, folder));
        IsEmpty = Folders.Count == 0;
        RefreshStats();
        AppServices.Engine.StatusChanged += OnStatusChanged;
    }

    public ObservableCollection<FolderItemViewModel> Folders { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string? AddError { get; set; }

    [RelayCommand]
    async Task AddFolderAsync()
    {
        AddError = null;
        var path = await _shell.Main.PickFolderAsync(Tr.T("Choose a folder to back up"));
        if (path is null)
            return;

        var settings = AppServices.Settings.Current;
        if (FolderRules.ValidateNewFolder(path, settings.Folders) is { } error)
        {
            AddError = error;
            return;
        }
        if (Core.Platform.CameraCardWatcher.IsOnRemovableDrive(path))
        {
            // A folder on a USB drive or card only backs up while it's plugged in. Often a one-time upload is what's wanted.
            var choice = await _shell.Main.ChooseAsync(Tr.T("This folder is on a removable drive"),
                Tr.T("It's only backed up while the drive is plugged in; when it isn't, the folder shows as not found (nothing is removed from Immich). To copy what's on it just once, upload it instead."),
                Tr.T("Upload once"), Tr.T("Keep backing it up"), Tr.T("Cancel"));
            if (choice == true)
            {
                _shell.Main.OfferUpload([path]);
                return;
            }
            if (choice is null)
                return;
        }

        var folder = new SyncFolder { Path = FolderRules.Normalize(path) };
        AppServices.Settings.Update(s => s.Folders.Add(folder));
        AppServices.Activity.Add(ActivityKind.Info, Tr.T("Added folder “{0}”", folder.DisplayName));
        var item = new FolderItemViewModel(this, folder) { IsExpanded = true };
        Folders.Add(item);
        IsEmpty = false;
        RefreshStats();
    }

    internal async Task RemoveAsync(FolderItemViewModel item)
    {
        var confirmed = await _shell.Main.ConfirmAsync(
            Tr.T("Stop backing up “{0}”?", item.Name),
            Tr.T("Photos and videos already uploaded stay in Immich. New files in this folder won't be backed up anymore."),
            Tr.T("Remove folder"), danger: true);
        if (!confirmed)
            return;

        AppServices.Settings.Update(s => s.Folders.RemoveAll(f => f.Id == item.Id));
        AppServices.Activity.Add(ActivityKind.Info, Tr.T("Removed folder “{0}”", item.Name));
        Folders.Remove(item);
        IsEmpty = Folders.Count == 0;
    }

    internal static void Save(FolderItemViewModel item)
    {
        AppServices.Settings.Update(s =>
        {
            var index = s.Folders.FindIndex(f => f.Id == item.Id);
            if (index >= 0)
                s.Folders[index] = item.ToModel();
        });
    }

    void OnStatusChanged(SyncStatus status) => Dispatcher.UIThread.Post(() =>
    {
        if (DateTime.UtcNow - _lastStatsRefresh > TimeSpan.FromSeconds(2) || !status.IsBusy)
            RefreshStats();
    });

    void RefreshStats()
    {
        _lastStatsRefresh = DateTime.UtcNow;
        Dictionary<string, StatusCounts> counts;
        try
        {
            counts = AppServices.Database.GetFolderCounts();
        }
        catch
        {
            return;
        }
        foreach (var folder in Folders)
        {
            counts.TryGetValue(folder.Id, out var c);
            folder.UpdateStats(c);
        }
    }

    public void Dispose()
    {
        AppServices.Engine.StatusChanged -= OnStatusChanged;
        foreach (var folder in Folders)
            folder.FlushPendingSave();
    }
}

/// <summary>One folder card. Edits are saved automatically after a short pause in typing.</summary>
public sealed partial class FolderItemViewModel : ViewModelBase
{
    readonly FoldersViewModel _owner;
    readonly DispatcherTimer _saveTimer;
    readonly bool _loading;

    public FolderItemViewModel(FoldersViewModel owner, SyncFolder folder)
    {
        _owner = owner;
        _loading = true;
        Id = folder.Id;
        Path = folder.Path;
        Name = folder.DisplayName;
        Enabled = folder.Enabled;
        IncludeSubfolders = folder.IncludeSubfolders;
        IncludePhotos = folder.IncludePhotos;
        IncludeVideos = folder.IncludeVideos;
        AlbumName = folder.AlbumName ?? "";
        ExcludePatterns = string.Join(Environment.NewLine, folder.ExcludePatterns);
        IsMissing = !Directory.Exists(folder.Path);
        _loading = false;

        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            SaveHint = null;
        };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            FoldersViewModel.Save(this);
            SaveHint = Tr.T("Saved \u00B7 applied to backup");
            _hintTimer.Stop();
            _hintTimer.Start();
        };
    }

    readonly DispatcherTimer _hintTimer;

    /// <summary>Little "Saving / Saved" note so edits feel acknowledged.</summary>
    [ObservableProperty]
    public partial string? SaveHint { get; set; }

    const string ScreenshotPattern = "Screenshot*";

    /// <summary>One-click exclusion for screenshots (Windows "Screenshots" folder and Screenshot_*.png style files).</summary>
    public bool SkipScreenshots
    {
        get => PatternLines().Any(p => string.Equals(p, ScreenshotPattern, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value == SkipScreenshots)
                return;
            var lines = PatternLines().Where(p => !string.Equals(p, ScreenshotPattern, StringComparison.OrdinalIgnoreCase)).ToList();
            if (value)
                lines.Insert(0, ScreenshotPattern);
            ExcludePatterns = string.Join(Environment.NewLine, lines);
        }
    }

    List<string> PatternLines() =>
        ExcludePatterns.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public string Id { get; }
    public string Path { get; }
    public string Name { get; }
    public bool IsMissing { get; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool IncludeSubfolders { get; set; }

    [ObservableProperty]
    public partial bool IncludePhotos { get; set; }

    [ObservableProperty]
    public partial bool IncludeVideos { get; set; }

    [ObservableProperty]
    public partial string AlbumName { get; set; } = "";

    [ObservableProperty]
    public partial string ExcludePatterns { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandIcon))]
    public partial bool IsExpanded { get; set; }

    public MaterialIconKind ExpandIcon => IsExpanded ? MaterialIconKind.ChevronUp : MaterialIconKind.ChevronDown;

    [ObservableProperty]
    public partial string StatsText { get; set; } = "";

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial bool IsComplete { get; set; }

    [ObservableProperty]
    public partial string? IssueText { get; set; }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || _saveTimer is null)
            return;
        if (e.PropertyName == nameof(ExcludePatterns))
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SkipScreenshots)));
        if (e.PropertyName is nameof(Enabled) or nameof(IncludeSubfolders) or nameof(IncludePhotos) or nameof(IncludeVideos) or nameof(AlbumName) or nameof(ExcludePatterns))
        {
            SaveHint = Tr.T("Saving\u2026");
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    public void FlushPendingSave()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            FoldersViewModel.Save(this);
        }
    }

    public SyncFolder ToModel() => new()
    {
        Id = Id,
        Path = Path,
        Enabled = Enabled,
        IncludeSubfolders = IncludeSubfolders,
        IncludePhotos = IncludePhotos,
        IncludeVideos = IncludeVideos,
        AlbumName = string.IsNullOrWhiteSpace(AlbumName) ? null : AlbumName.Trim(),
        ExcludePatterns = ExcludePatterns
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
    };

    public void UpdateStats(StatusCounts c)
    {
        Percent = c.Total == 0 ? 0 : 100.0 * c.BackedUp / c.Total;
        IsComplete = c.Total > 0 && c.BackedUp == c.Total;
        StatsText = IsMissing
            ? Tr.T("Folder not found — is the drive connected?")
            : !Enabled
                ? Tr.T("Backup turned off for this folder")
                : c.Total == 0
                    ? Tr.T("No photos or videos found yet")
                    : Tr.T("{0} of {1} backed up", Format.Count(c.BackedUp), Format.Count(c.Total));
        IssueText = c.Failed > 0 ? Tr.T("{0} couldn't be uploaded", Format.Plural(c.Failed, "file")) : null;
    }

    [RelayCommand]
    void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    void OpenFolder() => ShellLauncher.Open(Path);

    [RelayCommand]
    Task Remove() => _owner.RemoveAsync(this);
}
