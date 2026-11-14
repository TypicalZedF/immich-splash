using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ImmichSplash.Core.Api;

/// <summary>Typed client for the subset of the Immich REST API this app needs.</summary>
public sealed class ImmichClient : IDisposable
{
    // Leave unset fields out entirely: Immich v3 validates strictly and rejects null for optional fields
    // (e.g. "assetIds": null when creating an album fails with "Validation failed").
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    readonly HttpClient _http;

    public ImmichClient(Uri apiUrl, AuthCredential? credential, HttpMessageHandler? handler = null)
    {
        ApiUrl = WithTrailingSlash(apiUrl);
        Credential = credential;
        _http = CreateHttpClient(handler);
        _http.BaseAddress = ApiUrl;

        if (credential is { Kind: AuthKind.AccessToken })
        {
            _http.DefaultRequestHeaders.Authorization = new("Bearer", credential.Secret);
            // Immich redirects some image requests (e.g. full size → original), and .NET drops the
            // Authorization header on redirects. Immich's own header survives them.
            _http.DefaultRequestHeaders.Add("x-immich-user-token", credential.Secret);
        }
        else if (credential is { Kind: AuthKind.ApiKey })
            _http.DefaultRequestHeaders.Add("x-api-key", credential.Secret);
    }

    public Uri ApiUrl { get; }
    public AuthCredential? Credential { get; }

    /// <summary>Filled in on first contact; used to stay compatible with older servers.</summary>
    public ServerVersion? ServerVersion { get; set; }

    /// <summary>Identifies this computer to servers older than v2, which require a device id on upload.</summary>
    public string DeviceId { get; set; } = Environment.MachineName;

    // ---- Discovery -------------------------------------------------------------------------

    /// <summary>
    /// Turns whatever the user typed ("photos.example.com", "http://192.168.1.5:2283", ".../api") into a verified server.
    /// </summary>
    public static async Task<ServerInfo> DiscoverAsync(string input, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        input = input.Trim();
        if (input.Length == 0)
            throw new ServerDiscoveryException(Loc.T("Enter the address of your Immich server."));

        var candidates = input.Contains("://", StringComparison.Ordinal)
            ? new[] { input }
            : new[] { "https://" + input, "http://" + input };

        using var http = CreateHttpClient(handler);
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            {
                last = new ServerDiscoveryException(Loc.T("That doesn't look like a valid server address."));
                continue;
            }

            try
            {
                return await ProbeAsync(http, uri, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last as ServerDiscoveryException ?? new ServerDiscoveryException(NetworkErrors.Describe(last!), last);
    }

    static async Task<ServerInfo> ProbeAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        var web = new UriBuilder(uri.Scheme, uri.Host, uri.Port, path + "/").Uri;
        var api = new Uri(web, "api/");

        // Servers can advertise a custom API location.
        try
        {
            var wellKnown = await GetJsonAsync<WellKnownDto>(http, new Uri(web, ".well-known/immich"), TimeSpan.FromSeconds(10), ct);
            if (wellKnown?.Api?.Endpoint is { Length: > 0 } endpoint)
                api = WithTrailingSlash(new Uri(web, endpoint));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Optional endpoint; fall back to /api.
        }

        PingResponseDto? ping;
        try
        {
            ping = await GetJsonAsync<PingResponseDto>(http, new Uri(api, "server/ping"), TimeSpan.FromSeconds(15), ct);
        }
        catch (Exception ex) when (ex is JsonException or ImmichApiException { StatusCode: HttpStatusCode.NotFound })
        {
            throw new ServerDiscoveryException(Loc.T("This doesn't look like an Immich server."), ex);
        }

        if (!string.Equals(ping?.Res, "pong", StringComparison.OrdinalIgnoreCase))
            throw new ServerDiscoveryException(Loc.T("This doesn't look like an Immich server."));

        var version = await GetJsonAsync<VersionResponseDto>(http, new Uri(api, "server/version"), TimeSpan.FromSeconds(15), ct);
        return new ServerInfo(web, api, new ServerVersion(version!.Major, version.Minor, version.Patch));
    }

    // ---- Auth & account ------------------------------------------------------------------------

    public async Task<string> LoginAsync(string email, string password, CancellationToken ct)
    {
        var response = await SendAsync<LoginResponseDto>(HttpMethod.Post, "auth/login", new LoginRequestDto(email.Trim(), password), ct);
        return response.AccessToken;
    }

    public async Task<LoginOptions> GetLoginOptionsAsync(CancellationToken ct)
    {
        var features = await SendAsync<ServerFeaturesDto>(HttpMethod.Get, "server/features", null, ct);
        string? buttonText = null;
        try
        {
            buttonText = (await SendAsync<ServerConfigDto>(HttpMethod.Get, "server/config", null, ct)).OauthButtonText;
        }
        catch (ImmichApiException)
        {
            // Older servers; the default label is fine.
        }
        return new LoginOptions(features.PasswordLogin, features.Oauth, features.OauthAutoLaunch,
            string.IsNullOrWhiteSpace(buttonText) ? "Sign in with SSO" : buttonText.Trim());
    }

    /// <summary>Starts an OAuth/SSO sign-in and returns the identity provider URL to open in the browser.</summary>
    public async Task<string> StartOAuthAsync(string redirectUri, string codeChallenge, string state, CancellationToken ct)
    {
        var response = await SendAsync<OAuthAuthorizeResponseDto>(HttpMethod.Post, "oauth/authorize",
            new OAuthAuthorizeRequestDto(redirectUri, codeChallenge, state), ct);
        return response.Url;
    }

    /// <summary>Exchanges the browser callback for an access token.</summary>
    public async Task<string> FinishOAuthAsync(string callbackUrl, string state, string codeVerifier, CancellationToken ct)
    {
        var response = await SendAsync<LoginResponseDto>(HttpMethod.Post, "oauth/callback",
            new OAuthCallbackRequestDto(callbackUrl, state, codeVerifier), ct);
        return response.AccessToken;
    }

    public Task LogoutAsync(CancellationToken ct) => SendAsync(HttpMethod.Post, "auth/logout", null, ct);

    public async Task<UserInfo> GetMeAsync(CancellationToken ct)
    {
        var me = await SendAsync<UserResponseDto>(HttpMethod.Get, "users/me", null, ct);
        return new UserInfo(me.Id, me.Name, me.Email, me.QuotaSizeInBytes, me.QuotaUsageInBytes, me.ProfileImagePath, me.AvatarColor);
    }

    /// <summary>The user's profile picture, or null when none is set.</summary>
    public async Task<byte[]?> GetProfileImageAsync(string userId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);
        using var response = await _http.GetAsync($"users/{Uri.EscapeDataString(userId)}/profile-image", cts.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cts.Token);
        return await response.Content.ReadAsByteArrayAsync(cts.Token);
    }

