using System.Diagnostics;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Settings;

namespace ImmichSplash.Core.Sync;

public sealed partial class SyncEngine
{
    // ---- Uploading --------------------------------------------------------------------------------

    async Task UploadAsync(ImmichClient client, AppSettings settings, CancellationToken ct)
    {
        var folderIds = EnabledFolderIds(settings);
        _runTotal = _db.CountCandidates(folderIds);
        _runDone = 0;
        _runFailed = 0;
        if (_runTotal == 0)
            return;
        StartEstimate(folderIds);
        var started = Stopwatch.StartNew();

        var uploaded = 0;

        var byFolder = new Dictionary<string, int>(StringComparer.Ordinal);
        var alreadyThere = 0;
        SetState(SyncState.Uploading, Loc.T("Checking files…"));
        _progressTimer.Change(400, 400);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (settings.PauseOnBattery && IsOnBattery())
                    throw new WaitForPowerException();
                if (PausedForData(settings))
                    throw new WaitForNetworkException();
                if (OutsideWindow(settings))
                    throw new WaitForScheduleException(); // the window closed: stop between batches

                folderIds = _rules.Keys.ToList(); // folders turned off mid-run stop after this batch
                RecountRemaining(folderIds);
                var batch = _db.GetCandidates(folderIds, BatchSize);
                if (batch.Count == 0)
                    break;

                // Data saving: on a metered connection videos wait for a better one.
                if (PhotosOnlyForData(settings))
                {
                    var videos = batch.Where(r => _media.Classify(r.Path) == MediaKind.Video).ToList();
                    if (videos.Count > 0)
                    {
                        foreach (var video in videos)
                            _db.Defer(video.Path, DateTime.UtcNow.AddMinutes(15));
                        if (!_deferredVideosNoted)
                        {
                            _deferredVideosNoted = true;
                            _activity.Add(ActivityKind.Info, Loc.T("On a metered connection: videos will upload when you're on an unmetered one"));
                        }
                        batch = batch.Except(videos).ToList();
                        if (batch.Count == 0)
                            continue;
                    }
                }
                else
                {
                    _deferredVideosNoted = false;
                }

                var ready = await PrepareBatchAsync(batch, ct);
                if (ready.Count == 0)
                    continue;

                var results = await client.BulkUploadCheckAsync(ready.Select(r => new ChecksumItem(r.Path, r.Checksum!)).ToList(), ct);
                var byPath = results.ToDictionary(r => r.Id, StringComparer.Ordinal);
                var toUpload = new List<FileRecord>(ready.Count);

                foreach (var record in ready)
                {
                    if (!byPath.TryGetValue(record.Path, out var result))
                    {
                        _db.Defer(record.Path, DateTime.UtcNow.AddMinutes(5));
                        continue;
                    }

                    if (result.IsAccepted)
                    {
                        toUpload.Add(record);
                    }
                    else if (result.IsDuplicate)
                    {
                        _db.MarkDone(record.Path, FileStatus.Duplicate, result.AssetId);
                        alreadyThere++;
                        Interlocked.Increment(ref _runDone);
                    }
                    else
                    {
                        _db.MarkUnsupported(record.Path, Loc.T("This file type isn't supported by your Immich server."));
                        _activity.Add(ActivityKind.Warning, Loc.T("Skipped {0} — file type not supported by the server", Path.GetFileName(record.Path)), record.Path);
                        Interlocked.Increment(ref _runDone);
                    }
                }

                _detail = null;
                Publish();

                await Parallel.ForEachAsync(
                    toUpload,
                    new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrentUploads, CancellationToken = ct },
                    async (record, token) =>
                    {
                        if (await UploadOneAsync(client, record, token))
                        {
                            Interlocked.Increment(ref uploaded);
                            var folder = FolderNameOf(settings, record.Path);
                            lock (byFolder)
                                byFolder[folder] = byFolder.GetValueOrDefault(folder) + 1;
                        }
                    });

