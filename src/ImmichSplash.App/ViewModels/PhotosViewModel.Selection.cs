using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- Selection ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelecting), nameof(SelectionText))]
    public partial int SelectionCount { get; set; }

    public bool IsSelecting => SelectionCount > 0;
    public string SelectionText => Tr.T("{0} selected", SelectionCount);

    internal bool IsAssetSelected(string id) => _selected.Contains(id);

    internal void ToggleSelection(AssetCellViewModel cell)
    {
        if (IsPicker || cell.Asset is not { } asset)
            return;
        if (!_selected.Remove(asset.Id))
            _selected.Add(asset.Id);
        cell.IsSelected = _selected.Contains(asset.Id);
        SelectionCount = _selected.Count;
    }

    [RelayCommand]
    void ClearSelection()
    {
        _selected.Clear();
        SelectionCount = 0;
        foreach (var cell in VisibleCells)
            cell.IsSelected = false;
    }

    IReadOnlyList<string> SelectedIds => _selected.ToList();

    /// <summary>What "today" is for grouping recent days (replaceable for testing).</summary>
    public static Func<DateTime> Today { get; set; } = () => DateTime.Now;

    /// <summary>Reading the months not loaded yet, for "Select all".</summary>
    [ObservableProperty]
    public partial bool IsSelectingAll { get; set; }

    /// <summary>Selects every photo and video shown here (reading months not loaded yet); again to clear.</summary>
    [RelayCommand]
    async Task SelectAllAsync()
    {
        var buckets = _buckets;
        if (IsPicker || IsSelectingAll || buckets.Count == 0)
            return;
        IsSelectingAll = true;
        try
        {
            var ids = new List<string>();
            // A few months at a time: a big library is hundreds of them.
            foreach (var chunk in buckets.Chunk(6))
            {
                foreach (var assets in await Task.WhenAll(chunk.Select(b => b.GetAssetsAsync(_lifetime.Token))))
                    ids.AddRange(assets.Where(a => !a.IsLocalOnly).Select(a => a.Id)); // not backed up yet: can't be selected
                if (!ReferenceEquals(buckets, _buckets))
                    return; // the page changed meanwhile
            }
            if (_selected.IsSupersetOf(ids))
            {
                ClearSelection(); // everything was already selected: the button clears instead
                return;
            }
            _selected.UnionWith(ids);
            SelectionCount = _selected.Count;
            foreach (var cell in VisibleCells)
                cell.IsSelected = cell.Asset is { } asset && _selected.Contains(asset.Id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't select everything. {0}", NetworkErrors.Describe(ex)));
        }
        finally
        {
            IsSelectingAll = false;
        }
    }

    [RelayCommand]
    void ShareSelection() => ShowShare(SelectedIds);

    [RelayCommand]
    void AddSelectionToAlbum() => ShowAlbumPicker(SelectedIds);

    [RelayCommand]
    async Task FavoriteSelectionAsync()
    {
        if (await SetFavoriteAsync(SelectedIds, true))
            ClearSelection();
    }

    [RelayCommand]
    Task DownloadSelectionAsync() => DownloadAsync(SelectedIds);

    [RelayCommand]
    async Task TrashSelectionAsync()
    {
        if (await TrashAsync(SelectedIds))
            ClearSelection();
    }

    // What the selection bar offers depends on where you are.
    bool InTrash => Page == LibraryPage.Trash && Drill is null;
    bool InArchive => Page == LibraryPage.Archive && Drill is null;
    bool InLocked => Page == LibraryPage.Locked;

    /// <summary>Normal actions (share, album, favorite, download, archive, trash): everywhere but the trash and the locked folder.</summary>
    public bool CanUseNormalActions => !InTrash && !InLocked;
    public bool CanArchive => CanUseNormalActions && !InArchive;
    public bool CanUnarchive => InArchive;
    public bool CanMoveToLocked => CanUseNormalActions;
    public bool CanMoveOutOfLocked => InLocked;
    public bool CanRestore => InTrash;

    partial void OnDrillChanged(GridQuery? value)
    {
        OnPropertyChanged(nameof(IsAlbumOffline));
        OnPropertyChanged(nameof(OfflineTip));
        if (value?.Album?.Id != _albumToRemember)
            CommitRecentAlbum();
        foreach (var name in new[] { nameof(CanUseNormalActions), nameof(CanArchive), nameof(CanUnarchive), nameof(CanMoveToLocked), nameof(CanRestore) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    Task ArchiveSelectionAsync() => MoveSelectionAsync("archive", Tr.T("Archived {0}"));

    [RelayCommand]
    Task UnarchiveSelectionAsync() => MoveSelectionAsync("timeline", Tr.T("Moved {0} back to your timeline"));

    [RelayCommand]
    async Task MoveSelectionToLockedAsync()
    {
        if (await EnsureUnlockedAsync())
            await MoveSelectionAsync("locked", Tr.T("Moved {0} to the locked folder"));
    }

    [RelayCommand]
    Task MoveSelectionOutOfLockedAsync() => MoveSelectionAsync("timeline", Tr.T("Moved {0} back to your timeline"));

    async Task MoveSelectionAsync(string visibility, string doneFormat)
    {
        if (Client is not { } client || _selected.Count == 0)
            return;
        var ids = SelectedIds;
        try
        {
            await client.SetVisibilityAsync(ids, visibility, CancellationToken.None);
            ClearSelection();
            ShowNotice(string.Format(System.Globalization.CultureInfo.CurrentCulture, doneFormat, Format.Plural(ids.Count, "photo")));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't move the photos. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    async Task RestoreSelectionAsync()
    {
        if (Client is not { } client || _selected.Count == 0)
            return;
        var ids = SelectedIds;
        try
        {
            await client.RestoreFromTrashAsync(ids, CancellationToken.None);
            ClearSelection();
            ShowNotice(Tr.T("Restored {0}", Format.Plural(ids.Count, "photo")));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't restore. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    async Task DeleteSelectionForeverAsync()
    {
        if (Client is not { } client || _main is null || _selected.Count == 0)
            return;
        var ids = SelectedIds;
        var what = Format.Plural(ids.Count, "photo");
        if (!await _main.ConfirmAsync(Tr.T("Delete {0} forever?", what), Tr.T("They're removed from Immich for good. This can't be undone."),
                Tr.T("Delete forever"), danger: true))
            return;
        try
        {
            await client.DeletePermanentlyAsync(ids, CancellationToken.None);
            ClearSelection();
            ShowNotice(Tr.T("Deleted {0}", what));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't delete. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    async Task RestoreAllAsync()
    {
        if (Client is not { } client)
            return;
        try
        {
            await client.RestoreAllFromTrashAsync(CancellationToken.None);
            ShowNotice(Tr.T("Restored everything in the trash"));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't restore. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    async Task EmptyTrashAsync()
    {
        if (Client is not { } client || _main is null)
            return;
        if (!await _main.ConfirmAsync(Tr.T("Empty the trash?"), Tr.T("Everything in the trash is removed from Immich for good. This can't be undone."),
                Tr.T("Empty trash"), danger: true))
            return;
        try
        {
            await client.EmptyTrashAsync(CancellationToken.None);
            ShowNotice(Tr.T("The trash is empty."));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't empty the trash. {0}", NetworkErrors.Describe(ex)));
        }
    }
}
