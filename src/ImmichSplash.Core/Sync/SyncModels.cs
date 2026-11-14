namespace ImmichSplash.Core.Sync;

public enum FileStatus
{
    Pending = 0,
    Uploaded = 1,
    Duplicate = 2,
    Failed = 3,
    Unsupported = 4,
}

public enum SyncState
{
    NotConfigured,
    Idle,
    Scanning,
    Uploading,
    Paused,
    WaitingForPower,

    /// <summary>Outside the hours chosen for backing up.</summary>
    WaitingForSchedule,

    /// <summary>Data saving: paused on a metered connection.</summary>
    WaitingForNetwork,
    Offline,
    AuthError,
    Error,
}

public sealed record ScannedFile(string Path, string FolderId, long Size, long ModifiedTicks);

public sealed record FileRecord(string Path, string FolderId, long Size, long ModifiedTicks, string? Checksum, int Attempts);

public sealed record FailedFile(string Path, string? Error, DateTimeOffset? NextAttempt);

public readonly record struct StatusCounts(int Pending, int Uploaded, int Duplicate, int Failed, int Unsupported)
{
    public int Total => Pending + Uploaded + Duplicate + Failed + Unsupported;

    /// <summary>Files that are safely on the server (uploaded by us or already there).</summary>
    public int BackedUp => Uploaded + Duplicate;

    public static StatusCounts operator +(StatusCounts a, StatusCounts b) =>
        new(a.Pending + b.Pending, a.Uploaded + b.Uploaded, a.Duplicate + b.Duplicate, a.Failed + b.Failed, a.Unsupported + b.Unsupported);
}

public sealed record ActiveUpload(string Path, long Size, long Sent)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public double Fraction => Size <= 0 ? 0 : Math.Clamp(Sent / (double)Size, 0, 1);
}

/// <summary>Immutable snapshot of what the sync engine is doing, published to the UI and tray.</summary>
/// <summary>What one upload run did: files sent, files the server already had, files that failed.</summary>
/// <param name="ByFolder">How many were uploaded from each backed-up folder (by folder name), most first.</param>
public sealed record RunSummary(int Uploaded, int AlreadyThere, int Failed, TimeSpan Duration,
    IReadOnlyList<(string Folder, int Count)>? ByFolder = null);

public sealed record SyncStatus
{
    public SyncState State { get; init; }
    public string? Detail { get; init; }
    public StatusCounts Counts { get; init; }

    /// <summary>Files queued in the current upload run, and how many of those are finished.</summary>
    public int RunTotal { get; init; }
    public int RunDone { get; init; }

    public IReadOnlyList<ActiveUpload> Active { get; init; } = [];
    public double BytesPerSecond { get; init; }
    public DateTimeOffset? LastSync { get; init; }

    /// <summary>Estimated time until everything queued is uploaded; null until there's enough to go on.</summary>
    public TimeSpan? Remaining { get; init; }

    public bool IsBusy => State is SyncState.Scanning or SyncState.Uploading;
}
