using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Sync;

namespace ImmichSplash.Core.Import;

/// <summary>What Google Photos knew about a photo, from the .json file Takeout puts next to it.</summary>
public sealed record TakeoutMetadata(DateTimeOffset? Taken, double? Latitude, double? Longitude, string? Description, bool Favorite, bool Archived);

/// <summary>One photo or video in a Takeout export: a file in a folder, or an entry in one of the .zip files.</summary>
public sealed record TakeoutItem(string? Zip, string Path, long Size, string? Album, TakeoutMetadata? Metadata)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed record TakeoutProgress(string Stage, int Done, int Total, int Uploaded, int AlreadyThere, int Failed);

public sealed record TakeoutResult(int Uploaded, int AlreadyThere, int Failed, int Albums);

/// <summary>
/// Imports a Google Photos Takeout export into Immich, keeping what Google knew: when each photo was taken, where,
/// its description, favorites, archived photos and albums. Works straight from the .zip files (nothing to unpack
/// first; each file is copied out one at a time), or from an unpacked folder. Photos already in Immich are skipped.
/// </summary>
public static partial class GoogleTakeout
{
    // ---- Reading the export ----

    /// <summary>Finds every photo and video, with its metadata and album, in the given folders and .zip files.</summary>
    public static List<TakeoutItem> Scan(IEnumerable<string> sources, MediaTypes media, CancellationToken ct = default)
    {
        // The .json files first, from every source: Takeout splits big exports across zips, and a photo's .json
        // isn't always in the same zip as the photo.
        var jsons = new Dictionary<string, Dictionary<string, JsonEntry>>(StringComparer.OrdinalIgnoreCase); // folder → name → json
        var files = new List<(string? Zip, string Path, long Size)>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(source);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/'))
                        continue;
                    var path = entry.FullName.Replace('\\', '/');
                    if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (entry.Length < 1_000_000)
                        {
                            using var reader = new StreamReader(entry.Open());
                            AddJson(jsons, path, reader.ReadToEnd());
                        }
                    }
                    else if (media.Classify(path) != MediaKind.None)
                        files.Add((source, path, entry.Length));
                }
            }
            else if (Directory.Exists(source))
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    var path = System.IO.Path.GetRelativePath(source, file).Replace('\\', '/');
                    if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (new FileInfo(file).Length < 1_000_000)
                            AddJson(jsons, path, File.ReadAllText(file));
                    }
                    else if (media.Classify(file) != MediaKind.None)
                        files.Add((null, file, new FileInfo(file).Length));
                }
            }
        }

        var items = new List<TakeoutItem>(files.Count);
        foreach (var (zip, path, size) in files)
        {
            var folder = FolderOf(zip is null ? path.Replace('\\', '/') : path);
            jsons.TryGetValue(folder, out var inFolder);
            var album = inFolder is not null && inFolder.TryGetValue("metadata.json", out var albumJson) ? albumJson.AlbumTitle : null;
            // Some exports put albums' details in a localized file name; any .json with only a title counts.
            album ??= inFolder?.Values.FirstOrDefault(j => j.AlbumTitle is not null && j.Metadata is null)?.AlbumTitle;
            var name = System.IO.Path.GetFileName(path);
            var metadata = inFolder is null ? null : FindSidecar(name, inFolder)?.Metadata;
            items.Add(new TakeoutItem(zip, path, size, album, metadata));
        }
        return items;
    }

    internal sealed record JsonEntry(TakeoutMetadata? Metadata, string? AlbumTitle);

    static string FolderOf(string path)
    {
        var at = path.LastIndexOf('/');
        var parent = at < 0 ? "" : path[..at];
        return System.IO.Path.GetFileName(parent);
    }

    static void AddJson(Dictionary<string, Dictionary<string, JsonEntry>> jsons, string path, string text)
    {
        if (Parse(text) is not { } entry)
            return;
        var folder = FolderOf(path);
        if (!jsons.TryGetValue(folder, out var inFolder))
            jsons[folder] = inFolder = new Dictionary<string, JsonEntry>(StringComparer.OrdinalIgnoreCase);
        inFolder[System.IO.Path.GetFileName(path)] = entry;
    }

    static JsonEntry? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            DateTimeOffset? taken = null;
            foreach (var key in new[] { "photoTakenTime", "creationTime" })
                if (taken is null && root.TryGetProperty(key, out var time) && time.TryGetProperty("timestamp", out var ts)
                    && long.TryParse(ts.ValueKind == JsonValueKind.String ? ts.GetString() : ts.GetRawText(), out var seconds) && seconds > 0)
                    taken = DateTimeOffset.FromUnixTimeSeconds(seconds);
            double? lat = null, lon = null;
            foreach (var key in new[] { "geoData", "geoDataExif" })
                if (lat is null && root.TryGetProperty(key, out var geo)
                    && geo.TryGetProperty("latitude", out var la) && geo.TryGetProperty("longitude", out var lo)
                    && la.TryGetDouble(out var a) && lo.TryGetDouble(out var o) && (a != 0 || o != 0))
                    (lat, lon) = (a, o);
            var description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } text2 ? text2 : null;
            var favorite = root.TryGetProperty("favorited", out var f) && f.ValueKind == JsonValueKind.True;
            var archived = root.TryGetProperty("archived", out var ar) && ar.ValueKind == JsonValueKind.True;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            // A photo's .json has when it was taken; an album's metadata.json has just a title (and a date).
            var isPhoto = root.TryGetProperty("photoTakenTime", out _);
            return isPhoto
                ? new JsonEntry(new TakeoutMetadata(taken, lat, lon, description, favorite, archived), null)
                : new JsonEntry(null, string.IsNullOrWhiteSpace(title) ? null : title);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The .json for a photo. Google names it "photo.jpg.json" or "photo.jpg.supplemental-metadata.json", cuts long
    /// names short (to 51 characters), puts a copy number after the extension ("photo(1).jpg" → "photo.jpg(1).json"),
    /// shares one between "photo-edited.jpg" and "photo.jpg", and between a live photo's picture and video.
    /// </summary>
    internal static JsonEntry? FindSidecar(string name, IReadOnlyDictionary<string, JsonEntry> inFolder)
    {
        JsonEntry? Exact(string candidate) => inFolder.TryGetValue(candidate, out var j) && j.Metadata is not null ? j : null;

        foreach (var candidate in Candidates(name))
        {
            if (Exact(candidate + ".json") is { } exact)
                return exact;
            if (Exact(candidate + ".supplemental-metadata.json") is { } supplemental)
                return supplemental;
        }
        // Cut short: the longest .json whose name (without ".json") starts "photo.jpg.supplemental-metadata".
        foreach (var candidate in Candidates(name))
        {
            var full = candidate + ".supplemental-metadata";
            JsonEntry? best = null;
            var bestLength = 0;
            foreach (var (jsonName, entry) in inFolder)
            {
                if (entry.Metadata is null)
                    continue;
                var stem = jsonName[..^5];
                if (stem.Length > bestLength && stem.Length >= Math.Min(candidate.Length, 30) && full.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                {
                    best = entry;
                    bestLength = stem.Length;
                }
            }
            if (best is not null)
                return best;
        }
        // A live photo's video: "IMG_1.MP4" uses "IMG_1.HEIC.json".
        var stemOnly = System.IO.Path.GetFileNameWithoutExtension(name) + ".";
        return inFolder.FirstOrDefault(kv => kv.Value.Metadata is not null && kv.Key.StartsWith(stemOnly, StringComparison.OrdinalIgnoreCase)).Value;
    }

    static IEnumerable<string> Candidates(string name)
    {
        yield return name;
        // "photo(1).jpg" → "photo.jpg(1)" and "photo.jpg.supplemental-metadata(1)"
        if (CopyNumber().Match(name) is { Success: true } m)
        {
            var original = m.Groups[1].Value + m.Groups[3].Value;
            yield return $"{original}({m.Groups[2].Value})";
            yield return $"{original}.supplemental-metadata({m.Groups[2].Value})";
            yield return original;
        }
        // "photo-edited.jpg" → "photo.jpg"
        if (Edited().Match(name) is { Success: true } e)
            yield return e.Groups[1].Value + e.Groups[2].Value;
    }

    [GeneratedRegex(@"^(.*)\((\d+)\)(\.[^.]+)$")]
    private static partial Regex CopyNumber();

    [GeneratedRegex(@"^(.*)-(?:edited|bearbeitet|modifié|editado|modificato|bewerkt|edytowane)(\.[^.]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Edited();

    // ---- Uploading ----

    public static async Task<TakeoutResult> ImportAsync(ImmichClient client, IReadOnlyList<TakeoutItem> items, BandwidthLimiter limiter,
        IProgress<TakeoutProgress>? progress, CancellationToken ct)
    {
        int uploaded = 0, alreadyThere = 0, failed = 0, done = 0;
        var albums = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var favorites = new List<string>();
        var archived = new List<string>();
        var temp = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ImmichSplash", "takeout")).FullName;
        var zips = new Dictionary<string, ZipArchive>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Zip by zip, in order, so each archive is read from start to end.
            foreach (var item in items.OrderBy(i => i.Zip).ThenBy(i => i.Path, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                string? extracted = null;
                try
                {
                    var file = item.Path;
                    if (item.Zip is { } zipPath)
                    {
                        if (!zips.TryGetValue(zipPath, out var zip))
                        {
                            foreach (var open in zips.Values)
                                open.Dispose();
                            zips.Clear();
                            zips[zipPath] = zip = ZipFile.OpenRead(zipPath);
                        }
                        var entry = zip.GetEntry(item.Path) ?? throw new FileNotFoundException(item.Path);
                        extracted = file = System.IO.Path.Combine(temp, $"{Guid.NewGuid():N}{System.IO.Path.GetExtension(item.Path)}");
                        entry.ExtractToFile(extracted);
                    }
                    var checksum = await FileHasher.Sha1HexAsync(file, ct);
                    var check = (await client.BulkUploadCheckAsync([new ChecksumItem(item.Path, checksum)], ct)).FirstOrDefault();
                    string assetId;
                    if (check is { IsAccepted: false, AssetId: { } existing })
                    {
                        assetId = existing;
                        alreadyThere++;
                    }
                    else
                    {
                        var taken = item.Metadata?.Taken?.UtcDateTime ?? File.GetLastWriteTimeUtc(file);
                        var result = await client.UploadAsync(new UploadRequest(file, item.FileName, new FileInfo(file).Length, checksum, taken, taken), limiter, null, ct);
                        assetId = result.Id;
                        if (result.IsDuplicate)
                            alreadyThere++;
                        else
                        {
                            uploaded++;
                            // Where it was and its description live in the .json, not in the file.
                            if (item.Metadata is { } m && (m.Latitude is not null || m.Description is not null))
                                await client.UpdateAssetAsync(assetId, null, m.Latitude, m.Longitude, m.Description, ct);
                        }
                    }
                    if (item.Album is { } album)
                        (albums.TryGetValue(album, out var list) ? list : albums[album] = []).Add(assetId);
                    if (item.Metadata?.Favorite == true)
                        favorites.Add(assetId);
                    if (item.Metadata?.Archived == true)
                        archived.Add(assetId);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Takeout import: {item.FileName} failed: {ex.Message}");
                    failed++;
                }
                finally
                {
                    if (extracted is not null)
                        try { File.Delete(extracted); } catch { /* temp */ }
                }
                progress?.Report(new TakeoutProgress("upload", ++done, items.Count, uploaded, alreadyThere, failed));
            }

            progress?.Report(new TakeoutProgress("albums", done, items.Count, uploaded, alreadyThere, failed));
            foreach (var ids in favorites.Distinct().Chunk(500))
                await client.SetFavoriteAsync(ids, true, ct);
            foreach (var ids in archived.Distinct().Chunk(500))
                await client.SetVisibilityAsync(ids, "archive", ct);
            var existingAlbums = albums.Count > 0 ? await client.GetAlbumsAsync(ct) : [];
            foreach (var (name, ids) in albums)
            {
                var target = existingAlbums.FirstOrDefault(a => a.AlbumName == name)?.Id ?? (await client.CreateAlbumAsync(name, null, ct)).Id;
                foreach (var chunk in ids.Distinct().Chunk(500))
                    await client.AddAssetsToAlbumAsync(target, chunk, ct);
            }
        }
        finally
        {
            foreach (var zip in zips.Values)
                zip.Dispose();
        }
        return new TakeoutResult(uploaded, alreadyThere, failed, albums.Count);
    }
}
