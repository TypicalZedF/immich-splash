namespace ImmichSplash.Core.Api;

public enum AuthKind
{
    AccessToken,
    ApiKey,
}

public sealed record AuthCredential(AuthKind Kind, string Secret);

public sealed record ServerVersion(int Major, int Minor, int Patch)
{
    public override string ToString() => $"v{Major}.{Minor}.{Patch}";
}

/// <summary>A reachable Immich server: the web address users know and the API base the app talks to.</summary>
public sealed record ServerInfo(Uri WebUrl, Uri ApiUrl, ServerVersion Version)
{
    public string Host => WebUrl.IsDefaultPort ? WebUrl.Host : $"{WebUrl.Host}:{WebUrl.Port}";
}

public sealed record UserInfo(string Id, string Name, string Email, long? QuotaSizeInBytes = null, long? QuotaUsageInBytes = null,
    string? ProfileImagePath = null, string? AvatarColor = null)
{
    public bool HasProfileImage => !string.IsNullOrEmpty(ProfileImagePath);
}

/// <summary>How much space is used: the user's quota when one is set, otherwise the server's disk.</summary>
public sealed record StorageInfo(long Used, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Used / (double)Total, 0, 1);
}

public sealed record ChecksumItem(string Id, string Checksum);

public sealed record BulkCheckResult(string Id, string Action, string? Reason, string? AssetId)
{
    public bool IsAccepted => Action == "accept";
    public bool IsDuplicate => Action == "reject" && Reason == "duplicate";
}

public sealed record UploadResult(string Id, string Status)
{
    public bool IsDuplicate => Status == "duplicate";
}

