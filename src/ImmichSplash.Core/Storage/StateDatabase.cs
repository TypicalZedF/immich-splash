using ImmichSplash.Core.Sync;
using Microsoft.Data.Sqlite;

namespace ImmichSplash.Core.Storage;

/// <summary>
/// Local SQLite index of every tracked file and its backup state, so restarts never re-hash or re-upload
/// files that haven't changed. Safe to use from multiple threads (one pooled connection per call, WAL mode).
/// </summary>
public sealed class StateDatabase
{
    const int SchemaVersion = 3; // 2: privacy_scan, 3: stacked
    const int ActivityKeep = 1000;

    readonly string _connectionString;
    int _activityInserts;

    public StateDatabase(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        Migrate();
    }

    // ---- Schema ---------------------------------------------------------------------------------

    void Migrate()
    {
        using var db = Open();
        Exec(db, "PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(Scalar(db, "PRAGMA user_version;"));
        if (version >= SchemaVersion)
            return;

        Exec(db, $"""
            CREATE TABLE IF NOT EXISTS files (
                path         TEXT    NOT NULL PRIMARY KEY,
                folder_id    TEXT    NOT NULL,
                size         INTEGER NOT NULL,
                mtime        INTEGER NOT NULL,
                checksum     TEXT,
                status       INTEGER NOT NULL DEFAULT 0,
                asset_id     TEXT,
                error        TEXT,
                attempts     INTEGER NOT NULL DEFAULT 0,
                next_attempt INTEGER,
                album_done   INTEGER NOT NULL DEFAULT 0,
                updated_at   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_files_status ON files(status, next_attempt);
            CREATE INDEX IF NOT EXISTS ix_files_folder ON files(folder_id, status);
            CREATE TABLE IF NOT EXISTS activity (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                time    INTEGER NOT NULL,
                kind    INTEGER NOT NULL,
                message TEXT    NOT NULL,
                path    TEXT
            );
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            -- Results of the on-device privacy check (only ever stored here, on this PC).
            CREATE TABLE IF NOT EXISTS privacy_scan (
                asset_id   TEXT    NOT NULL PRIMARY KEY,
                version    INTEGER NOT NULL,
                kinds      INTEGER NOT NULL,
                score      REAL    NOT NULL DEFAULT 0,
                decision   INTEGER NOT NULL DEFAULT 0,
                scanned_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_privacy_open ON privacy_scan(decision, kinds);
            -- Files already put in a RAW+JPEG stack (or found not to need one).
            CREATE TABLE IF NOT EXISTS stacked (path TEXT NOT NULL PRIMARY KEY);
            PRAGMA user_version = {SchemaVersion};
            """);
    }

    // ---- Privacy check (results stay on this PC) -------------------------------------------------

    /// <summary>Assets already checked with this version of the checks.</summary>
    public HashSet<string> GetPrivacyScanned(int version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT asset_id FROM privacy_scan WHERE version >= $v";
        cmd.Parameters.AddWithValue("$v", version);
        using var reader = cmd.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Saves what was found in one asset; a choice already made about it is kept.</summary>
    public void SavePrivacyScan(string assetId, int version, int kinds, double score)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO privacy_scan (asset_id, version, kinds, score, decision, scanned_at) VALUES ($id, $v, $k, $s, 0, $t)
            ON CONFLICT(asset_id) DO UPDATE SET version = $v, kinds = $k, score = $s, scanned_at = $t
            """;
        cmd.Parameters.AddWithValue("$id", assetId);
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$k", kinds);
        cmd.Parameters.AddWithValue("$s", score);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Flagged assets nobody has decided about yet, most certain first.</summary>
    public List<(string AssetId, int Kinds, double Score)> GetPrivacySuggestions()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT asset_id, kinds, score FROM privacy_scan WHERE decision = 0 AND kinds <> 0 ORDER BY score DESC, scanned_at DESC";
        using var reader = cmd.ExecuteReader();
        var result = new List<(string, int, double)>();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetInt32(1), reader.GetDouble(2)));
        return result;
    }

    public int CountPrivacySuggestions()
    {
        using var db = Open();
        return Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM privacy_scan WHERE decision = 0 AND kinds <> 0"));
    }

    /// <summary>Records a choice: 1 moved to the locked folder, 2 not sensitive (don't suggest again), 3 moved to the trash.</summary>
    public void SetPrivacyDecision(IReadOnlyCollection<string> assetIds, int decision)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE privacy_scan SET decision = $d WHERE asset_id = $id";
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        cmd.Parameters.AddWithValue("$d", decision);
        foreach (var assetId in assetIds)
        {
            id.Value = assetId;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Forgets "nothing found" results saved before <paramref name="before"/> for these assets, so they get checked again.
    /// Results you've already decided on are kept. Returns how many were forgotten.
    /// </summary>
    public int ForgetCleanScans(IEnumerable<string> assetIds, DateTime before)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM privacy_scan WHERE asset_id = $id AND kinds = 0 AND decision = 0 AND scanned_at < $t";
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        cmd.Parameters.AddWithValue("$t", before.Ticks);
        var forgotten = 0;
        foreach (var assetId in assetIds)
        {
            id.Value = assetId;
            forgotten += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return forgotten;
    }

    /// <summary>Forgets every result (e.g. when the check is turned off and its data cleared).</summary>
    public void ClearPrivacyScans()
    {
        using var db = Open();
        Exec(db, "DELETE FROM privacy_scan");
    }

    SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Exec(connection, "PRAGMA busy_timeout = 10000; PRAGMA synchronous = NORMAL;");
        return connection;
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    static long Now => DateTime.UtcNow.Ticks;

    /// <summary>Adds "AND folder_id IN (...)" with one parameter per id.</summary>
    static string FolderFilter(SqliteCommand cmd, IReadOnlyCollection<string> folderIds)
    {
        if (folderIds.Count == 0)
            return " AND 0";
        var names = new List<string>(folderIds.Count);
        var i = 0;
        foreach (var id in folderIds)
        {
            var name = "$f" + i++;
            cmd.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        return $" AND folder_id IN ({string.Join(",", names)})";
    }

    /// <summary>Range bounds that select every path strictly under <paramref name="directory"/> (binary collation).</summary>
    static (string Low, string High) ChildRange(string directory)
    {
        var sep = Path.DirectorySeparatorChar;
        var low = directory.TrimEnd(sep, Path.AltDirectorySeparatorChar) + sep;
        var high = low[..^1] + (char)(sep + 1);
        return (low, high);
    }

    // ---- Scanning -------------------------------------------------------------------------------

    /// <summary>Known files of a folder (optionally only under a subdirectory), keyed by path.</summary>
    public Dictionary<string, (long Size, long ModifiedTicks)> GetIndex(string folderId, string? underDirectory, IEqualityComparer<string> comparer)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path, size, mtime FROM files WHERE folder_id = $folder";
        cmd.Parameters.AddWithValue("$folder", folderId);
        if (underDirectory is not null)
        {
            var (low, high) = ChildRange(underDirectory);
            cmd.CommandText += " AND path >= $low AND path < $high";
            cmd.Parameters.AddWithValue("$low", low);
            cmd.Parameters.AddWithValue("$high", high);
        }

        var result = new Dictionary<string, (long, long)>(comparer);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        return result;
    }

    public (long Size, long ModifiedTicks)? GetStat(string path)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT size, mtime FROM files WHERE path = $path";
        cmd.Parameters.AddWithValue("$path", path);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    /// <summary>
    /// Inserts new files and marks changed ones (different size or modified time) as pending again.
    /// Unchanged files keep their state, even when they move to a different folder id.
    /// </summary>
    public void Upsert(IReadOnlyCollection<ScannedFile> files)
    {
        if (files.Count == 0)
            return;

        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO files (path, folder_id, size, mtime, status, updated_at)
            VALUES ($path, $folder, $size, $mtime, 0, $now)
            ON CONFLICT(path) DO UPDATE SET
                checksum     = CASE WHEN files.size = excluded.size AND files.mtime = excluded.mtime THEN files.checksum ELSE NULL END,
                status       = CASE WHEN files.size = excluded.size AND files.mtime = excluded.mtime THEN files.status ELSE 0 END,
                asset_id     = CASE WHEN files.size = excluded.size AND files.mtime = excluded.mtime THEN files.asset_id ELSE NULL END,
                error        = CASE WHEN files.size = excluded.size AND files.mtime = excluded.mtime THEN files.error ELSE NULL END,
                attempts     = CASE WHEN files.size = excluded.size AND files.mtime = excluded.mtime THEN files.attempts ELSE 0 END,
                next_attempt = CASE WHEN files.size = excluded.size AND files.mtime = excluded.mtime THEN files.next_attempt ELSE NULL END,
                album_done   = CASE WHEN files.size = excluded.size AND files.mtime = excluded.mtime AND files.folder_id = excluded.folder_id THEN files.album_done ELSE 0 END,
                folder_id    = excluded.folder_id,
                size         = excluded.size,
                mtime        = excluded.mtime,
                updated_at   = excluded.updated_at;
            """;
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pFolder = cmd.Parameters.Add("$folder", SqliteType.Text);
        var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
        var pMtime = cmd.Parameters.Add("$mtime", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$now", Now);
        cmd.Prepare();

        foreach (var file in files)
        {
            pPath.Value = file.Path;
            pFolder.Value = file.FolderId;
            pSize.Value = file.Size;
            pMtime.Value = file.ModifiedTicks;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeletePaths(IEnumerable<string> paths)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM files WHERE path = $path";
        var p = cmd.Parameters.Add("$path", SqliteType.Text);
        foreach (var path in paths)
        {
            p.Value = path;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Forgets a path and, if it was a directory, everything beneath it.</summary>
    public void DeleteUnder(string path)
    {
        var (low, high) = ChildRange(path);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE path = $path OR (path >= $low AND path < $high)";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$low", low);
        cmd.Parameters.AddWithValue("$high", high);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Removes rows for folders that are no longer configured.</summary>
    public void DeleteFoldersExcept(IReadOnlyCollection<string> folderIds)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        if (folderIds.Count == 0)
        {
            cmd.CommandText = "DELETE FROM files";
        }
        else
        {
            var filter = FolderFilter(cmd, folderIds);
            cmd.CommandText = "DELETE FROM files WHERE 1" + filter.Replace("folder_id IN", "folder_id NOT IN", StringComparison.Ordinal);
        }
        cmd.ExecuteNonQuery();
    }

    // ---- Upload queue ---------------------------------------------------------------------------

    const string CandidateWhere = "status IN (0, 3) AND (next_attempt IS NULL OR next_attempt <= $now)";

    public int CountCandidates(IReadOnlyCollection<string> folderIds)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$now", Now);
        cmd.CommandText = $"SELECT COUNT(*) FROM files WHERE {CandidateWhere}" + FolderFilter(cmd, folderIds);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Total size of the files waiting to upload (for the time estimate).</summary>
    public long SumCandidateBytes(IReadOnlyCollection<string> folderIds)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$now", Now);
        cmd.CommandText = $"SELECT COALESCE(SUM(size), 0) FROM files WHERE {CandidateWhere}" + FolderFilter(cmd, folderIds);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Photos and videos in these folders that aren't in Immich yet (waiting, or failed and to be retried).</summary>
    public List<(string Path, long ModifiedTicks, bool Failed)> GetLocalOnly(IReadOnlyCollection<string> folderIds)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path, mtime, status FROM files WHERE status IN (0, 3)" + FolderFilter(cmd, folderIds);
        using var reader = cmd.ExecuteReader();
        var result = new List<(string, long, bool)>();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2) == (int)FileStatus.Failed));
        return result;
    }

