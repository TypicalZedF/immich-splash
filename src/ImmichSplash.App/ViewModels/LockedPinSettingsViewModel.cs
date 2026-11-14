using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

/// <summary>
/// Settings → Security: the locked folder's PIN (it belongs to the Immich account, so it works in the web and
/// mobile apps too). Create one, change it, or remove it; changing and removing take the current PIN.
/// </summary>
public sealed partial class LockedPinSettingsViewModel : ViewModelBase
{
    public enum Mode { None, Create, Change, Remove }

    public LockedPinSettingsViewModel() => _ = RefreshAsync();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanCreate), nameof(CanChange))]
    public partial bool? HasPin { get; set; }

    public string StatusText => HasPin switch
    {
        null => Tr.T("Checking…"),
        true => Tr.T("A PIN is set. You need it to open the locked folder."),
        false => Tr.T("No PIN yet. Make one to use the locked folder."),
    };

    public bool CanCreate => HasPin == false && Editing == Mode.None;
    public bool CanChange => HasPin == true && Editing == Mode.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(EditTitle), nameof(NeedsCurrent), nameof(NeedsNew), nameof(IsRemoving), nameof(ConfirmText),
        nameof(CanCreate), nameof(CanChange))]
    public partial Mode Editing { get; set; }

    public bool IsEditing => Editing != Mode.None;
    public bool NeedsCurrent => Editing is Mode.Change or Mode.Remove;
    public bool NeedsNew => Editing is Mode.Create or Mode.Change;
    public bool IsRemoving => Editing == Mode.Remove;

    public string EditTitle => Editing switch
    {
        Mode.Create => Tr.T("Create a PIN"),
        Mode.Change => Tr.T("Change the PIN"),
        Mode.Remove => Tr.T("Remove the PIN"),
        _ => "",
    };

    public string ConfirmText => Editing switch
    {
        Mode.Create => Tr.T("Create PIN"),
        Mode.Change => Tr.T("Change PIN"),
        Mode.Remove => Tr.T("Remove PIN"),
        _ => "",
    };

    [ObservableProperty]
    public partial string CurrentPin { get; set; } = "";

    [ObservableProperty]
    public partial string NewPin { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPin { get; set; } = "";

    partial void OnCurrentPinChanged(string value) => CurrentPin = Digits(value);
    partial void OnNewPinChanged(string value) => NewPin = Digits(value);
    partial void OnConfirmPinChanged(string value) => ConfirmPin = Digits(value);

    static string Digits(string value) => new(value.Where(char.IsAsciiDigit).Take(6).ToArray());

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    async Task RefreshAsync()
    {
        if (AppServices.Account.Client is not { } client)
            return;
        try
        {
            HasPin = (await client.GetAuthStatusAsync(CancellationToken.None)).HasPin;
        }
        catch (Exception ex)
        {
            Log.Info($"PIN status unavailable: {ex.Message}");
            Message = Tr.T("Couldn't check the PIN. {0}", NetworkErrors.Describe(ex));
        }
    }

    /// <summary>The PIN is remembered on this PC (ticked when entering it).</summary>
    [ObservableProperty]
    public partial bool IsRemembered { get; set; } = RememberedPin.Exists;

    [RelayCommand]
    void ForgetRemembered()
    {
        RememberedPin.Forget();
        IsRemembered = false;
        Message = Tr.T("This PC no longer remembers your PIN.");
    }

    [RelayCommand]
    void StartCreate() => Start(Mode.Create);

    [RelayCommand]
    void StartChange() => Start(Mode.Change);

    [RelayCommand]
    void StartRemove() => Start(Mode.Remove);

    void Start(Mode mode)
    {
        CurrentPin = NewPin = ConfirmPin = "";
        Error = Message = null;
        Editing = mode;
    }

    [RelayCommand]
    void Cancel()
    {
        CurrentPin = NewPin = ConfirmPin = "";
        Error = null;
        Editing = Mode.None;
    }

    [RelayCommand]
    async Task SaveAsync()
    {
        if (AppServices.Account.Client is not { } client || IsSaving || Editing == Mode.None)
            return;
        if (NeedsCurrent && CurrentPin.Length != 6)
        {
            Error = Tr.T("Type your current PIN.");
            return;
        }
        if (NeedsNew && NewPin.Length != 6)
        {
            Error = Tr.T("The new PIN needs 6 digits.");
            return;
        }
        if (NeedsNew && NewPin != ConfirmPin)
        {
            Error = Tr.T("The two new PINs don't match.");
            return;
        }
        IsSaving = true;
        Error = null;
        try
        {
            switch (Editing)
            {
                case Mode.Create:
                    await client.SetupPinAsync(NewPin, CancellationToken.None);
                    Message = Tr.T("PIN created.");
                    HasPin = true;
                    break;
                case Mode.Change:
                    await client.ChangePinAsync(CurrentPin, NewPin, CancellationToken.None);
                    if (RememberedPin.Exists)
                        RememberedPin.Save(NewPin);
                    Message = Tr.T("PIN changed.");
                    break;
                case Mode.Remove:
                    await client.RemovePinAsync(CurrentPin, CancellationToken.None);
                    RememberedPin.Forget();
                    IsRemembered = false;
                    Message = Tr.T("PIN removed.");
                    HasPin = false;
                    break;
            }
            Cancel();
        }
        catch (ImmichApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest && NeedsCurrent)
        {
            Error = Tr.T("That PIN isn't right.");
            CurrentPin = "";
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the PIN", ex);
            Error = Tr.T("Couldn't save the PIN. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsSaving = false;
        }
    }
}
