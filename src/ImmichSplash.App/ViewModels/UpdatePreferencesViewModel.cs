using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core.Settings;

namespace ImmichSplash.App.ViewModels;

/// <summary>Asked once, right after setup: how the app should update itself (also in Settings).</summary>
public sealed partial class UpdatePreferencesViewModel : ViewModelBase
{
    readonly MainWindowViewModel _main;

    public UpdatePreferencesViewModel(MainWindowViewModel main)
    {
        _main = main;
        var s = AppServices.Settings.Current;
        // A portable copy or Linux can't install by itself, so "automatic" there means "tell me".
        if (!s.CheckForUpdates)
            ModeOff = true;
        else if (s.AutoInstallUpdates && CanSelfUpdate)
            ModeAutomatic = true;
        else
            ModeAsk = true;
        ChannelBeta = s.UpdateChannel == UpdateChannel.Beta;
        ChannelStable = !ChannelBeta;
    }

    public static bool CanSelfUpdate => UpdateService.IsInstalledCopy;

    public string AskText => CanSelfUpdate
        ? Tr.T("Download updates, then ask me before installing")
        : Tr.T("Tell me when there's a new version");

    [ObservableProperty]
    public partial bool ModeAutomatic { get; set; }

    [ObservableProperty]
    public partial bool ModeAsk { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChecksForUpdates))]
    public partial bool ModeOff { get; set; }

    /// <summary>The channel only matters when the app looks for updates.</summary>
    public bool ChecksForUpdates => !ModeOff;

    [ObservableProperty]
    public partial bool ChannelStable { get; set; }

    [ObservableProperty]
    public partial bool ChannelBeta { get; set; }

    [RelayCommand]
    void Save()
    {
        var channel = ChannelBeta ? UpdateChannel.Beta : UpdateChannel.Stable;
        var channelChanged = AppServices.Settings.Current.UpdateChannel != channel;
        AppServices.Settings.Update(s =>
        {
            s.CheckForUpdates = !ModeOff;
            s.AutoInstallUpdates = ModeAutomatic;
            s.UpdateChannel = channel;
            s.UpdatePreferencesPending = false;
        });
        if (channelChanged)
            _ = AppServices.Updates.RecheckAsync();
        _main.CloseOverlay(this);
    }
}