    /// <summary>Immich assets that came from (or match) a file on this PC, and where that file is.</summary>
    public Dictionary<string, string> GetAssetsOnDevice()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT asset_id, path FROM files WHERE status IN (1, 2) AND asset_id IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            result.TryAdd(reader.GetString(0), reader.GetString(1));
        return result;
    }

    /// <summary>Next files to upload, newest first (like the mobile app).</summary>
    public List<FileRecord> GetCandidates(IReadOnlyCollection<string> folderIds, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue("$now", Now);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = $"SELECT path, folder_id, size, mtime, checksum, attempts FROM files WHERE {CandidateWhere}"
                          + FolderFilter(cmd, folderIds) + " ORDER BY mtime DESC LIMIT $limit";

        var result = new List<FileRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FileRecord(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5)));
        }
        return result;
    }

    public DateTime? GetNextAttempt(IReadOnlyCollection<string> folderIds)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT MIN(next_attempt) FROM files WHERE status IN (0, 3) AND next_attempt IS NOT NULL" + FolderFilter(cmd, folderIds);
        return cmd.ExecuteScalar() is long ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;
    }

    void Update(string sql, string path, params (string Name, object? Value)[] values)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$now", Now);
        foreach (var (name, value) in values)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void UpdateStat(string path, long size, long modifiedTicks) =>
        Update("UPDATE files SET size = $size, mtime = $mtime, checksum = NULL, status = 0, updated_at = $now WHERE path = $path",
            path, ("$size", size), ("$mtime", modifiedTicks));

    public void SetChecksum(string path, string checksum) =>
        Update("UPDATE files SET checksum = $checksum, updated_at = $now WHERE path = $path", path, ("$checksum", checksum));

    public void MarkDone(string path, FileStatus status, string? assetId) =>
        Update("UPDATE files SET status = $status, asset_id = $asset, error = NULL, attempts = 0, next_attempt = NULL, updated_at = $now WHERE path = $path",
            path, ("$status", (int)status), ("$asset", assetId));

    public void MarkFailed(string path, string error, DateTime nextAttemptUtc) =>
        Update("UPDATE files SET status = 3, error = $error, attempts = attempts + 1, next_attempt = $next, updated_at = $now WHERE path = $path",
            path, ("$error", error), ("$next", nextAttemptUtc.Ticks));

    public void MarkUnsupported(string path, string reason) =>
        Update("UPDATE files SET status = 4, error = $error, next_attempt = NULL, updated_at = $now WHERE path = $path",
            path, ("$error", reason));

    /// <summary>Postpones a file without counting it as a failure (e.g. it's still being written).</summary>
    public void Defer(string path, DateTime untilUtc) =>
        Update("UPDATE files SET next_attempt = $next, updated_at = $now WHERE path = $path", path, ("$next", untilUtc.Ticks));

    /// <summary>Makes failed files eligible again right away. Returns how many were reset.</summary>
    public int RetryFailed()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE files SET next_attempt = NULL, attempts = 0 WHERE status = 3 OR (status = 0 AND next_attempt IS NOT NULL)";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Forgets every file, so the next scan re-checks everything against the server.</summary>
    public void ResetAll()
    {
        using var db = Open();
        Exec(db, "DELETE FROM files;");
    }

    public List<FailedFile> GetFailed(int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path, error, next_attempt FROM files WHERE status = 3 ORDER BY updated_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<FailedFile>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FailedFile(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero)));
        }
        return result;
    }

    // ---- Statistics -----------------------------------------------------------------------------

    public Dictionary<string, StatusCounts> GetFolderCounts()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT folder_id, status, COUNT(*) FROM files GROUP BY folder_id, status";
        var result = new Dictionary<string, StatusCounts>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var folder = reader.GetString(0);
            var count = reader.GetInt32(2);
            result.TryGetValue(folder, out var c);
            result[folder] = (FileStatus)reader.GetInt32(1) switch
            {
                FileStatus.Pending => c with { Pending = c.Pending + count },
                FileStatus.Uploaded => c with { Uploaded = c.Uploaded + count },
                FileStatus.Duplicate => c with { Duplicate = c.Duplicate + count },
                FileStatus.Failed => c with { Failed = c.Failed + count },
                FileStatus.Unsupported => c with { Unsupported = c.Unsupported + count },
                _ => c,
            };
        }
        return result;
    }

    public StatusCounts GetCounts(IReadOnlyCollection<string> folderIds)
    {
        var all = GetFolderCounts();
        var total = default(StatusCounts);
        foreach (var id in folderIds)
        {
            if (all.TryGetValue(id, out var c))
                total += c;
        }
        return total;
    }

    // ---- RAW+JPEG stacks --------------------------------------------------------------------------

    /// <summary>Backed-up files that aren't in a RAW+JPEG stack yet.</summary>
    public List<(string Path, string AssetId)> GetUnstackedUploads()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path, asset_id FROM files WHERE status IN (1, 2) AND asset_id IS NOT NULL AND path NOT IN (SELECT path FROM stacked)";
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public void MarkStacked(IEnumerable<string> paths)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO stacked (path) VALUES ($path)";
        var p = cmd.Parameters.Add("$path", SqliteType.Text);
        foreach (var path in paths)
        {
            p.Value = path;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---- Albums ---------------------------------------------------------------------------------

    public List<(string Path, string AssetId)> GetAlbumWork(string folderId, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path, asset_id FROM files WHERE folder_id = $folder AND status IN (1, 2) AND asset_id IS NOT NULL AND album_done = 0 LIMIT $limit";
        cmd.Parameters.AddWithValue("$folder", folderId);
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public void MarkAlbumDone(IEnumerable<string> paths)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE files SET album_done = 1 WHERE path = $path";
        var p = cmd.Parameters.Add("$path", SqliteType.Text);
        foreach (var path in paths)
        {
            p.Value = path;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ResetAlbumFlags(string folderId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE files SET album_done = 0 WHERE folder_id = $folder";
        cmd.Parameters.AddWithValue("$folder", folderId);
        cmd.ExecuteNonQuery();
    }

    // ---- Meta & activity ------------------------------------------------------------------------

    public string? GetMeta(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string? value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public long AddActivity(DateTimeOffset time, ActivityKind kind, string message, string? path)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO activity (time, kind, message, path) VALUES ($time, $kind, $message, $path); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$time", time.UtcTicks);
        cmd.Parameters.AddWithValue("$kind", (int)kind);
        cmd.Parameters.AddWithValue("$message", message);
        cmd.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
        var id = (long)cmd.ExecuteScalar()!;

        if (Interlocked.Increment(ref _activityInserts) % 100 == 0)
            Exec(db, $"DELETE FROM activity WHERE id <= (SELECT MAX(id) - {ActivityKeep} FROM activity);");
        return id;
    }

    public List<ActivityEntry> GetActivity(int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, time, kind, message, path FROM activity ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<ActivityEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActivityEntry(
                reader.GetInt64(0),
                new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero).ToLocalTime(),
                (ActivityKind)reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
    }

    public void ClearActivity()
    {
        using var db = Open();
        Exec(db, "DELETE FROM activity;");
    }
}
