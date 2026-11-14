using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

/// <summary>
/// Utilities → Review duplicates, like the Immich web app: for each group of look-alike photos, pick
/// what to keep (the biggest, best-quality one is suggested) and the rest go to the trash. Or say
/// they aren't duplicates.
/// </summary>
public sealed partial class DuplicatesViewModel : ViewModelBase, IDisposable
{
    const int ThumbnailWidth = 220;

    readonly PhotosViewModel _owner;
    readonly CancellationTokenSource _lifetime = new();

    public DuplicatesViewModel(PhotosViewModel owner)
    {
        _owner = owner;
        _ = LoadAsync();
    }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroups), nameof(ShowEmpty))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasGroups => !IsLoading && Groups.Count > 0;
    public bool ShowEmpty => !IsLoading && Error is null && Groups.Count == 0;
    public string Summary => Groups.Count == 0 ? "" : Format.Plural(Groups.Count, Tr.T("group of duplicates"), Tr.T("groups of duplicates"));

    async Task LoadAsync()
    {
        if (_owner.Client is not { } client)
            return;
        IsLoading = true;
        Error = null;
        try
        {
            var groups = await client.GetDuplicatesAsync(_lifetime.Token);
            foreach (var group in Groups)
                group.Dispose();
            Groups.Clear();
            foreach (var group in groups)
                Groups.Add(new DuplicateGroupViewModel(this, group));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ImmichApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Error = Tr.T("Your Immich server is too old to find duplicates. Update it to use this.");
        }
        catch (Exception ex)
        {
            Error = Tr.T("Couldn't load duplicates. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsLoading = false;
            Changed();
        }
        foreach (var group in Groups.ToList())
            await group.LoadThumbnailsAsync(_owner.Thumbnails, ThumbnailWidth, _lifetime.Token);
    }

    void Changed()
    {
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(Summary));
        _owner.OnDuplicatesChanged(Groups.Count);
    }

    internal async Task ResolveAsync(DuplicateGroupViewModel group, bool notDuplicates)
    {
        if (_owner.Client is not { } client || IsBusy)
            return;
        var keep = notDuplicates ? group.Items.ToList() : group.Items.Where(i => i.IsKept).ToList();
        var trash = notDuplicates ? [] : group.Items.Where(i => !i.IsKept).ToList();
        if (keep.Count == 0)
        {
            _owner.ShowNotice(Tr.T("Keep at least one photo."));
            return;
        }
        IsBusy = true;
        try
        {
            await client.ResolveDuplicatesAsync(keep.Select(i => i.Id).ToList(), trash.Select(i => i.Id).ToList(), CancellationToken.None);
            Remove(group);
            _owner.ShowNotice(notDuplicates ? Tr.T("Marked as not duplicates") : Tr.T("Moved {0} to the trash", Format.Plural(trash.Count, "photo")));
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't update the duplicates. {0}", NetworkErrors.Describe(ex)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Keeps the suggested photo of every group and trashes the rest, after asking.</summary>
    [RelayCommand]
    async Task KeepAllSuggestedAsync()
    {
        if (_owner.Client is not { } client || IsBusy || Groups.Count == 0)
            return;
        var trashCount = Groups.Sum(g => g.Items.Count - 1);
        if (!await _owner.ConfirmAsync(Tr.T("Keep the best of every group?"),
                Tr.T("The suggested photo of each group is kept and {0} go to the trash. You can restore them from the trash for a while.",
                    Format.Plural(trashCount, "photo")),
                Tr.T("Move to trash"), danger: true))
            return;
        IsBusy = true;
        var done = 0;
        try
        {
            foreach (var group in Groups.ToList())
            {
                var best = group.Suggested;
                await client.ResolveDuplicatesAsync([best.Id], group.Items.Where(i => i != best).Select(i => i.Id).ToList(), CancellationToken.None);
                done += group.Items.Count - 1;
                Remove(group);
            }
            _owner.ShowNotice(Tr.T("Moved {0} to the trash", Format.Plural(done, "photo")));
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't update the duplicates. {0}", NetworkErrors.Describe(ex)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    Task Refresh() => LoadAsync();

    void Remove(DuplicateGroupViewModel group)
    {
        Groups.Remove(group);
        group.Dispose();
        Changed();
    }

    internal void Open(DuplicateGroupViewModel group, DuplicateItemViewModel item)
    {
        var items = group.Items.ToList();
        _owner.OpenAssets(items.Select(i => i.Asset).ToList(), items.IndexOf(item));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach (var group in Groups)
            group.Dispose();
    }
}

public sealed partial class DuplicateGroupViewModel : ViewModelBase, IDisposable
{
    readonly DuplicatesViewModel _owner;

    public DuplicateGroupViewModel(DuplicatesViewModel owner, DuplicateGroup group)
    {
        _owner = owner;
        Items = group.Assets.Select(a => new DuplicateItemViewModel(this, a)).ToList();
        // Suggest the biggest file, then the most pixels: usually the original rather than a resized copy.
        Suggested = Items.OrderByDescending(i => i.Source.FileSize).ThenByDescending(i => (long)i.Source.Width * i.Source.Height).First();
        Suggested.IsSuggested = true;
        Suggested.IsKept = true;
    }

    public IReadOnlyList<DuplicateItemViewModel> Items { get; }
    public DuplicateItemViewModel Suggested { get; }
    public string Title => Format.Plural(Items.Count, Tr.T("photo"), Tr.T("photos"));

    internal void OnKeptChanged() => OnPropertyChanged(nameof(TrashText));

    public string TrashText => Items.Count(i => !i.IsKept) is var n and > 0
        ? Tr.T("Keep selected, trash {0}", n.ToString(System.Globalization.CultureInfo.CurrentCulture))
        : Tr.T("Keep all");

    internal async Task LoadThumbnailsAsync(ThumbnailLoader? loader, int width, CancellationToken ct)
    {
        if (loader is null)
            return;
        await Task.WhenAll(Items.Select(i => i.LoadThumbnailAsync(loader, width, ct)));
    }

    /// <summary>Keeps the photos marked to keep; the rest go to the trash.</summary>
    [RelayCommand]
    Task Resolve() => _owner.ResolveAsync(this, notDuplicates: Items.All(i => i.IsKept));

    [RelayCommand]
    Task NotDuplicates() => _owner.ResolveAsync(this, notDuplicates: true);

    internal void Open(DuplicateItemViewModel item) => _owner.Open(this, item);

    public void Dispose()
    {
        foreach (var item in Items)
            item.Dispose();
    }
}

public sealed partial class DuplicateItemViewModel(DuplicateGroupViewModel group, DuplicateAsset source) : ViewModelBase, IDisposable
{
    public DuplicateAsset Source { get; } = source;
    public TimelineAsset Asset => Source.Asset;
    public string Id => Source.Asset.Id;
    public string FileName => Source.FileName;

    public string Details => string.Join("  ·  ", new[]
    {
        Source.Width > 0 && Source.Height > 0 ? $"{Source.Width} × {Source.Height}" : null,
        Source.FileSize > 0 ? Format.Bytes(Source.FileSize) : null,
    }.Where(s => s is not null));

    public string DateText => DateTimeOffset.TryParse(Source.Asset.FileCreatedAt, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var d) ? d.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture) : "";

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsSuggested { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrashed))]
    public partial bool IsKept { get; set; }

    public bool IsTrashed => !IsKept;

    partial void OnIsKeptChanged(bool value) => group.OnKeptChanged();

    [RelayCommand]
    void ToggleKeep() => IsKept = !IsKept;

    [RelayCommand]
    void Open() => group.Open(this);

    internal async Task LoadThumbnailAsync(ThumbnailLoader loader, int width, CancellationToken ct)
    {
        try
        {
            var bitmap = await loader.LoadAsync(Id, AssetImageSize.Thumbnail, width, ct);
            if (ct.IsCancellationRequested)
                bitmap?.Dispose();
            else
                Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Thumbnail failed for {Id}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}