                await SyncAlbumsDuringUploadAsync(client, ct);
                RefreshCounts(settings);
            }
        }
        finally
        {
            if (uploaded > 0 || alreadyThere > 0)
            {
                var message = uploaded > 0 ? Loc.T("Backed up {0}", Format.Plural(uploaded, "new file")) : Loc.T("No new files to upload");
                if (alreadyThere > 0)
                    message += $" · {Format.Count(alreadyThere)} already on the server";
                _activity.Add(ActivityKind.Success, message);
            }
            try
            {
                List<(string, int)> perFolder;
                lock (byFolder)
                    perFolder = byFolder.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
                RunFinished?.Invoke(new RunSummary(uploaded, alreadyThere, _runFailed, started.Elapsed, perFolder));
            }
            catch (Exception ex)
            {
                Log.Error("Run listener failed", ex);
            }
        }
    }

    /// <summary>Hashes a batch, skipping files that vanished, are still being written, or can't be read.</summary>
    async Task<List<FileRecord>> PrepareBatchAsync(List<FileRecord> batch, CancellationToken ct)
    {
        var ready = new List<FileRecord>(batch.Count);
        foreach (var record in batch)
        {
            ct.ThrowIfCancellationRequested();
            if (Skip(record))
                continue;

            var file = new FileInfo(record.Path);
            if (!file.Exists)
            {
                _db.DeletePaths([record.Path]);
                Interlocked.Increment(ref _runDone);
                continue;
            }

            if (DateTime.UtcNow - file.LastWriteTimeUtc < FileSettleTime)
            {
                _db.Defer(record.Path, DateTime.UtcNow + FileSettleTime + TimeSpan.FromSeconds(5));
                continue;
            }

            var current = record;
            if (file.Length != record.Size || file.LastWriteTimeUtc.Ticks != record.ModifiedTicks)
            {
                current = record with { Size = file.Length, ModifiedTicks = file.LastWriteTimeUtc.Ticks, Checksum = null };
                _db.UpdateStat(record.Path, current.Size, current.ModifiedTicks);
            }

            if (current.Checksum is null)
            {
                try
                {
                    var checksum = await FileHasher.Sha1HexAsync(record.Path, ct);
                    _db.SetChecksum(record.Path, checksum);
                    current = current with { Checksum = checksum };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _db.MarkFailed(record.Path, Loc.T("Couldn't read the file: {0}", ex.Message), NextAttempt(record.Attempts + 1));
                    Interlocked.Increment(ref _runDone);
                    Interlocked.Increment(ref _runFailed);
                    continue;
                }
            }

            ready.Add(current);
        }
        return ready;
    }

    /// <summary>Drops a queued file that the current folder rules no longer want.</summary>
    bool Skip(FileRecord record)
    {
        var wanted = IsWanted(record.Path, record.FolderId);
        if (wanted == Wanted.Yes)
            return false;
        if (wanted == Wanted.Excluded)
            _db.DeletePaths([record.Path]);
        Interlocked.Increment(ref _runDone);
        return true;
    }

    async Task<bool> UploadOneAsync(ImmichClient client, FileRecord record, CancellationToken ct)
    {
        if (Skip(record))
            return false;

        using var tracker = new UploadTracker(record.Path, record.FolderId, record.Size, ct);
        _active[record.Path] = tracker;
        ct = tracker.Token;
        try
        {
            var file = new FileInfo(record.Path);
            if (!file.Exists)
            {
                _db.DeletePaths([record.Path]);
                Interlocked.Increment(ref _runDone);
                return false;
            }
            if (file.Length != record.Size || file.LastWriteTimeUtc.Ticks != record.ModifiedTicks)
            {
                // Changed since it was hashed; it'll be picked up again with a fresh checksum.
                _db.UpdateStat(record.Path, file.Length, file.LastWriteTimeUtc.Ticks);
                return false;
            }

            var created = file.CreationTimeUtc < file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;
            var request = new UploadRequest(record.Path, file.Name, record.Size, record.Checksum!, created, file.LastWriteTimeUtc);
            var result = await client.UploadAsync(request, _limiter, sent =>
            {
                var delta = sent - tracker.Sent;
                tracker.Sent = sent;
                Interlocked.Add(ref _bytesSent, delta);
            }, ct);

            _db.MarkDone(record.Path, result.IsDuplicate ? FileStatus.Duplicate : FileStatus.Uploaded, result.Id);
            Interlocked.Increment(ref _runDone);
            if (!result.IsDuplicate)
                _activity.Add(ActivityKind.Uploaded, file.Name, record.Path);
            return !result.IsDuplicate;
        }
        catch (OperationCanceledException) when (tracker.WasCancelledIndividually)
        {
            // Excluded or its folder turned off while uploading: stop just this file.
            Log.Info($"Stopped uploading {record.Path}: no longer included");
            Skip(record);
            return false;
        }
        catch (ImmichApiException ex) when (!ex.IsUnauthorized && !ex.IsTransient)
        {
            Fail(record, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !NetworkErrors.IsConnectivity(ex))
        {
            Fail(record, Loc.T("Couldn't read the file: {0}", ex.Message));
            return false;
        }
        catch (Exception ex) when (NetworkErrors.IsConnectivity(ex))
        {
            // Server or network went away: put the file back and let the cycle handle going offline.
            _db.Defer(record.Path, DateTime.UtcNow.AddSeconds(30));
            throw;
        }
        finally
        {
            _active.TryRemove(record.Path, out _);
        }
    }

    void Fail(FileRecord record, string reason)
    {
        Log.Warn($"Upload failed for {record.Path}: {reason}");
        _db.MarkFailed(record.Path, reason, NextAttempt(record.Attempts + 1));
        Interlocked.Increment(ref _runDone);
        Interlocked.Increment(ref _runFailed);
        _activity.Add(ActivityKind.Error, Loc.T("Couldn't upload {0}: {1}", Path.GetFileName(record.Path), reason), record.Path);
    }

    static DateTime NextAttempt(int attempts) =>
        DateTime.UtcNow + TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempts), 12 * 60));


    sealed class UploadTracker(string path, string folderId, long size, CancellationToken runToken) : IDisposable
    {
        readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        volatile bool _cancelled;
        long _sent;
        public string Path { get; } = path;
        public string FolderId { get; } = folderId;
        public long Size { get; } = size;
        public CancellationToken Token => _cts.Token;
        public bool WasCancelledIndividually => _cancelled && !runToken.IsCancellationRequested;

        public void Cancel()
        {
            _cancelled = true;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        }

        public void Dispose() => _cts.Dispose();

        public long Sent
        {
            get => Interlocked.Read(ref _sent);
            set => Interlocked.Exchange(ref _sent, value);
        }
    }

    sealed class WaitForPowerException : Exception;

    sealed class WaitForScheduleException : Exception;

    /// <summary>Outside the backup hours: wait until they start (or until the settings change).</summary>
    TimeSpan WaitForWindow(AppSettings settings)
    {
        RefreshCounts(settings);
        SetState(SyncState.WaitingForSchedule);
        var now = Now();
        return BackupSchedule.NextOpen(settings, now) - now + TimeSpan.FromSeconds(1);
    }

    sealed class WaitForNetworkException : Exception;

    bool _deferredVideosNoted;

    /// <summary>The backed-up folder a file belongs to, by name (the deepest folder that contains it).</summary>
    static string FolderNameOf(AppSettings settings, string path)
    {
        var folder = settings.Folders
            .Where(f => path.StartsWith(f.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Path.Length)
            .FirstOrDefault();
        return folder is null ? "" : Path.GetFileName(folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : folder.Path;
    }
}
