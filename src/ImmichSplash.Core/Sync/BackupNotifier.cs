using ImmichSplash.Core.Settings;

namespace ImmichSplash.Core.Sync;

public enum NoticeKind
{
    /// <summary>A backup run finished.</summary>
    BackedUp,
    /// <summary>Files failed in the run that just ended.</summary>
    Failed,
    /// <summary>The server signed the app out.</summary>
    SignInNeeded,
    /// <summary>A camera card or USB drive with photos was plugged in.</summary>
    CameraCard,
}

/// <summary>Something worth a desktop notification. <see cref="Kind"/> says where a click should lead.</summary>
public sealed record Notice(NoticeKind Kind, string Title, string Body);

/// <summary>
/// Decides which backup events become desktop notifications, following the user's choices: finished
/// backups (never, only big ones, or every one that uploaded something), failures, and sign-in.
/// </summary>
public sealed class BackupNotifier
{
    /// <summary>A run counts as big from this many uploaded files, or from <see cref="LongRun"/>.</summary>
    public const int BigRunFiles = 20;
    public static readonly TimeSpan LongRun = TimeSpan.FromMinutes(2);

    SyncState _state = SyncState.NotConfigured;

    /// <summary>After each upload run, with exactly what it did.</summary>
    public static Notice? OnRunFinished(RunSummary run, AppSettings settings)
    {
        if (!settings.Notifications)
            return null;
        if (run.Failed > 0 && settings.NotifyFailures)
            return new Notice(NoticeKind.Failed, Loc.T("{0} couldn't be backed up", Format.Plural(run.Failed, "file")),
                Loc.T("They'll be tried again automatically. Open Activity to see why."));
        var wanted = settings.NotifyBackups switch
        {
            BackupNotice.Every => run.Uploaded > 0,
            BackupNotice.BigBackups => run.Uploaded >= BigRunFiles || (run.Uploaded > 0 && run.Duration >= LongRun),
            _ => false,
        };
        if (!wanted)
            return null;
        var body = Loc.T("Backed up {0} to Immich.", Format.Plural(run.Uploaded, "new photo or video", "new photos and videos"));
        // Which folders they came from, e.g. "VRChat: 42 · Pictures: 3" (when there's more than one, or it's named).
        if (run.ByFolder is { Count: > 0 } folders && folders.Any(f => f.Folder.Length > 0))
        {
            var parts = folders.Where(f => f.Folder.Length > 0).Take(3).Select(f => $"{f.Folder}: {Format.Count(f.Count)}").ToList();
            if (folders.Count > 3)
                parts.Add(Loc.T("{0} more", Format.Count(folders.Count - 3)));
            body += "\n" + string.Join(" · ", parts);
        }
        return new Notice(NoticeKind.BackedUp, Loc.T("Backup complete"), body);
    }

    /// <summary>Feed every status update in order: notices when the app gets signed out.</summary>
    public Notice? OnStatus(SyncStatus status, AppSettings settings)
    {
        var previous = _state;
        _state = status.State;
        return status.State == SyncState.AuthError && previous != SyncState.AuthError && settings is { Notifications: true, NotifySignIn: true }
            ? new Notice(NoticeKind.SignInNeeded, Loc.T("Sign in to Immich again"),
                Loc.T("Your sign-in has expired, so backup has stopped. Open Immich Splash to sign in."))
            : null;
    }
}
