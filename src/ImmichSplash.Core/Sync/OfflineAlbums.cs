using System.Text.Json;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Services;
using ImmichSplash.Core.Settings;

namespace ImmichSplash.Core.Sync;

/// <summary>An album kept on this PC: its photos are downloaded into <see cref="Folder"/> and kept up to date.</summary>
public sealed class OfflineAlbum
{
    public string AlbumId { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public string Folder { get; set; } = "";
}

/// <summary>
/// Keeps chosen albums on this PC, so you can look at them without a connection (in File Explorer, or any photo
/// app): new photos in the album are downloaded, and ones taken out of it are removed again. Only files it
/// downloaded itself are ever deleted (they're listed in a small file in the folder), and these folders are
/// never backed up again.
/// </summary>
public sealed class OfflineAlbums : IDisposable
{
    const string ManifestName = ".immich-offline.json";
    static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    readonly SettingsStore _settings;
    readonly AccountService _account;
    readonly Func<bool> _isMetered;
    readonly CancellationTokenSource _lifetime = new();
    readonly SemaphoreSlim _wake = new(0);
    int _running;

    public OfflineAlbums(SettingsStore settings, AccountService account, Func<bool> isMetered)
    {
        _settings = settings;
        _account = account;
        _isMetered = isMetered;
        ApplyFolders(settings.Current);
        settings.Changed += ApplyFolders;
    }

    /// <summary>Where offline albums go by default: a folder in your home folder (not in Pictures, which is often backed up).</summary>
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Immich Offline");

    public static string FolderFor(string albumName) =>
        Path.Combine(DefaultRoot, string.Concat(albumName.Split(Path.GetInvalidFileNameChars())).Trim() is { Length: > 0 } name ? name : "Album");

    /// <summary>Progress for the album page: (album id, downloaded, total), or null when idle.</summary>
    public event Action<string, int, int>? Progress;

    static void ApplyFolders(AppSettings settings) =>
        FileFilter.SkipFolders = settings.OfflineAlbums.Select(a => a.Folder).Where(f => f.Length > 0).ToList();

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>Check now (an album was just added).</summary>
    public void SyncSoon() => _wake.Release();

    async Task LoopAsync()
    {
        var ct = _lifetime.Token;
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncAllAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("Updating offline albums failed", ex);
            }
            try
            {
                await _wake.WaitAsync(Every, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task SyncAllAsync(CancellationToken ct)
    {
        if (_account.Client is not { } client || _settings.Current.OfflineAlbums.Count == 0 || _isMetered())
            return;
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        try
        {
            foreach (var album in _settings.Current.OfflineAlbums.ToList())
                await SyncAsync(client, album, ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    async Task SyncAsync(ImmichClient client, OfflineAlbum album, CancellationToken ct)
    {
        Directory.CreateDirectory(album.Folder);
        var manifestPath = Path.Combine(album.Folder, ManifestName);
        var manifest = LoadManifest(manifestPath);
        var assets = await client.GetAllTimelineAssetsAsync(new TimelineFilter(AlbumId: album.AlbumId), ct);
        var inAlbum = assets.Select(a => a.Id).ToHashSet();

        // Taken out of the album: remove our copy.
        foreach (var (id, name) in manifest.ToList())
        {
            if (inAlbum.Contains(id))
                continue;
            try
            {
                var path = Path.Combine(album.Folder, name);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Info($"Couldn't remove an offline copy: {ex.Message}");
            }
            manifest.Remove(id);
        }

        var missing = assets.Where(a => !manifest.TryGetValue(a.Id, out var name) || !File.Exists(Path.Combine(album.Folder, name))).ToList();
        var done = 0;
        foreach (var asset in missing)
        {
            ct.ThrowIfCancellationRequested();
            if (_isMetered())
                break;
            try
            {
                var details = await client.GetAssetAsync(asset.Id, ct);
                var name = UniqueName(album.Folder, details.OriginalFileName ?? $"{asset.Id}.jpg");
                var temp = Path.Combine(album.Folder, name + ".part");
                await using (var file = File.Create(temp))
                    await client.DownloadOriginalAsync(asset.Id, file, ct);
                File.Move(temp, Path.Combine(album.Folder, name), overwrite: true);
                if (DateTimeOffset.TryParse(details.DateTimeOriginal ?? details.FileCreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var taken))
                    File.SetLastWriteTimeUtc(Path.Combine(album.Folder, name), taken.UtcDateTime);
                manifest[asset.Id] = name;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Info($"Offline copy of {asset.Id} failed: {ex.Message}");
            }
            done++;
            Progress?.Invoke(album.AlbumId, done, missing.Count);
            if (done % 20 == 0)
                SaveManifest(manifestPath, manifest);
        }
        SaveManifest(manifestPath, manifest);
        if (missing.Count > 0)
            Log.Info($"Offline album updated: {done} downloaded");
    }

    static Dictionary<string, string> LoadManifest(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch
        {
            return [];
        }
    }

    static void SaveManifest(string path, Dictionary<string, string> manifest)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(manifest));
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't save the offline album list: {ex.Message}");
        }
    }

    static string UniqueName(string folder, string name)
    {
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; File.Exists(Path.Combine(folder, name)); i++)
            name = $"{stem} ({i}){ext}";
        return name;
    }

    /// <summary>Stops keeping an album (its downloaded copies are deleted when <paramref name="deleteFiles"/>).</summary>
    public static void Forget(OfflineAlbum album, bool deleteFiles)
    {
        if (!deleteFiles)
            return;
        var manifestPath = Path.Combine(album.Folder, ManifestName);
        foreach (var name in LoadManifest(manifestPath).Values)
        {
            try { File.Delete(Path.Combine(album.Folder, name)); } catch { /* in use or gone */ }
        }
        try
        {
            File.Delete(manifestPath);
            if (Directory.Exists(album.Folder) && !Directory.EnumerateFileSystemEntries(album.Folder).Any())
                Directory.Delete(album.Folder);
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't tidy the offline album folder: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _settings.Changed -= ApplyFolders;
    }
}
