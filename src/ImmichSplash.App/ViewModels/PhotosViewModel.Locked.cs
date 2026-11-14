using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- Locked folder (needs the account's PIN, like the web and mobile apps) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowLockedPrompt), nameof(ShowLockAction))]
    public partial bool IsUnlocked { get; set; }

    [ObservableProperty]
    public partial string Pin { get; set; } = "";

    [ObservableProperty]
    public partial string? LockedMessage { get; set; }

    // ---- Remembering the PIN on this PC (confirmed with Windows Hello) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnlockWithHello), nameof(ShowRememberPin))]
    public partial bool HasRememberedPin { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RememberPinText), nameof(HelloButtonText), nameof(CanUnlockWithHello), nameof(ShowRememberPin))]
    public partial bool HelloAvailable { get; set; }

    [ObservableProperty]
    public partial bool RememberPin { get; set; }

    public bool CanUnlockWithHello => HasRememberedPin && HelloAvailable && CanEnterPin && !IsCreatingPin;
    public bool ShowRememberPin => !HasRememberedPin && HelloAvailable && CanEnterPin && !IsCreatingPin;
    public string RememberPinText => RememberedPin.CheckBoxText(HelloAvailable);
    public string HelloButtonText => HelloAvailable ? WindowsHello.UnlockButtonText : Tr.T("Unlock with the remembered PIN");

    async Task RefreshRememberedAsync()
    {
        HasRememberedPin = RememberedPin.Exists;
        HelloAvailable = await WindowsHello.IsAvailableAsync();
    }

    [RelayCommand]
    async Task UnlockWithHelloAsync()
    {
        if (Client is not { } client || IsUnlocking)
            return;
        IsUnlocking = true;
        LockedMessage = null;
        try
        {
            if (await RememberedPin.TryUnlockAsync(client, _main))
            {
                IsUnlocked = true;
                await LoadGridAsync();
            }
            else
                HasRememberedPin = RememberedPin.Exists;
        }
        catch (Exception ex)
        {
            LockedMessage = Tr.T("Couldn't unlock. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsUnlocking = false;
        }
    }

    const int PinLength = 6;

    partial void OnPinChanged(string value)
    {
        // Digits only; the box row is the display and the six-digit PIN unlocks as soon as it's complete.
        var digits = new string(value.Where(char.IsAsciiDigit).Take(PinLength).ToArray());
        if (digits != value)
        {
            Pin = digits;
            return;
        }
        if (digits.Length == PinLength && CanEnterPin && !IsUnlocking)
            _ = IsCreatingPin ? CreatePinStepAsync() : UnlockAsync();
    }

    // ---- No PIN yet: make one right here (typed twice), then the folder opens ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinPrompt))]
    public partial bool IsCreatingPin { get; set; }

    string? _firstPin;

    public string PinPrompt => !IsCreatingPin ? Tr.T("Enter your PIN code to access the locked folder")
        : _firstPin is null ? Tr.T("Create a 6-digit PIN for the locked folder")
        : Tr.T("Type the same PIN again to confirm");

    async Task CreatePinStepAsync()
    {
        if (_firstPin is null)
        {
            _firstPin = Pin;
            Pin = "";
            LockedMessage = null;
            OnPropertyChanged(nameof(PinPrompt));
            return;
        }
        if (Pin != _firstPin)
        {
            _firstPin = null;
            Pin = "";
            LockedMessage = Tr.T("Those didn't match. Start again.");
            OnPropertyChanged(nameof(PinPrompt));
            return;
        }
        if (Client is not { } client)
            return;
        IsUnlocking = true;
        try
        {
            var pin = Pin;
            await client.SetupPinAsync(pin, CancellationToken.None);
            await client.UnlockSessionAsync(pin, CancellationToken.None);
            _firstPin = null;
            Pin = "";
            IsCreatingPin = false;
            ShowNotice(Tr.T("PIN created. You can change it in Settings."));
            IsUnlocked = true;
            await LoadGridAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create the PIN", ex);
            _firstPin = null;
            Pin = "";
            LockedMessage = Tr.T("Couldn't create the PIN. {0}", NetworkErrors.Describe(ex));
            OnPropertyChanged(nameof(PinPrompt));
        }
        finally
        {
            IsUnlocking = false;
        }
    }

    partial void OnCanEnterPinChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUnlockWithHello));
        OnPropertyChanged(nameof(ShowRememberPin));
    }

    partial void OnIsCreatingPinChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUnlockWithHello));
        OnPropertyChanged(nameof(ShowRememberPin));
    }

    /// <summary>Set by the shell: leaves the locked folder (the PIN prompt's Cancel).</summary>
    internal Action? LeaveRequested { get; set; }

    [RelayCommand]
    void CancelPin()
    {
        _firstPin = null;
        OnPropertyChanged(nameof(PinPrompt));
        Pin = "";
        LockedMessage = null;
        LeaveRequested?.Invoke();
    }

    [ObservableProperty]
    public partial bool CanEnterPin { get; set; }

    [ObservableProperty]
    public partial bool IsUnlocking { get; set; }

    async Task CheckLockedAsync()
    {
        if (Client is not { } client)
            return;
        IsLoading = false;
        LockedMessage = null;
        CanEnterPin = false;
        try
        {
            var status = await client.GetAuthStatusAsync(_lifetime.Token);
            IsCreatingPin = !status.HasPin;
            if (status.IsElevated)
            {
                IsUnlocked = true;
                await LoadGridAsync();
                return;
            }
            CanEnterPin = true;
            await RefreshRememberedAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            LockedMessage = Tr.T("The locked folder needs signing in with your password or SSO (an API key can't open it).");
        }
        catch (Exception ex)
        {
            LockedMessage = Tr.T("Couldn't check the locked folder. {0}", NetworkErrors.Describe(ex));
        }
    }

    [RelayCommand]
    async Task UnlockAsync()
    {
        if (Client is not { } client || Pin.Length == 0 || IsUnlocking)
            return;
        IsUnlocking = true;
        LockedMessage = null;
        try
        {
            await client.UnlockSessionAsync(Pin, CancellationToken.None);
            if (RememberPin)
            {
                RememberedPin.Save(Pin);
                HasRememberedPin = true;
                RememberPin = false;
            }
            Pin = "";
            IsUnlocked = true;
            await LoadGridAsync();
        }
        catch (ImmichApiException ex) when ((int)ex.StatusCode is >= 400 and < 500)
        {
            LockedMessage = Tr.T("That PIN isn't right.");
            Pin = "";
        }
        catch (Exception ex)
        {
            LockedMessage = Tr.T("Couldn't unlock. {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsUnlocking = false;
        }
    }

    /// <summary>Locks the folder again (also when leaving the page).</summary>
    [RelayCommand]
    public async Task LockAsync()
    {
        if (!IsUnlocked)
            return;
        IsUnlocked = false;
        Drill = null;
        ClearSelection();
        ReleaseRows();
        Viewer = null;
        CanEnterPin = true;
        try
        {
            if (Client is { } client)
                await client.LockSessionAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Info($"Locking the session failed: {ex.Message}");
        }
    }

    /// <summary>Asks for the PIN (in a panel) and then runs <paramref name="then"/>; used to move photos into the locked folder.</summary>
    async Task<bool> EnsureUnlockedAsync()
    {
        if (Client is not { } client)
            return false;
        try
        {
            var status = await client.GetAuthStatusAsync(CancellationToken.None);
            if (status.IsElevated)
                return true;
            if (!status.HasPin)
            {
                ShowNotice(Tr.T("Open the Locked folder first to create a PIN, then try again."));
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't check the locked folder. {0}", NetworkErrors.Describe(ex)));
            return false;
        }
        try
        {
            if (await RememberedPin.TryUnlockAsync(client, _main))
                return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Unlocking with the remembered PIN failed: {ex.Message}");
        }
        var hello = await WindowsHello.IsAvailableAsync();
        var panel = new PinPanelViewModel(this, RememberedPin.CheckBoxText(hello)) { CanRemember = hello };
        Panel = panel;
        return await panel.Result;
    }
}

/// <summary>One of the PIN prompt's boxes.</summary>
public sealed record PinBox(bool Filled, bool Active);