    /// <summary>Sets the profile picture (same as changing it in the Immich web app).</summary>
    public async Task SetProfileImageAsync(byte[] image, string fileName, string contentType, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await _http.PostAsync("users/profile-image", form, cts.Token);
        await EnsureSuccessAsync(response, cts.Token);
    }

    public async Task RemoveProfileImageAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);
        using var response = await _http.DeleteAsync("users/profile-image", cts.Token);
        await EnsureSuccessAsync(response, cts.Token);
    }

    /// <summary>Storage shown in the sidebar, like the Immich web app: personal quota if set, else server disk.</summary>
    public async Task<StorageInfo> GetStorageAsync(CancellationToken ct)
    {
        var me = await GetMeAsync(ct);
        if (me.QuotaSizeInBytes is > 0 && me.QuotaUsageInBytes is { } used)
            return new StorageInfo(used, me.QuotaSizeInBytes.Value);

        var disk = await SendAsync<ServerStorageDto>(HttpMethod.Get, "server/storage", null, ct);
        return new StorageInfo(disk.DiskUseRaw, disk.DiskSizeRaw);
    }

    // ---- Server ------------------------------------------------------------------------------

    public async Task PingAsync(CancellationToken ct)
    {
        var ping = await SendAsync<PingResponseDto>(HttpMethod.Get, "server/ping", null, ct, TimeSpan.FromSeconds(15));
        if (!string.Equals(ping.Res, "pong", StringComparison.OrdinalIgnoreCase))
            throw new ImmichApiException(HttpStatusCode.ServiceUnavailable, Loc.T("The server isn't responding normally."));
    }

    public async Task<ServerVersion> GetVersionAsync(CancellationToken ct)
    {
        var v = await SendAsync<VersionResponseDto>(HttpMethod.Get, "server/version", null, ct);
        return new ServerVersion(v.Major, v.Minor, v.Patch);
    }

    public Task<MediaTypesInfo> GetMediaTypesAsync(CancellationToken ct) =>
        SendAsync<MediaTypesInfo>(HttpMethod.Get, "server/media-types", null, ct);

    // ---- Assets ------------------------------------------------------------------------------

    public async Task<List<BulkCheckResult>> BulkUploadCheckAsync(IReadOnlyList<ChecksumItem> items, CancellationToken ct)
    {
        var response = await SendAsync<BulkCheckResponseDto>(HttpMethod.Post, "assets/bulk-upload-check", new BulkCheckRequestDto(items), ct, TimeSpan.FromSeconds(60));
        return response.Results;
    }

    public async Task<UploadResult> UploadAsync(UploadRequest request, BandwidthLimiter limiter, Action<long>? progress, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        if (ServerVersion is { Major: < 2 })
        {
            // Required by servers older than v2.
            form.Add(new StringContent($"{request.FileName}-{request.Size}"), "deviceAssetId");
            form.Add(new StringContent(DeviceId), "deviceId");
        }
        form.Add(new StringContent(IsoDate(request.CreatedAtUtc)), "fileCreatedAt");
        form.Add(new StringContent(IsoDate(request.ModifiedAtUtc)), "fileModifiedAt");
        form.Add(new StringContent(request.FileName), "filename");
        form.Add(new StringContent("false"), "isFavorite");
        form.Add(new FileUploadContent(request.FilePath, request.Size, limiter, progress), "assetData", request.FileName);

        using var message = new HttpRequestMessage(HttpMethod.Post, "assets") { Content = form };
        message.Headers.Add("x-immich-checksum", request.Checksum);

        // Generous timeout that scales with size, so a stalled connection doesn't hang forever.
        var timeout = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(request.Size / (32 * 1024.0));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await EnsureSuccessAsync(response, cts.Token);
            return (await response.Content.ReadFromJsonAsync<UploadResult>(Json, cts.Token))!;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("The upload timed out."));
        }
    }

    // ---- Library (timeline) ----------------------------------------------------------------

    /// <summary>The user's timeline, newest first, as buckets with counts (no assets yet).</summary>
    internal Task<List<TimeBucketDto>> GetTimeBucketsRawAsync(TimelineFilter filter, CancellationToken ct) =>
        SendAsync<List<TimeBucketDto>>(HttpMethod.Get, "timeline/buckets?" + TimelineQuery(filter), null, ct);

    public Task<List<TimeBucket>> GetTimeBucketsAsync(CancellationToken ct) => GetTimeBucketsAsync(TimelineFilter.All, ct);

    public async Task<List<TimeBucket>> GetTimeBucketsAsync(TimelineFilter filter, CancellationToken ct) =>
        (await GetTimeBucketsRawAsync(filter, ct)).Select(b => new TimeBucket(b.TimeBucket, b.Count)).ToList();

    public Task<List<TimelineAsset>> GetTimeBucketAsync(string bucket, CancellationToken ct) => GetTimeBucketAsync(bucket, TimelineFilter.All, ct);

    /// <summary>Every asset in a timeline (e.g. one album), month by month.</summary>
    public async Task<List<TimelineAsset>> GetAllTimelineAssetsAsync(TimelineFilter filter, CancellationToken ct)
    {
        var all = new List<TimelineAsset>();
        foreach (var bucket in await GetTimeBucketsRawAsync(filter, ct))
            all.AddRange(await GetTimeBucketAsync(bucket.TimeBucket, filter, ct));
        return all;
    }

    /// <summary>The assets in one bucket, newest first.</summary>
    public async Task<List<TimelineAsset>> GetTimeBucketAsync(string bucket, TimelineFilter filter, CancellationToken ct)
    {
        var dto = await SendAsync<TimeBucketAssetsDto>(HttpMethod.Get,
            $"timeline/bucket?{TimelineQuery(filter)}&timeBucket={Uri.EscapeDataString(bucket)}", null, ct, TimeSpan.FromSeconds(60));
        var assets = new List<TimelineAsset>(dto.Id.Count);
        for (var i = 0; i < dto.Id.Count; i++)
        {
            assets.Add(new TimelineAsset(
                dto.Id[i],
                i < dto.IsImage.Count && dto.IsImage[i],
                dto.IsFavorite is { } fav && i < fav.Count && fav[i],
                dto.Ratio is { } ratio && i < ratio.Count && ratio[i] > 0 ? ratio[i] : 1,
                dto.Duration is { } duration && i < duration.Count ? ParseDuration(duration[i]) : null,
                dto.FileCreatedAt is { } created && i < created.Count ? created[i] : null,
                LivePhotoVideoId: dto.LivePhotoVideoId is { } live && i < live.Count ? live[i] : null,
                StackId: dto.Stack is { } stack && i < stack.Count && stack[i] is [var stackId, _] ? stackId : null,
                StackCount: dto.Stack is { } counts && i < counts.Count && counts[i] is [_, var n] && int.TryParse(n, out var count) ? count : 0));
        }
        return assets;
    }

    static string TimelineQuery(TimelineFilter filter)
    {
        // Albums show everything in them, archived photos included, like the web app; the trash shows
        // whatever was trashed.
        var query = filter.AlbumId is { } album ? $"order=desc&albumId={Uri.EscapeDataString(album)}"
            : filter.Trashed ? "isTrashed=true&order=desc"
            : $"visibility={Uri.EscapeDataString(filter.Visibility ?? "timeline")}&order=desc&withStacked=true"; // a stack shows as its main photo
        if (filter.UserId is { } user)
            query += $"&userId={Uri.EscapeDataString(user)}";
        if (filter.PersonId is { } person)
            query += $"&personId={Uri.EscapeDataString(person)}";
        if (filter.TagId is { } tag)
            query += $"&tagId={Uri.EscapeDataString(tag)}";
        if (filter.Favorites)
            query += "&isFavorite=true";
        return query;
    }

    /// <summary>Thumbnail (~250px) or preview (~1440px) image bytes for an asset.</summary>
    /// <param name="edited">False: the picture as it was before any edits. Immich itself defaults to the unedited
    /// picture, so this always says which one it wants.</param>
    public async Task<byte[]> GetAssetImageAsync(string assetId, AssetImageSize size, CancellationToken ct, bool edited = true)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var sizeName = size switch
        {
            AssetImageSize.Preview => "preview",
            AssetImageSize.Fullsize => "fullsize",
            _ => "thumbnail",
        };
        try
        {
            using var response = await _http.GetAsync($"assets/{Uri.EscapeDataString(assetId)}/thumbnail?size={sizeName}&edited={(edited ? "true" : "false")}", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await EnsureSuccessAsync(response, cts.Token);
            return await response.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("The server took too long to send the image."));
        }
    }

    /// <summary>Durations are milliseconds on newer servers and "h:mm:ss.fff" strings on older ones.</summary>
    static long? ParseDuration(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var ms) => ms,
        JsonValueKind.Number => (long)value.GetDouble(),
        JsonValueKind.String when TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var span) => (long)span.TotalMilliseconds,
        _ => null,
    };

    /// <summary>Full details of one asset (for the viewer's info panel).</summary>
    public async Task<AssetDetails> GetAssetAsync(string assetId, CancellationToken ct)
    {
        var a = await SendAsync<AssetDto>(HttpMethod.Get, $"assets/{Uri.EscapeDataString(assetId)}", null, ct);
        var e = a.ExifInfo;
        string? camera = e is null ? null : string.Join(" ", new[] { e.Make, e.Model }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        var exposure = new List<string>();
        if (e?.FNumber is > 0) exposure.Add($"ƒ/{e.FNumber.Value.ToString("0.#", CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(e?.ExposureTime)) exposure.Add($"{e.ExposureTime} s");
        if (e?.FocalLength is > 0) exposure.Add($"{e.FocalLength.Value.ToString("0.#", CultureInfo.InvariantCulture)} mm");
        if (e?.Iso is > 0) exposure.Add($"ISO {e.Iso.Value.ToString("0", CultureInfo.InvariantCulture)}");
        var place = e is null ? null : string.Join(", ", new[] { e.City, e.State, e.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new AssetDetails(a.Id, a.OriginalFileName, a.FileCreatedAt, a.IsFavorite, !string.Equals(a.Type, "VIDEO", StringComparison.OrdinalIgnoreCase),
            e?.FileSizeInByte, e?.ExifImageWidth, e?.ExifImageHeight,
            string.IsNullOrWhiteSpace(camera) ? null : camera, e?.LensModel,
            exposure.Count > 0 ? string.Join("  ·  ", exposure) : null,
            string.IsNullOrWhiteSpace(place) ? null : place, string.IsNullOrWhiteSpace(e?.Description) ? null : e.Description,
            a.People?.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList() ?? [],
            e?.DateTimeOriginal, e?.Latitude, e?.Longitude,
            a.Tags?.Select(t => new TagInfo(t.Id, t.Value ?? t.Name ?? "")).OrderBy(t => t.Value, StringComparer.CurrentCultureIgnoreCase).ToList() ?? []);
    }

    // ---- Editing details: when and where, description ----

    /// <summary>Changes one photo's date, place or description (only what's given).</summary>
    public Task UpdateAssetAsync(string assetId, DateTimeOffset? taken, double? latitude, double? longitude, string? description, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"assets/{Uri.EscapeDataString(assetId)}",
            new UpdateAssetDto(taken?.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture), latitude, longitude, description), ct);

    /// <summary>Gives several photos the same date or place.</summary>
    public Task UpdateAssetsAsync(IReadOnlyList<string> assetIds, DateTimeOffset? taken, double? latitude, double? longitude, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, "assets",
            new UpdateAssetsDto(assetIds, taken?.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture), latitude, longitude), ct);

    /// <summary>Cities matching a name (Immich's own place list).</summary>
    public async Task<List<PlaceResult>> SearchPlacesAsync(string name, CancellationToken ct) =>
        await SendAsync<List<PlaceResult>>(HttpMethod.Get, $"search/places?name={Uri.EscapeDataString(name)}", null, ct) ?? [];

    // ---- Tags ----

    public async Task<List<TagInfo>> GetTagsAsync(CancellationToken ct) =>
        (await SendAsync<List<TagDto>>(HttpMethod.Get, "tags", null, ct) ?? [])
            .Select(t => new TagInfo(t.Id, t.Value ?? t.Name ?? "")).OrderBy(t => t.Value, StringComparer.CurrentCultureIgnoreCase).ToList();

    public async Task<TagInfo> CreateTagAsync(string name, CancellationToken ct)
    {
        var tag = await SendAsync<TagDto>(HttpMethod.Post, "tags", new CreateTagDto(name), ct);
        return new TagInfo(tag.Id, tag.Value ?? tag.Name ?? name);
    }

    public Task TagAssetsAsync(string tagId, IReadOnlyList<string> assetIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"tags/{Uri.EscapeDataString(tagId)}/assets", new BulkIdsDto(assetIds), ct);

    public Task UntagAssetsAsync(string tagId, IReadOnlyList<string> assetIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"tags/{Uri.EscapeDataString(tagId)}/assets", new BulkIdsDto(assetIds), ct);

    /// <summary>Marks assets as favorites (or not).</summary>
    public Task SetFavoriteAsync(IReadOnlyList<string> assetIds, bool favorite, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, "assets", new FavoriteRequestDto(assetIds, favorite), ct);

    /// <summary>Moves assets to the trash (they can be restored in Immich for a while).</summary>
    public Task TrashAssetsAsync(IReadOnlyList<string> assetIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, "assets", new DeleteAssetsRequestDto(assetIds, Force: false), ct);

    /// <summary>
    /// Saves a video in a format any player can play (the server's encoded version, which is the
    /// original when no re-encoding was needed). Reports progress from 0 to 1 when the size is known.
    /// </summary>
    public async Task DownloadVideoAsync(string assetId, Stream destination, Action<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"assets/{Uri.EscapeDataString(assetId)}/video/playback", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total is > 0)
                progress?.Invoke(Math.Min(1, done / (double)total.Value));
        }
    }

    /// <summary>
    /// Part of a video's playable version, for streaming it into the player: bytes from
    /// <paramref name="offset"/>, and the video's total size. Servers that ignore the range send it all.
    /// </summary>
    /// <summary>
    /// Where a video player can stream an asset from, with the sign-in headers it needs ("name: value").
    /// The headers carry the session token or API key, so they're never logged.
    /// </summary>
    public (Uri Url, IReadOnlyList<string> Headers) VideoPlaybackRequest(string assetId)
    {
        var url = new Uri(ApiUrl, $"assets/{Uri.EscapeDataString(assetId)}/video/playback");
        IReadOnlyList<string> headers = Credential switch
        {
            { Kind: AuthKind.AccessToken } c => [$"x-immich-user-token: {c.Secret}"],
            { Kind: AuthKind.ApiKey } c => [$"x-api-key: {c.Secret}"],
            _ => [],
        };
        return (url, headers);
    }

    /// <summary>Saves the full-quality file of an asset to <paramref name="destination"/>, with its edits (crop, turn) if it has any.</summary>
    public async Task DownloadOriginalAsync(string assetId, Stream destination, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"assets/{Uri.EscapeDataString(assetId)}/original?edited=true", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await source.CopyToAsync(destination, ct);
    }

    // ---- Search & explore ------------------------------------------------------------------

    /// <summary>Smart search ("beach at sunset"), best matches first. Needs machine learning on the server.</summary>
    public async Task<List<TimelineAsset>> SmartSearchAsync(string query, int size, CancellationToken ct)
    {
        var response = await SendAsync<SearchResponseDto>(HttpMethod.Post, "search/smart", new SmartSearchRequestDto(query, 1, size), ct, TimeSpan.FromSeconds(60));
        return response.Assets.Items.Where(a => !a.IsHidden).Select(ToTimelineAsset).ToList();
    }

    /// <summary>
    /// The memories Immich has for a day ("on this day" in earlier years, birthdays), most recent year
    /// first, like the strip at the top of the web app's Photos page. Empty on servers without memories.
    /// </summary>
    public async Task<List<Memory>> GetMemoriesAsync(DateOnly day, CancellationToken ct)
    {
        List<MemoryDto> memories;
        try
        {
            memories = await SendAsync<List<MemoryDto>>(HttpMethod.Get, $"memories?for={day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}", null, ct);
        }
        catch (ImmichApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return [];
        }
        return memories
            .Where(m => m.Assets.Count > 0)
            .Select(m => new Memory(m.Id, m.Type, m.Data?.Year, m.Data?.PersonName, m.Assets.Select(ToTimelineAsset).ToList()))
            .OrderByDescending(m => m.Year ?? int.MaxValue)
            .ToList();
    }

    /// <summary>Photos by file name or city, newest first, up to <paramref name="max"/>.</summary>
    public async Task<List<TimelineAsset>> SearchAsync(string? fileName, string? city, int max, CancellationToken ct)
    {
        var results = new List<TimelineAsset>();
        for (var page = 1; results.Count < max; page++)
        {
            var response = await SendAsync<SearchResponseDto>(HttpMethod.Post, "search/metadata",
                new MetadataSearchRequestDto(city, fileName, page, Math.Min(250, max)), ct, TimeSpan.FromSeconds(60));
            // Search also finds live photos' hidden video parts (no thumbnails); the timeline never shows those.
            results.AddRange(response.Assets.Items.Where(a => !a.IsHidden).Select(ToTimelineAsset));
            if (string.IsNullOrEmpty(response.Assets.NextPage) || response.Assets.Items.Count == 0)
                break;
        }
        return results;
    }

    public async Task<List<PersonInfo>> GetPeopleAsync(CancellationToken ct)
    {
        var people = new List<PersonInfo>();
        for (var page = 1; page <= 20; page++)
        {
            var response = await SendAsync<PeopleResponseDto>(HttpMethod.Get, $"people?withHidden=false&page={page}&size=500", null, ct);
            people.AddRange((response.People ?? []).Where(p => !p.IsHidden).Select(p => new PersonInfo(p.Id, p.Name ?? "", p.IsHidden)));
            if (response.HasNextPage != true)
                break;
        }
        return people;
    }

    /// <summary>Gives a person a name.</summary>
    public Task UpdatePersonAsync(string personId, string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"people/{Uri.EscapeDataString(personId)}", new UpdatePersonRequestDto(name), ct);

    /// <summary>
    /// Discards a person: Immich forgets them. Their photos stay; the faces aren't matched to a new person
    /// again unless the server's face detection is forced to run over everything.
    /// </summary>
    public Task DeletePersonAsync(string personId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"people/{Uri.EscapeDataString(personId)}", null, ct);

    /// <summary>
    /// Merges people into <paramref name="targetId"/> (the same person found more than once). Can't be undone.
    /// Immich 3.2.1 and newer take an ordered list (the first one's name wins); older servers the target in the path.
    /// </summary>
    public async Task MergePeopleAsync(string targetId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        try
        {
            await SendAsync(HttpMethod.Post, "people/merge", new MergePeopleRequestDto([targetId, .. ids]), ct);
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            await SendAsync(HttpMethod.Post, $"people/{Uri.EscapeDataString(targetId)}/merge", new MergePeopleRequestDto(ids), ct);
        }
    }

    public async Task<byte[]> GetPersonThumbnailAsync(string personId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.GetAsync($"people/{Uri.EscapeDataString(personId)}/thumbnail", cts.Token);
        await EnsureSuccessAsync(response, cts.Token);
        return await response.Content.ReadAsByteArrayAsync(cts.Token);
    }

    /// <summary>Cities with photos, like the web app's Explore → Places.</summary>
    public async Task<List<PlaceInfo>> GetPlacesAsync(CancellationToken ct)
    {
        var assets = await SendAsync<List<AssetDto>>(HttpMethod.Get, "search/cities", null, ct, TimeSpan.FromSeconds(60));
        return assets.Where(a => !string.IsNullOrWhiteSpace(a.ExifInfo?.City))
            .Select(a => new PlaceInfo(a.ExifInfo!.City!, a.ExifInfo.State, a.ExifInfo.Country, a.Id))
            .OrderBy(p => p.City, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Photos with a location, for the map, like the web app's map settings. Only options that are
    /// switched on are sent, so older servers that don't know some of them still work.
    /// </summary>
    public async Task<List<MapMarker>> GetMapMarkersAsync(MapFilter? filter, CancellationToken ct)
    {
        var markers = await SendAsync<List<MapMarkerDto>>(HttpMethod.Get, "map/markers" + MapQuery(filter ?? MapFilter.Everything), null, ct, TimeSpan.FromSeconds(90));
        return markers.Select(m => new MapMarker(m.Id, m.Lat, m.Lon, m.City, m.Country)).ToList();
    }

    internal static string MapQuery(MapFilter filter)
    {
        var parts = new List<string>();
        if (filter.OnlyFavorites)
            parts.Add("isFavorite=true");
        if (filter.IncludeArchived)
            parts.Add("isArchived=true");
        if (filter.WithPartners)
            parts.Add("withPartners=true");
        if (filter.WithSharedAlbums)
            parts.Add("withSharedAlbums=true");
        if (filter.After is { } after)
            parts.Add("fileCreatedAfter=" + Uri.EscapeDataString(IsoDate(after.UtcDateTime)));
        if (filter.Before is { } before)
            parts.Add("fileCreatedBefore=" + Uri.EscapeDataString(IsoDate(before.UtcDateTime)));
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    static TimelineAsset ToTimelineAsset(AssetDto a)
    {
        var ratio = a.ExifInfo is { ExifImageWidth: > 0, ExifImageHeight: > 0 } e ? e.ExifImageWidth!.Value / (double)e.ExifImageHeight!.Value : 1;
        long? duration = ParseDuration(a.Duration) is > 0 and var ms ? ms : null;
        return new TimelineAsset(a.Id, !string.Equals(a.Type, "VIDEO", StringComparison.OrdinalIgnoreCase), a.IsFavorite, ratio, duration, a.FileCreatedAt,
            FileSize: a.ExifInfo?.FileSizeInByte, LivePhotoVideoId: a.LivePhotoVideoId,
            StackId: a.Stack?.Id, StackCount: a.Stack?.AssetCount ?? 0);
    }

    // ---- Stacks ------------------------------------------------------------------------------

    /// <summary>The photos in a stack, its main photo first.</summary>
    public async Task<List<TimelineAsset>> GetStackAsync(string stackId, CancellationToken ct)
    {
        var stack = await SendAsync<StackDto>(HttpMethod.Get, $"stacks/{Uri.EscapeDataString(stackId)}", null, ct);
        return stack.Assets.Select(ToTimelineAsset).Select(a => a with { StackId = null, StackCount = 0 })
            .OrderBy(a => a.Id == stack.PrimaryAssetId ? 0 : 1).ToList();
    }

    /// <summary>Stacks photos together; the first one is shown in the timeline.</summary>
    public Task CreateStackAsync(IReadOnlyList<string> assetIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "stacks", new CreateStackRequestDto(assetIds), ct);

    // ---- Utilities ---------------------------------------------------------------------------

    /// <summary>Groups of photos the server found to be duplicates (needs machine learning on the server).</summary>
    public async Task<List<DuplicateGroup>> GetDuplicatesAsync(CancellationToken ct)
    {
        var groups = await SendAsync<List<DuplicateGroupDto>>(HttpMethod.Get, "duplicates", null, ct, TimeSpan.FromSeconds(120));
        return groups
            .Where(g => g.Assets.Count > 1)
            .Select(g => new DuplicateGroup(g.DuplicateId, g.Assets.Select(a => new DuplicateAsset(
                ToTimelineAsset(a), a.OriginalFileName ?? "", a.ExifInfo?.ExifImageWidth ?? 0, a.ExifInfo?.ExifImageHeight ?? 0,
                a.ExifInfo?.FileSizeInByte ?? 0)).ToList()))
            .ToList();
    }

    /// <summary>
    /// Settles a group of duplicates: <paramref name="trash"/> go to the trash and the rest stop being
    /// flagged (also how "these aren't duplicates" works, with nothing to trash).
    /// </summary>
    public async Task ResolveDuplicatesAsync(IReadOnlyList<string> keep, IReadOnlyList<string> trash, CancellationToken ct)
    {
        if (trash.Count > 0)
            await SendAsync(HttpMethod.Delete, "assets", new DeleteAssetsRequestDto(trash, Force: false), ct);
        if (keep.Count > 0)
            await SendAsync(HttpMethod.Put, "assets", new ClearDuplicateDto(keep, null), ct);
    }

    // Common names for screenshots and screen recordings (Android, Windows, macOS, iPhone, other languages).
    static readonly string[] ScreenshotNames = ["Screenshot", "Screen Shot", "Captura de pantalla", "Captura de tela", "Bildschirmfoto", "Capture d", "Schermafbeelding", "Schermata", "Skärmbild", "Zrzut ekranu", "スクリーンショット"];
    static readonly string[] RecordingNames = ["Screen Recording", "Screen_Recording", "ScreenRecording", "screen-recording", "Screencast", "RPReplay", "record_screen", "Grabación de pantalla", "Bildschirmaufnahme"];
    static readonly string[] CaptureFolders = ["Screenshots", "Screen recordings", "ScreenRecorder", "Screenrecorder"];

    /// <summary>
    /// Screenshots and screen recordings still in the timeline (Utilities → Archive screenshots):
    /// by their usual file names and folders, plus iPhone screenshots (PNG files with no camera).
    /// </summary>
    public async Task<List<ScreenCapture>> FindScreenCapturesAsync(CancellationToken ct)
    {
        var found = new Dictionary<string, ScreenCapture>(StringComparer.Ordinal);
        async Task Collect(Func<int, object> request, Func<AssetDto, CaptureKind> kind, int max = 5000)
        {
            for (var page = 1; found.Count < 20000; page++)
            {
                var response = await SendAsync<SearchResponseDto>(HttpMethod.Post, "search/metadata", request(page), ct, TimeSpan.FromSeconds(60));
                foreach (var a in response.Assets.Items)
                    found.TryAdd(a.Id, new ScreenCapture(ToTimelineAsset(a), kind(a)));
                if (string.IsNullOrEmpty(response.Assets.NextPage) || response.Assets.Items.Count == 0 || page * 250 >= max)
                    break;
            }
        }
        static CaptureKind ByType(AssetDto a) =>
            string.Equals(a.Type, "VIDEO", StringComparison.OrdinalIgnoreCase) ? CaptureKind.ScreenRecording : CaptureKind.Screenshot;
        foreach (var name in RecordingNames)
            await Collect(page => new CaptureSearchRequestDto(name, null, null, "timeline", page, 250), _ => CaptureKind.ScreenRecording);
        foreach (var name in ScreenshotNames)
            await Collect(page => new CaptureSearchRequestDto(name, null, null, "timeline", page, 250), ByType);
        foreach (var folder in CaptureFolders)
            await Collect(page => new CaptureSearchRequestDto(null, folder, null, "timeline", page, 250), ByType);
        // iPhone screenshots are IMG_1234.PNG with no camera details.
        try
        {
            await Collect(page => new NoCameraSearchRequestDto(".png", "IMAGE", "timeline", page, 250), _ => CaptureKind.LikelyScreenshot);
        }
        catch (ImmichApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            Log.Info($"Search for PNGs without a camera isn't supported here: {ex.Message}");
        }
        return [.. found.Values.OrderByDescending(c => c.Asset.FileCreatedAt, StringComparer.Ordinal)];
    }

    /// <summary>Your biggest photos and videos, largest first (Immich 1.124 and newer).</summary>
    public async Task<List<TimelineAsset>> GetLargeFilesAsync(long minBytes, int max, CancellationToken ct)
    {
        var assets = await SendAsync<List<AssetDto>>(HttpMethod.Post,
            FormattableString.Invariant($"search/large-assets?minFileSize={minBytes}&size={max}"), null, ct, TimeSpan.FromSeconds(90));
        return assets.Select(ToTimelineAsset).OrderByDescending(a => a.FileSize ?? 0).ToList();
    }

    // ---- Archive, locked folder, trash -------------------------------------------------------

    // ---- Editing (Immich 2.5 and newer): crop, turn and flip, applied by the server to the original ----

    /// <summary>
    /// The original photo's size, the right way up (what crops are measured on). Like the Immich web editor this
    /// comes from the photo's EXIF data: the asset's own width and height change once it's edited.
    /// </summary>
    public async Task<AssetSize> GetAssetSizeAsync(string assetId, CancellationToken ct)
    {
        var asset = await SendAsync<AssetSizeDto>(HttpMethod.Get, $"assets/{Uri.EscapeDataString(assetId)}", null, ct);
        if (asset.ExifInfo is { ExifImageWidth: > 0 and var w, ExifImageHeight: > 0 and var h } exif)
        {
            // Orientations 5 to 8 are a quarter turn: the stored picture is on its side.
            var orientation = exif.Orientation is { } o ? o.ToString() : "";
            var sideways = orientation is "5" or "6" or "7" or "8";
            return new AssetSize(sideways ? h : w, sideways ? w : h, asset.IsEdited == true);
        }
        return new AssetSize(asset.Width ?? 0, asset.Height ?? 0, asset.IsEdited == true);
    }

    /// <summary>Replaces the photo's edits with these (always described from the original photo).</summary>
    public Task ApplyEditsAsync(string assetId, IReadOnlyList<ImageEdit> edits, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"assets/{Uri.EscapeDataString(assetId)}/edits",
            new AssetEditsRequestDto(edits.Select(e => new AssetEditItemDto(e.Action, e switch
            {
                CropEdit c => new { x = c.X, y = c.Y, width = c.Width, height = c.Height },
                RotateEdit r => new { angle = r.Angle },
                MirrorEdit m => (object)new { axis = m.Axis },
                _ => throw new ArgumentException("Unknown edit"),
            })).ToList()), ct);

    /// <summary>Puts the photo back as it was: removes all its edits.</summary>
    public Task RemoveEditsAsync(string assetId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"assets/{Uri.EscapeDataString(assetId)}/edits", null, ct);

    /// <summary>Photos that look like this one, most similar first (needs machine learning on the server).</summary>
    public async Task<List<TimelineAsset>> SimilarSearchAsync(string assetId, int size, CancellationToken ct)
    {
        var response = await SendAsync<SearchResponseDto>(HttpMethod.Post, "search/smart", new SimilarSearchRequestDto(assetId, 1, size), ct, TimeSpan.FromSeconds(60));
        return response.Assets.Items.Select(ToTimelineAsset).Where(a => a.Id != assetId).ToList();
    }

    /// <summary>Asks the server to redo something for photos: "refresh-faces", "refresh-metadata" or "regenerate-thumbnail".</summary>
    public Task RunAssetJobAsync(IReadOnlyList<string> assetIds, string job, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "assets/jobs", new AssetJobRequestDto(assetIds, job), ct);

    /// <summary>Moves assets to the timeline ("timeline"), the archive ("archive") or the locked folder ("locked").</summary>
    public Task SetVisibilityAsync(IReadOnlyList<string> assetIds, string visibility, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, "assets", new VisibilityRequestDto(assetIds, visibility), ct);

    public Task RestoreFromTrashAsync(IReadOnlyList<string> assetIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "trash/restore/assets", new IdsRequestDto(assetIds), ct);

    public Task RestoreAllFromTrashAsync(CancellationToken ct) => SendAsync(HttpMethod.Post, "trash/restore", null, ct);

    public Task EmptyTrashAsync(CancellationToken ct) => SendAsync(HttpMethod.Post, "trash/empty", null, ct);

    /// <summary>Deletes assets for good (skipping the trash).</summary>
    public Task DeletePermanentlyAsync(IReadOnlyList<string> assetIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, "assets", new DeleteAssetsRequestDto(assetIds, Force: true), ct);

    public async Task<AuthStatus> GetAuthStatusAsync(CancellationToken ct)
    {
        var status = await SendAsync<AuthStatusDto>(HttpMethod.Get, "auth/status", null, ct);
        return new AuthStatus(status.PinCode, status.IsElevated);
    }

    /// <summary>Unlocks the locked folder for this session with the account's PIN.</summary>
    public Task UnlockSessionAsync(string pin, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "auth/session/unlock", new SessionUnlockDto(pin), ct);

    public Task LockSessionAsync(CancellationToken ct) => SendAsync(HttpMethod.Post, "auth/session/lock", null, ct);

    // The locked folder's PIN (6 digits). SSO accounts have no password, so changing or removing it
    // always takes the current PIN here; a forgotten one can only be reset by a server admin.

    /// <summary>Creates the PIN (only while the account has none).</summary>
    public Task SetupPinAsync(string pin, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "auth/pin-code", new PinCodeSetupDto(pin), ct);

    public Task ChangePinAsync(string currentPin, string newPin, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, "auth/pin-code", new PinCodeChangeDto(currentPin, newPin), ct);

    /// <summary>Removes the PIN (the locked folder then can't be opened until a new one is made).</summary>
    public Task RemovePinAsync(string currentPin, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, "auth/pin-code", new PinCodeResetDto(currentPin), ct);

    // ---- Sharing -----------------------------------------------------------------------------

    /// <summary>Links you've shared, newest first.</summary>
    public async Task<List<SharedLinkSummary>> GetSharedLinksAsync(CancellationToken ct)
    {
        var links = await SendAsync<List<SharedLinkFullDto>>(HttpMethod.Get, "shared-links", null, ct);
        return links.Select(l =>
        {
            var isAlbum = string.Equals(l.Type, "ALBUM", StringComparison.OrdinalIgnoreCase);
            var count = isAlbum ? l.Album?.AssetCount ?? 0 : l.Assets?.Count ?? 0;
            var title = !string.IsNullOrWhiteSpace(l.Description) ? l.Description!
                : isAlbum ? l.Album?.AlbumName ?? ""
                : "";
            var cover = isAlbum ? l.Album?.AlbumThumbnailAssetId : l.Assets?.FirstOrDefault()?.Id;
            return new SharedLinkSummary(l.Id, l.Key, isAlbum, title, cover, count, l.CreatedAt, l.ExpiresAt, l.AllowDownload, isAlbum ? l.Album?.Id : null,
                isAlbum ? null : l.Assets?.Select(a => a.Id).ToList());
        }).OrderByDescending(l => l.CreatedAt).ToList();
    }

    /// <summary>Changes what a link allows (whether people can download).</summary>
    public Task UpdateSharedLinkAsync(string id, bool? allowDownload, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"shared-links/{Uri.EscapeDataString(id)}", new UpdateSharedLinkRequestDto(allowDownload, null), ct);

    // ---- Who an album is shared with ----

    public async Task<AlbumSharing> GetAlbumSharingAsync(string albumId, CancellationToken ct)
    {
        var album = await SendAsync<AlbumDetailDto>(HttpMethod.Get, $"albums/{Uri.EscapeDataString(albumId)}?withoutAssets=true", null, ct);
        static AlbumMember? Member(UserBriefDto? user, string role) =>
            user is null ? null : new AlbumMember(user.Id, user.Name ?? user.Email ?? "", user.Email ?? "", role);
        var members = (album.AlbumUsers ?? [])
            .Select(u => Member(u.User, string.Equals(u.Role, "editor", StringComparison.OrdinalIgnoreCase) ? "editor" : "viewer"))
            .OfType<AlbumMember>().ToList();
        return new AlbumSharing(album.Id, album.AlbumName, Member(album.Owner, "owner"), members, album.HasSharedLink == true);
    }

    /// <summary>Everyone on the server you could share with.</summary>
    public async Task<List<ServerUser>> GetUsersAsync(CancellationToken ct) =>
        (await SendAsync<List<UserBriefDto>>(HttpMethod.Get, "users", null, ct))
            .Select(u => new ServerUser(u.Id, u.Name ?? u.Email ?? "", u.Email ?? "")).ToList();

    public Task AddAlbumUsersAsync(string albumId, IReadOnlyList<string> userIds, string role, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"albums/{Uri.EscapeDataString(albumId)}/users",
            new AddAlbumUsersDto(userIds.Select(id => new AlbumUserRoleDto(id, role)).ToList()), ct);

    public Task SetAlbumUserRoleAsync(string albumId, string userId, string role, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"albums/{Uri.EscapeDataString(albumId)}/user/{Uri.EscapeDataString(userId)}", new RoleDto(role), ct);

    public Task RemoveAlbumUserAsync(string albumId, string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"albums/{Uri.EscapeDataString(albumId)}/user/{Uri.EscapeDataString(userId)}", null, ct);

    public Task DeleteSharedLinkAsync(string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"shared-links/{Uri.EscapeDataString(id)}", null, ct);

    /// <summary>People who share their library with you.</summary>
    public async Task<List<PartnerInfo>> GetPartnersAsync(CancellationToken ct)
    {
        var partners = await SendAsync<List<PartnerDto>>(HttpMethod.Get, "partners?direction=shared-with", null, ct);
        return partners.Select(p => new PartnerInfo(p.Id, p.Name ?? p.Email ?? "", p.Email ?? "")).ToList();
    }


    /// <summary>Creates a public link to some photos, or to an album.</summary>
    public Task<SharedLinkInfo> CreateSharedLinkAsync(IReadOnlyList<string>? assetIds, string? albumId, bool allowDownload, DateTimeOffset? expiresAt, CancellationToken ct) =>
        SendAsync<SharedLinkInfo>(HttpMethod.Post, "shared-links",
            new SharedLinkRequestDto(albumId is null ? "INDIVIDUAL" : "ALBUM", albumId is null ? assetIds : null, albumId,
                allowDownload, ShowMetadata: true, expiresAt is { } e ? IsoDate(e.UtcDateTime) : null), ct);

    /// <summary>The public address for shared links: the server's "external domain" if set, else <paramref name="webUrl"/>.</summary>
    public async Task<string> GetShareBaseUrlAsync(string webUrl, CancellationToken ct)
    {
        try
        {
            var config = await SendAsync<ServerExternalDomainDto>(HttpMethod.Get, "server/config", null, ct);
            if (!string.IsNullOrWhiteSpace(config.ExternalDomain))
                return config.ExternalDomain.TrimEnd('/');
        }
        catch (ImmichApiException)
        {
            // Older servers: the address we know is fine.
        }
        return webUrl.TrimEnd('/');
    }

    // ---- Albums ------------------------------------------------------------------------------

    public Task<List<AlbumInfo>> GetAlbumsAsync(CancellationToken ct) =>
        SendAsync<List<AlbumInfo>>(HttpMethod.Get, "albums", null, ct);

    /// <summary>Albums shared with you or by you.</summary>
    public Task<List<AlbumInfo>> GetSharedAlbumsAsync(CancellationToken ct) =>
        SendAsync<List<AlbumInfo>>(HttpMethod.Get, "albums?shared=true", null, ct);

    public Task<AlbumInfo> CreateAlbumAsync(string name, IReadOnlyList<string>? assetIds, CancellationToken ct) =>
        SendAsync<AlbumInfo>(HttpMethod.Post, "albums", new CreateAlbumRequestDto(name, assetIds), ct);

    /// <summary>Deletes an album. Its photos stay in the library.</summary>
    public Task DeleteAlbumAsync(string albumId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"albums/{Uri.EscapeDataString(albumId)}", null, ct);

    /// <summary>Renames an album and/or changes its description.</summary>
    public Task<AlbumInfo> UpdateAlbumAsync(string albumId, string name, string? description, CancellationToken ct) =>
        SendAsync<AlbumInfo>(HttpMethod.Patch, $"albums/{Uri.EscapeDataString(albumId)}", new UpdateAlbumRequestDto(name, description ?? ""), ct);

    public Task AddAssetsToAlbumAsync(string albumId, IReadOnlyList<string> assetIds, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"albums/{Uri.EscapeDataString(albumId)}/assets", new BulkIdsRequestDto(assetIds), ct);

    // ---- Plumbing ----------------------------------------------------------------------------

    internal static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        // Immich names a sign-in's device from these when it doesn't recognise the user agent (like its mobile apps
        // send them): "Immich Splash" on "Windows" in Account Settings → Authorized devices, instead of "Unknown".
        http.DefaultRequestHeaders.Add("deviceModel", AppInfo.Name);
        http.DefaultRequestHeaders.Add("deviceType", AppInfo.OsName);
        return http;
    }

    async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            using var request = CreateRequest(method, path, body);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await EnsureSuccessAsync(response, cts.Token);
            return (await response.Content.ReadFromJsonAsync<T>(Json, cts.Token))!;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("The server took too long to respond."));
        }
    }

    async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);
        try
        {
            using var request = CreateRequest(method, path, body);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await EnsureSuccessAsync(response, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("The server took too long to respond."));
        }
    }

    static HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return request;
    }

    static async Task<T?> GetJsonAsync<T>(HttpClient http, Uri url, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await ThrowIfFailedAsync(response, cts.Token);
            return await response.Content.ReadFromJsonAsync<T>(Json, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("The server took too long to respond."));
        }
    }

    /// <summary>
    /// Raised (on any thread) when the server says this client's sign-in is no longer valid, whatever was being
    /// done at the time: browsing the library notices it as well as a backup run.
    /// </summary>
    public event Action? SignedOut;

    async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await ThrowIfFailedAsync(response, ct);
        }
        catch (ImmichApiException ex) when (ex.IsSignedOut && Credential is not null)
        {
            SignedOut?.Invoke();
            throw;
        }
    }

    static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            detail = ParseErrorMessage(body);
            // Keep the server's full explanation (e.g. which field failed validation) for troubleshooting.
            if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.Unauthorized && body.Length > 0)
                Log.Warn($"{(int)response.StatusCode} from {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: {(body.Length > 600 ? body[..600] : body)}");
        }
        catch
        {
            // Body is optional.
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => detail ?? Loc.T("Your session has expired. Please sign in again."),
            HttpStatusCode.Forbidden => detail ?? Loc.T("You don't have permission to do that."),
            HttpStatusCode.NotFound => detail ?? Loc.T("The server doesn't support this request."),
            HttpStatusCode.RequestEntityTooLarge => Loc.T("The file is too large for the server (check your reverse proxy upload limit)."),
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => Loc.T("The server is temporarily unavailable."),
            _ => detail ?? Loc.T("The server returned an error ({0}).", (int)response.StatusCode),
        };
        throw new ImmichApiException(response.StatusCode, message) { ServerMessage = detail };
    }

    internal static string? ParseErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("message", out var message))
                return null;

            var text = message.ValueKind switch
            {
                JsonValueKind.String => message.GetString(),
                JsonValueKind.Array => string.Join("; ", message.EnumerateArray().Select(m => m.ToString())),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(text))
                return null;
            return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string IsoDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    static Uri WithTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    public void Dispose() => _http.Dispose();
}
