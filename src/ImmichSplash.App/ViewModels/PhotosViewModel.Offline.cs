using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- Keeping the open album on this PC (for viewing without a connection) ----

    OfflineAlbum? OpenOfflineAlbum => Drill?.Album is { } album
        ? AppServices.Settings.Current.OfflineAlbums.FirstOrDefault(a => a.AlbumId == album.Id) : null;

    public bool IsAlbumOffline => OpenOfflineAlbum is not null;

    public string OfflineTip => IsAlbumOffline
        ? Tr.T("Kept on this PC. Click for options.")
        : Tr.T("Keep on this PC (to view without a connection)");

    [RelayCommand]
    async Task ToggleAlbumOfflineAsync()
    {
        if (Drill?.Album is not { } album)
            return;
        if (OpenOfflineAlbum is { } kept)
        {
            var choice = _main is null ? null : await _main.ChooseAsync(Tr.T("“{0}” is kept on this PC", album.AlbumName),
                Tr.T("Its photos are in {0} and stay up to date with the album.", kept.Folder),
                Tr.T("Open folder"), Tr.T("Stop keeping it"));
            if (choice == true)
                ShellLauncher.Open(kept.Folder);
            else if (choice == false)
            {
                AppServices.Settings.Update(s => s.OfflineAlbums.RemoveAll(a => a.AlbumId == album.Id));
                OfflineAlbums.Forget(kept, deleteFiles: true);
                ShowNotice(Tr.T("“{0}” is no longer kept on this PC", album.AlbumName));
            }
        }
        else
        {
            var folder = OfflineAlbums.FolderFor(album.AlbumName);
            AppServices.Settings.Update(s => s.OfflineAlbums.Add(new OfflineAlbum { AlbumId = album.Id, AlbumName = album.AlbumName, Folder = folder }));
            AppServices.Offline.SyncSoon();
            ShowNotice(Tr.T("Saving “{0}” to {1}. New photos in it will be saved too.", album.AlbumName, folder));
        }
        OnPropertyChanged(nameof(IsAlbumOffline));
        OnPropertyChanged(nameof(OfflineTip));
    }
}
