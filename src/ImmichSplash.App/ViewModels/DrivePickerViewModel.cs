using CommunityToolkit.Mvvm.Input;
using ImmichSplash.Core.Platform;

namespace ImmichSplash.App.ViewModels;

/// <summary>Several USB drives or cards are plugged in: which one to back up?</summary>
public sealed partial class DrivePickerViewModel(PhotosViewModel owner, IReadOnlyList<CameraCard> drives) : ViewModelBase
{
    public IReadOnlyList<DriveChoice> Drives { get; } = drives.Select(d => new DriveChoice(d,
        d.PhotosFolder == d.Root ? d.Root : $"{d.Root} · DCIM")).ToList();

    [RelayCommand]
    void Choose(DriveChoice choice) => owner.BackUpDrive(choice.Drive);

    [RelayCommand]
    void Close() => owner.ClosePanel(this);
}

public sealed record DriveChoice(CameraCard Drive, string Detail)
{
    public string Label => Drive.Label;
}
