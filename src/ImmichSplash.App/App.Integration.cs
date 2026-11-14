using Avalonia.Threading;
using ImmichSplash.App.Services;
using ImmichSplash.App.ViewModels;
using ImmichSplash.Core;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.App;

/// <summary>
/// The app's hooks into the desktop: "Upload to Immich" in the file manager, the taskbar jump list, and noticing
/// camera cards as they're plugged in.
/// </summary>
public partial class App
{
    /// <summary>Jump list tasks: run the app with <c>--action name</c>.</summary>
    public const string ActionArgument = "--action";

    CameraCardWatcher? _cards;
    readonly List<string> _pendingUploads = [];
    DispatcherTimer? _uploadTimer;
    int _uploadTries;
    CameraCard? _pendingCard;
    readonly HashSet<string> _offeredCards = [];

    void StartIntegration()
    {
        var settings = AppServices.Settings.Current;
        ApplyExplorerIntegration(settings.ExplorerIntegration);
        WindowsShell.SetJumpListTasks(
        [
            new(Tr.T("Sync now"), $"{ActionArgument} sync"),
            new(Tr.T("Pause for 1 hour"), $"{ActionArgument} pause"),
            new(Tr.T("Open your library"), $"{ActionArgument} library"),
            new(Tr.T("Backup status"), $"{ActionArgument} backup"),
        ]);
        _cards = new CameraCardWatcher();
        _cards.CardInserted += card => Dispatcher.UIThread.Post(() => OnCardInserted(card));

        if (Program.StartupUploads.Count > 0)
            QueueUploads(Program.StartupUploads);
        if (Program.StartupAction is { } action)
            RunAction(action);
    }

    internal static void ApplyExplorerIntegration(bool on)
    {
        if (on)
            ExplorerIntegration.Register(Tr.T("Upload to Immich"),
                path => WindowsShell.CreateShortcut(path, ExplorerIntegration.UploadArgument, Tr.T("Upload to Immich")));
        else
            ExplorerIntegration.Unregister();
    }

    void RunAction(string action)
    {
        switch (action)
        {
            case "sync":
                AppServices.Engine.SyncNow();
                break;
            case "pause":
                AppServices.Engine.Pause(TimeSpan.FromHours(1));
                break;
            case "library":
                ShowMainWindow(ShellPage.Photos);
                break;
            case "backup":
                ShowMainWindow(ShellPage.Overview);
                break;
        }
    }

    // ---- Files handed over by the file manager (one copy of the app per file, so gather them up) ----

    void QueueUploads(IEnumerable<string> paths)
    {
        _pendingUploads.AddRange(paths);
        _uploadTries = 0;
        _uploadTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => OfferPendingUploads());
        _uploadTimer.Stop();
        _uploadTimer.Start();
    }

    void OfferPendingUploads()
    {
        _uploadTimer!.Stop();
        if (_pendingUploads.Count == 0)
            return;
        ShowMainWindow();
        // Signed in, unlocked and nothing else open: offer them. Otherwise wait (e.g. for Windows Hello) and try again.
        if (_window?.DataContext is MainWindowViewModel { CanAcceptDrop: true } main)
        {
            var paths = _pendingUploads.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pendingUploads.Clear();
            main.OfferUpload(paths);
            return;
        }
        if (++_uploadTries < 300)
        {
            _uploadTimer.Interval = TimeSpan.FromSeconds(1);
            _uploadTimer.Start();
        }
        else
        {
            Log.Info("Gave up offering files from the file manager: the app wasn't ready");
            _pendingUploads.Clear();
        }
        _uploadTimer.Interval = TimeSpan.FromMilliseconds(400);
    }

    // ---- Camera cards ----

    void OnCardInserted(CameraCard card)
    {
        var settings = AppServices.Settings.Current;
        if (!settings.AskAboutCameraCards || !AppServices.Account.IsSignedIn || settings.IgnoredCameraCards.Contains(card.Key)
            || !_offeredCards.Add(card.Key))
            return;
        Log.Info("A camera card was plugged in");
        _pendingCard = card;
        if (_window is { IsVisible: true })
            _ = OfferCardAsync();
        else if (settings.Notifications)
            DesktopNotifications.Show(new Notice(NoticeKind.CameraCard, Tr.T("Photos found on {0}", card.Label),
                Tr.T("Click to back them up to Immich.")));
    }

    /// <summary>Asks whether to back up the card that was just put in (opens the upload panel with its photos).</summary>
    internal async Task OfferCardAsync()
    {
        if (_pendingCard is not { } card || _window?.DataContext is not MainWindowViewModel { CanAcceptDrop: true } main)
            return;
        _pendingCard = null;
        if (!Directory.Exists(card.PhotosFolder))
            return;
        var choice = await main.ChooseAsync(Tr.T("Back up photos from {0}?", card.Label),
            Tr.T("Immich Splash found photos and videos on this card. Anything already in Immich is skipped."),
            Tr.T("Back up"), Tr.T("Not this card"));
        switch (choice)
        {
            case true:
                main.OfferUpload([card.PhotosFolder]);
                break;
            case false:
                AppServices.Settings.Update(s =>
                {
                    if (!s.IgnoredCameraCards.Contains(card.Key))
                        s.IgnoredCameraCards.Add(card.Key);
                });
                break;
        }
    }

    void StopIntegration() => _cards?.Dispose();
}