public sealed record AlbumInfo(string Id, string AlbumName, string? AlbumThumbnailAssetId = null, int AssetCount = 0, bool Shared = false,
    string? OwnerId = null, List<System.Text.Json.JsonElement>? AlbumUsers = null, System.Text.Json.JsonElement? Owner = null,
    DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null, DateTimeOffset? StartDate = null, DateTimeOffset? EndDate = null,
    string? Description = null, bool HasSharedLink = false)
{
    /// <summary>
    /// Shared with other people: anyone besides you is in it (or it's someone else's album). The
    /// server's own "shared" also counts albums that only have a public link.
    /// </summary>
    /// <summary>
    /// Whose album it is: ownerId on older servers, the first member on newer ones (null if unknown).
    /// </summary>
    public string? OwnerUserId =>
        OwnerId
        ?? (Owner is { ValueKind: System.Text.Json.JsonValueKind.Object } o && o.TryGetProperty("id", out var id) ? id.GetString() : null)
        ?? (AlbumUsers is [{ ValueKind: System.Text.Json.JsonValueKind.Object } first, ..]
            && first.TryGetProperty("user", out var user) && user.ValueKind == System.Text.Json.JsonValueKind.Object
            && user.TryGetProperty("id", out var uid) ? uid.GetString() : null);

    /// <summary>Only the owner can delete an album (when unknown, let the server decide).</summary>
    public bool IsOwnedBy(string? myUserId) => OwnerUserId is not { } owner || myUserId is null || owner == myUserId;

    public bool IsSharedWithPeople(string? myUserId)
    {
        // Everyone in the album: the owner and its members (newer servers list the owner first among
        // the members and no longer send ownerId).
        var people = new HashSet<string>(StringComparer.Ordinal);
        if (OwnerId is not null)
            people.Add(OwnerId);
        if (Owner is { ValueKind: System.Text.Json.JsonValueKind.Object } owner && owner.TryGetProperty("id", out var ownerId) && ownerId.GetString() is { } o)
            people.Add(o);
        foreach (var member in AlbumUsers ?? [])
        {
            if (member.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;
            var id = member.TryGetProperty("user", out var user) && user.ValueKind == System.Text.Json.JsonValueKind.Object && user.TryGetProperty("id", out var uid)
                ? uid.GetString()
                : member.TryGetProperty("userId", out var direct) ? direct.GetString() : null;
            if (id is not null)
                people.Add(id);
        }
        return myUserId is null ? people.Count > 1 : people.Any(p => p != myUserId);
    }
}

/// <summary>
/// Narrows the timeline: one album, person or partner, favorites, the archive ("archive"), the locked
/// folder ("locked"), or the trash. Empty = the whole library.
/// </summary>
public sealed record TimelineFilter(string? AlbumId = null, string? PersonId = null, bool Favorites = false,
    string? Visibility = null, bool Trashed = false, string? UserId = null, string? TagId = null)
{
    public static TimelineFilter All { get; } = new();
}

/// <summary>A public link you created (to photos or an album).</summary>
public sealed record SharedLinkSummary(string Id, string Key, bool IsAlbum, string Title, string? CoverAssetId, int AssetCount,
    DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt, bool AllowDownload, string? AlbumId = null, IReadOnlyList<string>? AssetIds = null)
{
    public bool IsExpired => ExpiresAt is { } e && e < DateTimeOffset.Now;
}

/// <summary>Someone who shares their library with you (Immich partner sharing).</summary>
public sealed record PartnerInfo(string Id, string Name, string Email);

/// <summary>Whether a PIN is set up, and whether this session is currently unlocked with it (for the locked folder).</summary>
public sealed record AuthStatus(bool HasPin, bool IsElevated);

/// <summary>A recognized face, as on the web app's People page.</summary>
public sealed record PersonInfo(string Id, string Name, bool IsHidden);

/// <summary>A city with photos (and one of them, for a cover).</summary>
public sealed record PlaceInfo(string City, string? State, string? Country, string CoverAssetId);

/// <summary>Where a photo was taken.</summary>
public sealed record MapMarker(string Id, double Lat, double Lon, string? City, string? Country);

/// <summary>Details shown in the photo viewer's info panel.</summary>
public sealed record AssetDetails(
    string Id,
    string? OriginalFileName,
    string? FileCreatedAt,
    bool IsFavorite,
    bool IsImage,
    long? FileSize,
    int? Width,
    int? Height,
    string? Camera,
    string? Lens,
    string? Exposure,
    string? Place,
    string? Description,
    IReadOnlyList<string> People,
    string? DateTimeOriginal = null,
    double? Latitude = null,
    double? Longitude = null,
    IReadOnlyList<TagInfo>? Tags = null);

/// <summary>An Immich tag. <see cref="Value"/> is the full name, with "/" between levels (e.g. "Trips/Japan").</summary>
public sealed record TagInfo(string Id, string Value);

internal sealed record TagDto(string Id, string? Name, string? Value);
internal sealed record CreateTagDto(string Name);
internal sealed record BulkIdsDto(IReadOnlyList<string> Ids);

/// <summary>A place found by name (Immich's own list of cities), to set where a photo was taken.</summary>
public sealed record PlaceResult(string Name, string? Admin1Name, string? CountryName, double Latitude, double Longitude)
{
    public string Label => string.Join(", ", new[] { Name, Admin1Name, CountryName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Changes to a photo's details; only what's set is changed.</summary>
internal sealed record UpdateAssetDto(string? DateTimeOriginal = null, double? Latitude = null, double? Longitude = null, string? Description = null);
internal sealed record UpdateAssetsDto(IReadOnlyList<string> Ids, string? DateTimeOriginal = null, double? Latitude = null, double? Longitude = null);

/// <summary>A public link to photos or an album.</summary>
public sealed record SharedLinkInfo(string Id, string Key);

public sealed record MediaTypesInfo(string[]? Image, string[]? Video);

/// <summary>Everything needed to upload one file.</summary>
public sealed record UploadRequest(
    string FilePath,
    string FileName,
    long Size,
    string Checksum,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc);

/// <summary>Which sign-in methods the server offers.</summary>
public sealed record LoginOptions(bool PasswordLogin, bool OAuth, bool OAuthAutoLaunch, string OAuthButtonText);

/// <summary>One month (or other period) of the library timeline.</summary>
public sealed record TimeBucket(string Key, int Count);

/// <summary>A photo or video in the timeline.</summary>
/// <param name="LocalPath">For a file on this PC that isn't in Immich yet: where it is.</param>
/// <param name="LocalFailed">That file couldn't be uploaded (it will be retried).</param>
/// <param name="FileSize">Size of the original, when the server said (search results, large files).</param>
/// <param name="LivePhotoVideoId">For a live photo: the short video that goes with it.</param>
/// <param name="StackId">The stack this photo leads (RAW+JPEG, bursts…), shown as one photo in the timeline.</param>
/// <param name="StackCount">How many photos are in that stack.</param>
public sealed record TimelineAsset(string Id, bool IsImage, bool IsFavorite, double Ratio, long? DurationMs, string? FileCreatedAt,
    string? LocalPath = null, bool LocalFailed = false, long? FileSize = null, string? LivePhotoVideoId = null,
    string? StackId = null, int StackCount = 0)
{
    public bool IsLocalOnly => LocalPath is not null;
    public bool IsLivePhoto => LivePhotoVideoId is not null;
    public bool IsStack => StackId is not null && StackCount > 1;
}

/// <summary>Which photos the map shows (Map settings).</summary>
public sealed record MapFilter(bool OnlyFavorites = false, bool IncludeArchived = false, bool WithPartners = false, bool WithSharedAlbums = false,
    DateTimeOffset? After = null, DateTimeOffset? Before = null)
{
    public static MapFilter Everything { get; } = new();
}

/// <summary>One photo in a group of duplicates, with what's needed to pick the one to keep.</summary>
public sealed record DuplicateAsset(TimelineAsset Asset, string FileName, int Width, int Height, long FileSize);

/// <summary>Photos the server thinks are the same picture (Utilities → Review duplicates).</summary>
public sealed record DuplicateGroup(string Id, IReadOnlyList<DuplicateAsset> Assets);


/// <summary>Image sizes the server can render for an asset.</summary>
public enum AssetImageSize
{
    Thumbnail,
    Preview,

    /// <summary>Full resolution in a format any app can show (the original for JPEGs). Newer servers only.</summary>
    Fullsize,
}

// Wire DTOs
internal sealed record LoginRequestDto(string Email, string Password);
internal sealed record LoginResponseDto(string AccessToken, string UserId, string UserEmail, string Name);
internal sealed record PingResponseDto(string? Res);
internal sealed record VersionResponseDto(int Major, int Minor, int Patch);
internal sealed record UserResponseDto(string Id, string Name, string Email, long? QuotaSizeInBytes, long? QuotaUsageInBytes,
    string? ProfileImagePath, string? AvatarColor);
internal sealed record ServerStorageDto(long DiskSizeRaw, long DiskUseRaw);
internal sealed record BulkCheckRequestDto(IReadOnlyList<ChecksumItem> Assets);
internal sealed record BulkCheckResponseDto(List<BulkCheckResult> Results);
internal sealed record UpdateAlbumRequestDto(string AlbumName, string Description);
internal sealed record CreateAlbumRequestDto(string AlbumName, IReadOnlyList<string>? AssetIds);
internal sealed record BulkIdsRequestDto(IReadOnlyList<string> Ids);
internal sealed record ServerFeaturesDto(bool PasswordLogin, bool Oauth, bool OauthAutoLaunch);
internal sealed record ServerConfigDto(string? OauthButtonText);
internal sealed record OAuthAuthorizeRequestDto(string RedirectUri, string CodeChallenge, string State);
internal sealed record OAuthAuthorizeResponseDto(string Url);
internal sealed record OAuthCallbackRequestDto(string Url, string State, string CodeVerifier);
internal sealed record TimeBucketDto(string TimeBucket, int Count);
internal sealed record TimeBucketAssetsDto(
    List<string> Id,
    List<bool> IsImage,
    List<bool>? IsFavorite,
    List<double>? Ratio,
    List<System.Text.Json.JsonElement>? Duration,
    List<string>? FileCreatedAt,
    List<string?>? LivePhotoVideoId = null,
    List<List<string>?>? Stack = null);
internal sealed record WellKnownDto(WellKnownApiDto? Api);
internal sealed record PeopleResponseDto(List<PersonDto>? People, bool? HasNextPage);
internal sealed record UpdatePersonRequestDto(string Name);
internal sealed record MergePeopleRequestDto(IReadOnlyList<string> Ids);
internal sealed record AssetEditItemDto(string Action, object Parameters);
internal sealed record AssetEditsRequestDto(List<AssetEditItemDto> Edits);
internal sealed record AssetSizeDto(int? Width, int? Height, bool? IsEdited, AssetSizeExifDto? ExifInfo);
internal sealed record AssetSizeExifDto(int? ExifImageWidth, int? ExifImageHeight, System.Text.Json.JsonElement? Orientation);

/// <summary>How big a photo is (as Immich sees it) and whether it already has edits.</summary>
public sealed record AssetSize(int Width, int Height, bool IsEdited);
internal sealed record PersonDto(string Id, string? Name, bool IsHidden);
internal sealed record ExifDto(
    string? Make, string? Model, string? LensModel, double? FNumber, string? ExposureTime, double? Iso, double? FocalLength,
    string? City, string? State, string? Country, int? ExifImageWidth, int? ExifImageHeight, long? FileSizeInByte, string? Description,
    string? DateTimeOriginal = null, double? Latitude = null, double? Longitude = null);
internal sealed record AssetPersonDto(string? Name);
/// <summary>A memory Immich made for a day: photos from that day in an earlier year, or someone's birthday.</summary>
public sealed record Memory(string Id, string Type, int? Year, string? PersonName, IReadOnlyList<TimelineAsset> Assets)
{
    public bool IsBirthday => Type == "birthday";
}

internal sealed record MemoryDataDto(int? Year, string? PersonName);
internal sealed record MemoryDto(string Id, string Type, MemoryDataDto? Data, List<AssetDto> Assets);

internal sealed record AssetDto(
    string Id, string? Type, string? OriginalFileName, string? FileCreatedAt, bool IsFavorite, System.Text.Json.JsonElement Duration,
    ExifDto? ExifInfo, List<AssetPersonDto>? People, string? LivePhotoVideoId = null, AssetStackDto? Stack = null, string? Visibility = null,
    List<TagDto>? Tags = null)
{
    /// <summary>A live photo's video part: Immich hides it and makes no thumbnail for it.</summary>
    public bool IsHidden => Visibility == "hidden";
}
internal sealed record AssetStackDto(string Id, string? PrimaryAssetId, int AssetCount);
internal sealed record StackDto(string Id, string? PrimaryAssetId, List<AssetDto> Assets);
internal sealed record CreateStackRequestDto(IReadOnlyList<string> AssetIds);
internal sealed record SearchAssetsDto(List<AssetDto> Items, string? NextPage);
internal sealed record SearchResponseDto(SearchAssetsDto Assets);
internal sealed record SmartSearchRequestDto(string Query, int Page, int Size);
internal sealed record SimilarSearchRequestDto(string QueryAssetId, int Page, int Size);
internal sealed record AssetJobRequestDto(IReadOnlyList<string> AssetIds, string Name);
/// <summary>A screen capture search: the file name or folder contains some text.</summary>
internal sealed record CaptureSearchRequestDto(string? OriginalFileName, string? OriginalPath, string? Type, string Visibility, int Page, int Size);

/// <summary>Files of a type with no camera make ("make": null is how the server is asked for that).</summary>
internal sealed record NoCameraSearchRequestDto(string? OriginalFileName, string Type, string Visibility, int Page, int Size)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public string? Make => null;
}

/// <summary>What kind of screen capture a photo looks like.</summary>
public enum CaptureKind { Screenshot, ScreenRecording, LikelyScreenshot }

public sealed record ScreenCapture(TimelineAsset Asset, CaptureKind Kind);

internal sealed record MetadataSearchRequestDto(string? City, string? OriginalFileName, int Page, int Size, string? Order = "desc");
internal sealed record MapMarkerDto(string Id, double Lat, double Lon, string? City, string? Country);
internal sealed record FavoriteRequestDto(IReadOnlyList<string> Ids, bool IsFavorite);
internal sealed record DeleteAssetsRequestDto(IReadOnlyList<string> Ids, bool Force);
internal sealed record SharedLinkRequestDto(string Type, IReadOnlyList<string>? AssetIds, string? AlbumId, bool AllowDownload, bool ShowMetadata, string? ExpiresAt);
internal sealed record SharedLinkDto(string Id, string Key);
internal sealed record ServerExternalDomainDto(string? ExternalDomain);
internal sealed record VisibilityRequestDto(IReadOnlyList<string> Ids, string Visibility);
internal sealed record IdsRequestDto(IReadOnlyList<string> Ids);
internal sealed record SharedLinkAlbumDto(string? Id, string? AlbumName, string? AlbumThumbnailAssetId, int? AssetCount);
internal sealed record SharedLinkAssetDto(string Id);
internal sealed record SharedLinkFullDto(string Id, string Key, string? Type, string? Description, SharedLinkAlbumDto? Album,
    List<SharedLinkAssetDto>? Assets, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt, bool AllowDownload);
/// <summary>Someone an album is shared with, and what they may do ("viewer" or "editor").</summary>
public sealed record AlbumMember(string UserId, string Name, string Email, string Role);

/// <summary>Who an album is shared with.</summary>
public sealed record AlbumSharing(string AlbumId, string AlbumName, AlbumMember? Owner, IReadOnlyList<AlbumMember> Members, bool HasSharedLink);

/// <summary>Another person on the server (to share an album with).</summary>
public sealed record ServerUser(string Id, string Name, string Email);

internal sealed record UserBriefDto(string Id, string? Name, string? Email);
internal sealed record AlbumUserDto(string? Role, UserBriefDto? User);
internal sealed record AlbumDetailDto(string Id, string AlbumName, List<AlbumUserDto>? AlbumUsers, UserBriefDto? Owner, bool? HasSharedLink);
internal sealed record AlbumUserRoleDto(string UserId, string Role);
internal sealed record AddAlbumUsersDto(IReadOnlyList<AlbumUserRoleDto> AlbumUsers);
internal sealed record RoleDto(string Role);
internal sealed record UpdateSharedLinkRequestDto(bool? AllowDownload, string? Description);
internal sealed record PartnerDto(string Id, string? Name, string? Email);
internal sealed record AuthStatusDto(bool PinCode, bool IsElevated);
internal sealed record DuplicateGroupDto(string DuplicateId, List<AssetDto> Assets);
internal sealed record ClearDuplicateDto(IReadOnlyList<string> Ids,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? DuplicateId);
internal sealed record SessionUnlockDto(string PinCode);
internal sealed record PinCodeSetupDto(string PinCode);
internal sealed record PinCodeChangeDto(string PinCode, string NewPinCode);
internal sealed record PinCodeResetDto(string PinCode);
internal sealed record WellKnownApiDto(string? Endpoint);
