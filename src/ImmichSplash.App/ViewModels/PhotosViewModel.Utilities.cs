using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

/// <summary>Utilities, like the Immich web app's: review duplicates, find large files, and what on this PC isn't backed up yet.</summary>
public sealed partial class PhotosViewModel
{
    /// <summary>Files from this size up count as large (the web app's default).</summary>
    const long LargeFileBytes = 10L * 1024 * 1024;
    const int LargeFilesShown = 250;

    public bool ShowUtilities => Drill is null && Duplicates is null && Privacy is null && Page == LibraryPage.Utilities;

    /// <summary>The duplicates review, while it's open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUtilities), nameof(ShowDuplicates), nameof(IsDrilledIn), nameof(Heading))]
    public partial DuplicatesViewModel? Duplicates { get; set; }

    public bool ShowDuplicates => Duplicates is not null;

    /// <summary>The privacy check (suggestions for the locked folder), while it's open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUtilities), nameof(ShowPrivacy), nameof(IsDrilledIn), nameof(Heading))]
    public partial PrivacyReviewViewModel? Privacy { get; set; }

    public bool ShowPrivacy => Privacy is not null;

    [ObservableProperty]
    public partial string PrivacyText { get; set; } = "";

    void UpdatePrivacyText(Core.Privacy.PrivacyScanStatus status) => PrivacyText = status.State switch
    {
        Core.Privacy.PrivacyScanState.Off => Tr.T("Off · finds private photos on this PC; nothing is sent anywhere"),
        _ when status.Suggestions > 0 => Format.Plural(status.Suggestions, Tr.T("photo to review"), Tr.T("photos to review")) + " · " + PrivacyReviewViewModel.Describe(status),
        _ => PrivacyReviewViewModel.Describe(status),
    };

    void OnPrivacyStatus(Core.Privacy.PrivacyScanStatus status) => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdatePrivacyText(status));

    [RelayCommand]
    void OpenPrivacy()
    {
        Privacy?.Dispose();
        Privacy = new PrivacyReviewViewModel(this);
    }

    [ObservableProperty]
    public partial string DuplicatesText { get; set; } = "";

    [ObservableProperty]
    public partial string NotBackedUpText { get; set; } = "";

    async Task LoadUtilitiesAsync()
    {
        IsLoading = false;
        if (!_privacyHooked)
        {
            _privacyHooked = true;
            AppServices.PrivacyScan.StatusChanged += OnPrivacyStatus;
        }
        UpdatePrivacyText(AppServices.PrivacyScan.Status with { Suggestions = AppServices.PrivacyScan.Suggestions });
        DuplicatesText = Tr.T("Checking…");
        try
        {
            NotBackedUpText = (await LocalOnlyAssetsAsync(_lifetime.Token)).Count is var n and > 0
                ? Format.Plural(n, Tr.T("file on this PC isn't in Immich yet"), Tr.T("files on this PC aren't in Immich yet"))
                : Tr.T("Everything in your backup folders is in Immich.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not count files waiting for backup", ex);
        }
        if (Client is not { } client)
            return;
        try
        {
            OnDuplicatesChanged((await client.GetDuplicatesAsync(_lifetime.Token)).Count);
        }
        catch (ImmichApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            DuplicatesText = Tr.T("Needs a newer Immich server.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DuplicatesText = Tr.T("Couldn't check. {0}", NetworkErrors.Describe(ex));
        }
    }

    bool _privacyHooked;

    internal void OnDuplicatesChanged(int groups) =>
        DuplicatesText = groups > 0
            ? Format.Plural(groups, Tr.T("group of look-alike photos to review"), Tr.T("groups of look-alike photos to review"))
            : Tr.T("No duplicates found.");

    [RelayCommand]
    void OpenTakeoutImport() => Panel = new TakeoutImportViewModel(this, _main);

    [RelayCommand]
    void OpenDuplicates()
    {
        Duplicates?.Dispose();
        Duplicates = new DuplicatesViewModel(this);
    }

    [RelayCommand]
    void OpenLargeFiles() => OpenDrill(new GridQuery(Tr.T("Large files"), async (client, ct) =>
    {
        var files = await client.GetLargeFilesAsync(LargeFileBytes, LargeFilesShown, ct);
        return BucketState.FromAssets(files, Tr.T("Largest first"));
    }, ShowSizes: true));

    [RelayCommand]
    void OpenNotBackedUp() => OpenDrill(new GridQuery(Tr.T("Not backed up yet"), async (_, ct) => BucketState.FromAssets(await LocalOnlyAssetsAsync(ct))));

    /// <summary>Screenshots and screen recordings still in the timeline, grouped by kind, to archive.</summary>
    [RelayCommand]
    void OpenScreenCaptures() => OpenDrill(new GridQuery(Tr.T("Screenshots and screen recordings"), async (client, ct) =>
    {
        var found = await client.FindScreenCapturesAsync(ct);
        var groups = new List<BucketState>();
        foreach (var (kind, label) in new[]
        {
            (CaptureKind.Screenshot, Tr.T("Screenshots")),
            (CaptureKind.ScreenRecording, Tr.T("Screen recordings")),
            (CaptureKind.LikelyScreenshot, Tr.T("Probably screenshots (pictures with no camera details)")),
        })
        {
            var assets = found.Where(f => f.Kind == kind).Select(f => f.Asset).ToList();
            if (assets.Count > 0)
                groups.Add(new BucketState(label, assets.Count, groups.Count, () => Task.FromResult(assets)));
        }
        return groups;
    }, OffersArchiveAll: true));

    /// <summary>Archives everything in the open list (they stay in Immich and in albums, just off the timeline).</summary>
    [RelayCommand]
    async Task ArchiveAllAsync()
    {
        if (Client is not { } client || _main is null || _buckets.Count == 0)
            return;
        var ids = new List<string>();
        foreach (var bucket in _buckets)
            ids.AddRange((await bucket.GetAssetsAsync(CancellationToken.None)).Select(a => a.Id));
        var what = Format.Plural(ids.Count, "photo or video", "photos and videos");
        if (!await _main.ConfirmAsync(Tr.T("Archive {0}?", what),
                Tr.T("They leave your timeline but stay in Immich, in their albums and in search. You can find them under Archive and unarchive them anytime."),
                Tr.T("Archive"), danger: false))
            return;
        try
        {
            foreach (var chunk in ids.Chunk(500))
                await client.SetVisibilityAsync(chunk, "archive", CancellationToken.None);
            ShowNotice(Tr.T("Archived {0}", what));
            _ = LoadGridAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't archive. {0}", NetworkErrors.Describe(ex)));
        }
    }

    /// <summary>Opens one photo in the viewer (e.g. from the duplicates review), without editing actions.</summary>
    internal void OpenAsset(TimelineAsset asset) => OpenAssets([asset], 0);

    /// <summary>Opens one of a few photos (a duplicate group, the privacy suggestions); back and next step through just those.</summary>
    internal void OpenAssets(IReadOnlyList<TimelineAsset> assets, int index)
    {
        if (assets.Count == 0)
            return;
        index = Math.Clamp(index, 0, assets.Count - 1);
        Viewer = new PhotoViewerViewModel(this, BucketState.FromAssets(assets, "")[0], index, assets[index], null, readOnly: true);
    }

    internal Task<bool> ConfirmAsync(string title, string message, string action, bool danger) =>
        _main?.ConfirmAsync(title, message, action, danger: danger) ?? Task.FromResult(false);
}
