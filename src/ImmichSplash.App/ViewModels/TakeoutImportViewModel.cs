using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Import;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.App.ViewModels;

/// <summary>Utilities → "Import from Google Photos": pick the Takeout .zip files (or the unpacked folder) and go.</summary>
public sealed partial class TakeoutImportViewModel(PhotosViewModel owner, MainWindowViewModel? main) : ViewModelBase
{
    List<string> _sources = [];
    List<TakeoutItem>? _items;
    CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial string Summary { get; set; } = Tr.T("Download your photos from takeout.google.com (choose Google Photos, .zip files), then choose the files here.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanChoose))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public bool CanStart => !IsWorking && _items is { Count: > 0 };
    public bool CanChoose => !IsWorking;

    [RelayCommand]
    async Task ChooseZipsAsync()
    {
        if (main?.PickFiles is not { } pick)
            return;
        var files = await pick(Tr.T("Choose your Google Takeout .zip files"), "*.zip");
        if (files.Count > 0)
            await ScanAsync(files.ToList());
    }

    [RelayCommand]
    async Task ChooseFolderAsync()
    {
        if (main is null || await main.PickFolderAsync(Tr.T("Choose your unpacked Google Takeout folder")) is not { } folder)
            return;
        await ScanAsync([folder]);
    }

    async Task ScanAsync(List<string> sources)
    {
        _sources = sources;
        IsWorking = true;
        Status = Tr.T("Reading your export…");
        try
        {
            _items = await Task.Run(() => GoogleTakeout.Scan(_sources, MediaTypes.Default));
            var albums = _items.Select(i => i.Album).Where(a => a is not null).Distinct().Count();
            var withPlace = _items.Count(i => i.Metadata?.Latitude is not null);
            Summary = _items.Count == 0
                ? Tr.T("No photos or videos found there. Choose the .zip files from Takeout, or the folder they unpacked to.")
                : Tr.T("Found {0} in {1}, {2} with a place. Ready to import.",
                    Format.Plural(_items.Count, Tr.T("photo or video"), Tr.T("photos and videos")),
                    Format.Plural(albums, Tr.T("album"), Tr.T("albums")), Format.Count(withPlace));
            Status = "";
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the Takeout export failed", ex);
            Status = Tr.T("Couldn't read the export. {0}", ex.Message);
            _items = null;
        }
        finally
        {
            IsWorking = false;
            OnPropertyChanged(nameof(CanStart));
        }
    }

    [RelayCommand]
    async Task StartAsync()
    {
        if (owner.Client is not { } client || _items is not { Count: > 0 } items)
            return;
        IsWorking = true;
        ShowProgress = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<TakeoutProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            Status = p.Stage == "albums"
                ? Tr.T("Putting photos in their albums…")
                : Tr.T("{0} of {1} · {2} new, {3} already in Immich{4}", Format.Count(p.Done), Format.Count(p.Total),
                    Format.Count(p.Uploaded), Format.Count(p.AlreadyThere), p.Failed > 0 ? " · " + Tr.T("{0} failed", Format.Count(p.Failed)) : "");
        });
        try
        {
            var limiter = new BandwidthLimiter(AppServices.Settings.Current.UploadLimitKBps * 1024L);
            var result = await Task.Run(() => GoogleTakeout.ImportAsync(client, items, limiter, progress, _cts.Token));
            Status = Tr.T("Done: {0} new, {1} already in Immich, {2} albums.", Format.Count(result.Uploaded), Format.Count(result.AlreadyThere), Format.Count(result.Albums))
                + (result.Failed > 0 ? " " + Tr.T("{0} couldn't be imported (see the log); run it again to retry them.", Format.Count(result.Failed)) : "");
            AppServices.RaiseAlbumsChanged();
        }
        catch (OperationCanceledException)
        {
            Status = Tr.T("Stopped. Run it again to carry on: what's already imported is skipped.");
        }
        catch (Exception ex)
        {
            Log.Warn("Takeout import failed", ex);
            Status = Tr.T("The import stopped. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsWorking = false;
            _cts = null;
        }
    }

    [RelayCommand]
    void Stop() => _cts?.Cancel();

    /// <summary>Google Takeout with only Google Photos picked.</summary>
    [RelayCommand]
    static void OpenTakeout() => Core.Platform.ShellLauncher.Open("https://takeout.google.com/settings/takeout/custom/photos");

    [RelayCommand]
    void Close()
    {
        if (!IsWorking)
            owner.ClosePanel(this);
    }
}
