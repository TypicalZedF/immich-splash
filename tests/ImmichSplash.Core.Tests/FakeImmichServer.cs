using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImmichSplash.Core.Tests;

static class TestSetup
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("IMMICH_SPLASH_DATA_DIR", Path.Combine(Path.GetTempPath(), "immich-splash-tests", "appdata"));
}

/// <summary>In-memory stand-in for the Immich API endpoints the app uses.</summary>
sealed class FakeImmichServer : HttpMessageHandler
{
    public ConcurrentDictionary<string, string> AssetsByChecksum { get; } = new();
    public ConcurrentQueue<string> UploadedFileNames { get; } = new();
    public ConcurrentDictionary<string, (string Name, HashSet<string> Assets)> Albums { get; } = new();

    /// <summary>Stacks made, each as its asset ids (the main photo first).</summary>
    public ConcurrentQueue<string[]> Stacks { get; } = new();

    /// <summary>Jobs asked for with POST /assets/jobs, and the kinds of smart search made.</summary>
    public ConcurrentQueue<string> AssetJobs { get; } = new();
    public ConcurrentQueue<string> AssetEdits { get; } = new();

    /// <summary>The EXIF orientation GET /assets/{id} reports ("6": stored on its side).</summary>
    public string ExifOrientation { get; set; } = "1";
    public ConcurrentQueue<string> SharedLinkPatches { get; } = new();
    public ConcurrentDictionary<string, List<(string UserId, string Role)>> AlbumMembers { get; } = new();
    public ConcurrentQueue<string> PeopleUpdates { get; } = new();

    /// <summary>Merges asked for: "path body". With <see cref="LegacyMergeOnly"/> the new route answers 404 (servers before 3.2.1).</summary>
    public ConcurrentQueue<string> PeopleMerges { get; } = new();
    public bool LegacyMergeOnly { get; set; }

    /// <summary>Picture requests (path and query), e.g. "/api/assets/a1/thumbnail?size=preview&amp;edited=true".</summary>
    public ConcurrentQueue<string> MediaRequests { get; } = new();

    /// <summary>PUT /assets/{id} bodies (date, place, description changes).</summary>
    public ConcurrentQueue<string> AssetUpdates { get; } = new();

    /// <summary>The account's locked-folder PIN (null: none yet), and whether this session is unlocked.</summary>
    public string? Pin { get; set; }
    public bool Elevated { get; set; }
    public ConcurrentQueue<string> SmartSearches { get; } = new();
    public bool RejectAuth { get; set; }

    /// <summary>Signed-in sessions by token (null: any token works). POST /auth/logout ends the caller's.</summary>
    public ConcurrentDictionary<string, bool>? Sessions { get; set; }

    /// <summary>Order of uploads and album additions, to check albums fill during a long upload.</summary>
    public ConcurrentQueue<string> Events { get; } = new();

