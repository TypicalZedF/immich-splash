using System.ComponentModel;
using Avalonia.Controls;
using ImmichSplash.App.Controls;
using ImmichSplash.App.ViewModels;

namespace ImmichSplash.App.Views.Photos;

public partial class LockedPinSection : UserControl
{
    readonly PinEntry _entry;

    public LockedPinSection()
    {
        InitializeComponent();
        _entry = this.FindControl<PinEntry>("PinEntry")!;
        _entry.Submitted += (_, _) => (DataContext as PhotosViewModel)?.UnlockCommand.Execute(null);
        // Ready to type as soon as the prompt shows: when the page opens, when it's shown again, after a wrong PIN.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PhotosViewModel vm)
            {
                vm.PropertyChanged += OnViewModelChanged;
                _entry.FocusSoon();
            }
        };
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhotosViewModel.CanEnterPin) or nameof(PhotosViewModel.ShowLockedPrompt) or nameof(PhotosViewModel.IsUnlocking))
            _entry.FocusSoon();
    }
}
