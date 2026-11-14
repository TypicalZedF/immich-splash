using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;

namespace ImmichSplash.App.ViewModels;

/// <summary>
/// The locked folder's PIN remembered on this PC (only when you ask), confirmed with Windows Hello (or your Linux
/// login) before it's used, so you don't type it every time you open the locked folder or move photos into it.
/// </summary>
internal static class RememberedPin
{
    static string? UserId => AppServices.Settings.Current.UserId;

    public static bool Exists => LockedPinStore.Load(UserId) is not null;

    public static string CheckBoxText(bool helloAvailable) => Tr.T("Remember on this PC and use {0} next time", WindowsHello.Name);

    public static void Save(string pin)
    {
        try
        {
            LockedPinStore.Save(UserId, pin);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remember the PIN", ex);
        }
    }

    public static void Forget() => LockedPinStore.Delete();

    /// <summary>
    /// Unlocks the session with the remembered PIN after Windows Hello (when it's set up on this PC).
    /// False when there's none, you cancelled, or the PIN no longer works (it's forgotten then).
    /// </summary>
    public static async Task<bool> TryUnlockAsync(ImmichClient client, MainWindowViewModel? main)
    {
        if (LockedPinStore.Load(UserId) is not { } pin)
            return false;
        // Like the Immich mobile app's biometric unlock: the PIN is only ever used after Windows Hello (or the Linux
        // login) says it's you. Without it, the remembered PIN isn't used at all.
        if (!await WindowsHello.IsAvailableAsync())
            return false;
        var result = await WindowsHello.VerifyAsync(main?.GetWindowHandle?.Invoke() ?? IntPtr.Zero, Tr.T("Open the locked folder"));
        if (result != HelloResult.Verified)
            return false;
        try
        {
            await client.UnlockSessionAsync(pin, CancellationToken.None);
            return true;
        }
        catch (ImmichApiException ex) when ((int)ex.StatusCode is >= 400 and < 500)
        {
            Log.Info("The remembered PIN no longer works; forgetting it");
            Forget();
            return false;
        }
    }
}