    /// <summary>Called after each upload with the running count (lets a test change settings mid-run).</summary>
    public Action<int>? AfterUpload { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;

        if (RejectAuth && path != "/api/server/ping")
            return Json(HttpStatusCode.Unauthorized, new { message = "Invalid user token" });
        if (Sessions is not null && path != "/api/server/ping" &&
            request.Headers.TryGetValues("x-immich-user-token", out var tokens) && !Sessions.ContainsKey(tokens.First()))
            return Json(HttpStatusCode.Unauthorized, new { message = "Invalid user token" });
        if (Sessions is not null && (method, path) == ("POST", "/api/auth/logout"))
        {
            Sessions.TryRemove(request.Headers.GetValues("x-immich-user-token").First(), out _);
            return Json(HttpStatusCode.OK, new { successful = true });
        }

        switch (method, path)
        {
            case ("GET", "/api/server/ping"):
                return Json(HttpStatusCode.OK, new { res = "pong" });
            case ("GET", "/api/server/version"):
                return Json(HttpStatusCode.OK, new { major = 3, minor = 3, patch = 0 });
            case ("GET", "/api/server/media-types"):
                return Json(HttpStatusCode.OK, new { image = new[] { "image/jpeg" }, video = new[] { "video/mp4" }, sidecar = Array.Empty<string>() });
            case ("POST", "/api/assets/bulk-upload-check"):
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var results = doc.RootElement.GetProperty("assets").EnumerateArray().Select(a =>
                {
                    var id = a.GetProperty("id").GetString()!;
                    var checksum = a.GetProperty("checksum").GetString()!;
                    return AssetsByChecksum.TryGetValue(checksum, out var existing)
                        ? (object)new { id, action = "reject", reason = "duplicate", assetId = existing }
                        : new { id, action = "accept" };
                }).ToList();
                return Json(HttpStatusCode.OK, new { results });
            }
            case ("POST", "/api/assets"):
            {
                var form = (MultipartFormDataContent)request.Content!;
                string? fileName = null;
                byte[]? data = null;
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (name == "filename") fileName = await part.ReadAsStringAsync(ct);
                    if (name == "assetData") data = await part.ReadAsByteArrayAsync(ct);
                }
                var checksum = Convert.ToHexStringLower(SHA1.HashData(data!));
                Assert.Equal(checksum, request.Headers.GetValues("x-immich-checksum").Single());

                if (AssetsByChecksum.TryGetValue(checksum, out var existing))
                    return Json(HttpStatusCode.OK, new { id = existing, status = "duplicate" });

                var id = Guid.NewGuid().ToString();
                AssetsByChecksum[checksum] = id;
                UploadedFileNames.Enqueue(fileName!);
                Events.Enqueue("upload");
                AfterUpload?.Invoke(UploadedFileNames.Count);
                return Json(HttpStatusCode.Created, new { id, status = "created" });
            }
            case ("GET", _) when path.StartsWith("/api/assets/", StringComparison.Ordinal) && (path.EndsWith("/thumbnail", StringComparison.Ordinal) || path.EndsWith("/original", StringComparison.Ordinal)):
                MediaRequests.Enqueue(path + request.RequestUri!.Query);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]) };
            case ("GET", "/api/users/me"):
                return Json(HttpStatusCode.OK, new { id = "user-1", name = "Test User", email = "test@example.com", quotaSizeInBytes = (long?)null, quotaUsageInBytes = 0L });
            case ("GET", "/api/auth/status"):
                return Json(HttpStatusCode.OK, new { pinCode = Pin is not null, password = true, isElevated = Elevated });
            case (_, "/api/auth/pin-code"):
            case ("POST", "/api/auth/session/unlock"):
            {
                // Like Immich (v3): PINs are exactly 6 digits; the current one must match to change, remove or unlock.
                using var doc = request.Content is null ? JsonDocument.Parse("{}") : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                string? Field(string name) => doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
                static bool Valid(string? pin) => pin is { Length: 6 } && pin.All(char.IsAsciiDigit);
                var current = Field("pinCode");
                var bad = Json(HttpStatusCode.BadRequest, new { message = "Wrong PIN code", statusCode = 400 });
                switch (request.Method.Method)
                {
                    case "POST" when path == "/api/auth/session/unlock":
                        if (Pin is null || current != Pin)
                            return bad;
                        Elevated = true;
                        return Json(HttpStatusCode.OK, new { });
                    case "POST":
                        if (Pin is not null || !Valid(current))
                            return Json(HttpStatusCode.BadRequest, new { message = "User already has a PIN code", statusCode = 400 });
                        Pin = current;
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case "PUT":
                        var next = Field("newPinCode");
                        if (Pin is null || current != Pin || !Valid(next))
                            return bad;
                        Pin = next;
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case "DELETE":
                        if (Pin is null || current != Pin)
                            return bad;
                        Pin = null;
                        Elevated = false;
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                return Json(HttpStatusCode.MethodNotAllowed, new { });
            }
            case ("POST", "/api/people/merge"):
            case ("POST", _) when path.StartsWith("/api/people/", StringComparison.Ordinal) && path.EndsWith("/merge", StringComparison.Ordinal):
            {
                if (LegacyMergeOnly && path == "/api/people/merge")
                    return Json(HttpStatusCode.NotFound, new { message = "Cannot POST /api/people/merge", statusCode = 404 });
                // Like Immich (zod): "ids", a list of uuids.
                var body = await request.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0
                    || ids.EnumerateArray().Any(i => !Guid.TryParse(i.GetString(), out _)))
                    return Json(HttpStatusCode.BadRequest, new { message = "Validation failed", statusCode = 400 });
                PeopleMerges.Enqueue(path + " " + body);
                return Json(HttpStatusCode.OK, ids.EnumerateArray().Select(i => new { id = i.GetString(), success = true }).ToList());
            }
            case ("DELETE", _) when path.StartsWith("/api/people/", StringComparison.Ordinal):
                PeopleUpdates.Enqueue("DELETE " + path);
                return Json(HttpStatusCode.NoContent, new { });
            case ("PUT", _) when path.StartsWith("/api/people/", StringComparison.Ordinal):
            {
                // Like Immich v3 (zod): optional fields may be left out but not null.
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (doc.RootElement.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Null))
                    return Json(HttpStatusCode.BadRequest, new { message = "Validation failed", statusCode = 400 });
                PeopleUpdates.Enqueue(path + " " + doc.RootElement.GetRawText());
                return Json(HttpStatusCode.OK, new { id = path.Split('/')[^1] });
            }
            case ("GET", _) when path.StartsWith("/api/assets/", StringComparison.Ordinal) && path.Count(c => c == '/') == 3:
                return Json(HttpStatusCode.OK, new { id = path.Split('/')[^1], width = 4000, height = 3000, isEdited = false, exifInfo = new { exifImageWidth = 4000, exifImageHeight = 3000, orientation = ExifOrientation } });
            case ("PUT", _) when path.StartsWith("/api/assets/", StringComparison.Ordinal) && path.Count(c => c == '/') == 3:
            {
                // Changing a photo's date, place or description. Like Immich v3 (zod): no nulls, and a place needs both halves.
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (root.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Null)
                    || root.TryGetProperty("latitude", out _) != root.TryGetProperty("longitude", out _))
                    return Json(HttpStatusCode.BadRequest, new { message = "Validation failed", statusCode = 400 });
                AssetUpdates.Enqueue(path + " " + root.GetRawText());
                return Json(HttpStatusCode.OK, new { id = path.Split('/')[^1] });
            }
            case ("PUT", _) when path.StartsWith("/api/assets/", StringComparison.Ordinal) && path.EndsWith("/edits", StringComparison.Ordinal):
            {
                // Like Immich v3 (zod): at least one edit, a known action and the right parameters for it.
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var edits = doc.RootElement.GetProperty("edits");
                var ok = edits.GetArrayLength() > 0;
                foreach (var edit in edits.EnumerateArray())
                {
                    var parameters = edit.GetProperty("parameters");
                    ok &= edit.GetProperty("action").GetString() switch
                    {
                        "crop" => parameters.TryGetProperty("x", out _) && parameters.TryGetProperty("y", out _)
                                  && parameters.TryGetProperty("width", out var cw) && cw.GetInt32() >= 1 && parameters.TryGetProperty("height", out var ch) && ch.GetInt32() >= 1,
                        "rotate" => parameters.TryGetProperty("angle", out var angle) && angle.GetInt32() is 0 or 90 or 180 or 270,
                        "mirror" => parameters.TryGetProperty("axis", out var axis) && axis.GetString() is "horizontal" or "vertical",
                        _ => false,
                    };
                }
                if (!ok)
                    return Json(HttpStatusCode.BadRequest, new { message = "Validation failed", statusCode = 400 });
                AssetEdits.Enqueue(doc.RootElement.GetRawText());
                return Json(HttpStatusCode.OK, new { assetId = path.Split('/')[3], edits = Array.Empty<object>() });
            }
            case ("DELETE", _) when path.StartsWith("/api/assets/", StringComparison.Ordinal) && path.EndsWith("/edits", StringComparison.Ordinal):
                AssetEdits.Enqueue("removed");
                return Json(HttpStatusCode.NoContent, new { });
            case ("GET", "/api/users"):
                return Json(HttpStatusCode.OK, new[] { new { id = "u1", name = "Alex", email = "alex@x.test" }, new { id = "u2", name = "Sam", email = "sam@x.test" }, new { id = "u3", name = "Riley", email = "riley@x.test" } });
            case ("GET", _) when path.StartsWith("/api/albums/", StringComparison.Ordinal) && path.Count(c => c == '/') == 3:
            {
                var albumId = path.Split('/')[^1];
                var users = AlbumMembers.GetValueOrDefault(albumId) ?? [];
                return Json(HttpStatusCode.OK, new
                {
                    id = albumId, albumName = "Trip",
                    owner = new { id = "u1", name = "Alex", email = "alex@x.test" },
                    albumUsers = users.Select(u => new { role = u.Role, user = new { id = u.UserId, name = u.UserId, email = u.UserId + "@x.test" } }),
                    hasSharedLink = false,
                });
            }
            case ("PUT", _) when path.StartsWith("/api/albums/", StringComparison.Ordinal) && path.EndsWith("/users", StringComparison.Ordinal):
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var albumId = path.Split('/')[3];
                var list = AlbumMembers.GetOrAdd(albumId, _ => []);
                foreach (var entry in doc.RootElement.GetProperty("albumUsers").EnumerateArray())
                {
                    var role = entry.GetProperty("role").GetString();
                    if (role is not ("editor" or "viewer"))
                        return Json(HttpStatusCode.BadRequest, new { message = "Validation failed", statusCode = 400 });
                    list.Add((entry.GetProperty("userId").GetString()!, role!));
                }
                return Json(HttpStatusCode.OK, new { id = albumId });
            }
            case ("PUT", _) when path.Contains("/user/", StringComparison.Ordinal) && path.StartsWith("/api/albums/", StringComparison.Ordinal):
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var parts = path.Split('/');
                var list = AlbumMembers.GetOrAdd(parts[3], _ => []);
                var i = list.FindIndex(u => u.UserId == parts[5]);
                if (i >= 0)
                    list[i] = (parts[5], doc.RootElement.GetProperty("role").GetString()!);
                return Json(HttpStatusCode.OK, new { });
            }
            case ("DELETE", _) when path.Contains("/user/", StringComparison.Ordinal) && path.StartsWith("/api/albums/", StringComparison.Ordinal):
            {
                var parts = path.Split('/');
                AlbumMembers.GetOrAdd(parts[3], _ => []).RemoveAll(u => u.UserId == parts[5]);
                return Json(HttpStatusCode.NoContent, new { });
            }
            case ("PATCH", _) when path.StartsWith("/api/shared-links/", StringComparison.Ordinal):
            {
                SharedLinkPatches.Enqueue(path + " " + await request.Content!.ReadAsStringAsync(ct));
                return Json(HttpStatusCode.OK, new { });
            }
            case ("POST", "/api/assets/jobs"):
            {
                // Like Immich v3 (zod): a list of ids and one of the known job names.
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var name = doc.RootElement.GetProperty("name").GetString();
                if (name is not ("refresh-faces" or "refresh-metadata" or "regenerate-thumbnail" or "transcode-video")
                    || doc.RootElement.GetProperty("assetIds").GetArrayLength() == 0)
                    return Json(HttpStatusCode.BadRequest, new { message = "Validation failed", statusCode = 400 });
                AssetJobs.Enqueue(name!);
                return Json(HttpStatusCode.NoContent, new { });
            }
            case ("POST", "/api/search/smart"):
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                SmartSearches.Enqueue(doc.RootElement.TryGetProperty("queryAssetId", out var id) ? "similar:" + id.GetString() : "text");
                return Json(HttpStatusCode.OK, new { assets = new { items = Array.Empty<object>(), nextPage = (string?)null } });
            }
            case ("GET", "/api/albums"):
                return Json(HttpStatusCode.OK, Albums.Select(a => new { id = a.Key, albumName = a.Value.Name }).ToList());
            case ("POST", "/api/albums"):
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                // Like Immich v3 (zod): optional fields may be omitted, but not null.
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Null && property.Name != "description")
                        return Json(HttpStatusCode.BadRequest, new { message = "Validation failed", statusCode = 400 });
                }
                var id = Guid.NewGuid().ToString();
                var name = doc.RootElement.GetProperty("albumName").GetString()!;
                Albums[id] = (name, []);
                return Json(HttpStatusCode.Created, new { id, albumName = name });
            }
            case ("PUT", _) when path.StartsWith("/api/albums/", StringComparison.Ordinal) && path.EndsWith("/assets", StringComparison.Ordinal):
            {
                var albumId = path.Split('/')[3];
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                foreach (var id in doc.RootElement.GetProperty("ids").EnumerateArray())
                    Albums[albumId].Assets.Add(id.GetString()!);
                Events.Enqueue("album");
                return Json(HttpStatusCode.OK, Array.Empty<object>());
            }
            case ("POST", "/api/stacks"):
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var ids = doc.RootElement.GetProperty("assetIds").EnumerateArray().Select(e => e.GetString()!).ToArray();
                if (ids.Length < 2 || ids.Any(id => !AssetsByChecksum.Values.Contains(id)))
                    return Json(HttpStatusCode.BadRequest, new { message = "Validation failed", statusCode = 400 });
                Stacks.Enqueue(ids);
                return Json(HttpStatusCode.Created, new { id = Guid.NewGuid().ToString(), primaryAssetId = ids[0], assets = Array.Empty<object>() });
            }
            default:
                return Json(HttpStatusCode.NotFound, new { message = $"No route for {method} {path}" });
        }
    }

    public void Preload(string filePath) =>
        AssetsByChecksum[Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(filePath)))] = Guid.NewGuid().ToString();

    static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
