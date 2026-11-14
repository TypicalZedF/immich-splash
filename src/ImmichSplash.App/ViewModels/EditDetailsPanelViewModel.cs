using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

/// <summary>
/// "Edit date, place and tags" for the selected photos: give them all the same date, the same place, or a tag.
/// Each part is applied on its own, so you can change just one thing.
/// </summary>
public sealed partial class EditDetailsPanelViewModel(PhotosViewModel owner, IReadOnlyList<string> assetIds) : ViewModelBase
{
    public string What { get; } = Format.Plural(assetIds.Count, Tr.T("photo"), Tr.T("photos"));

    // Date

    [ObservableProperty]
    public partial DateTime? Date { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial TimeSpan? Time { get; set; } = new TimeSpan(12, 0, 0);

    [RelayCommand]
    async Task ApplyDateAsync()
    {
        if (owner.Client is not { } client || Date is not { } day)
            return;
        var local = day.Date + (Time ?? TimeSpan.Zero);
        var taken = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        await RunAsync(() => client.UpdateAssetsAsync(assetIds, taken, null, null, CancellationToken.None),
            Tr.T("Changed the date of {0}", What), Tr.T("Couldn't change the date. {0}"));
    }

    // Place

    [ObservableProperty]
    public partial string PlaceSearch { get; set; } = "";

    public ObservableCollection<PlaceResult> PlaceResults { get; } = [];
    CancellationTokenSource? _search;

    async partial void OnPlaceSearchChanged(string value)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        var text = value.Trim();
        if (text.Length < 2 || owner.Client is not { } client)
        {
            PlaceResults.Clear();
            return;
        }
        try
        {
            await Task.Delay(250, cts.Token);
            var found = PhotoViewerViewModel.TryParseCoordinates(text) is { } p
                ? [new PlaceResult(Tr.T("These coordinates"), null, null, p.Lat, p.Lon)]
                : await client.SearchPlacesAsync(text, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            PlaceResults.Clear();
            foreach (var place in found.Take(6))
                PlaceResults.Add(place);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Place search failed: {ex.Message}");
        }
    }

    [RelayCommand]
    async Task ChoosePlaceAsync(PlaceResult place)
    {
        if (owner.Client is not { } client)
            return;
        await RunAsync(() => client.UpdateAssetsAsync(assetIds, null, place.Latitude, place.Longitude, CancellationToken.None),
            Tr.T("Set the place of {0} to {1}", What, place.Label), Tr.T("Couldn't set the place. {0}"));
    }

    // Tag

    [ObservableProperty]
    public partial string TagSearch { get; set; } = "";

    public ObservableCollection<TagChoice> TagChoices { get; } = [];
    List<TagInfo>? _tags;

    async partial void OnTagSearchChanged(string value)
    {
        TagChoices.Clear();
        var text = value.Trim();
        if (text.Length == 0 || owner.Client is not { } client)
            return;
        try
        {
            _tags ??= await client.GetTagsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Info($"Tags unavailable: {ex.Message}");
            _tags = [];
        }
        if (TagSearch.Trim() != text)
            return;
        foreach (var tag in _tags.Where(t => t.Value.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Take(6))
            TagChoices.Add(new TagChoice(tag, tag.Value));
        if (!_tags.Any(t => string.Equals(t.Value, text, StringComparison.CurrentCultureIgnoreCase)))
            TagChoices.Add(new TagChoice(null, Tr.T("Create tag “{0}”", text)));
    }

    [RelayCommand]
    Task AddTypedTagAsync() => TagChoices.FirstOrDefault() is { } first ? AddTagAsync(first) : Task.CompletedTask;

    [RelayCommand]
    async Task AddTagAsync(TagChoice choice)
    {
        if (owner.Client is not { } client)
            return;
        var name = TagSearch.Trim();
        await RunAsync(async () =>
        {
            var tag = choice.Tag ?? await client.CreateTagAsync(name, CancellationToken.None);
            await client.TagAssetsAsync(tag.Id, assetIds, CancellationToken.None);
        }, Tr.T("Tagged {0} with “{1}”", What, choice.Tag?.Value ?? name), Tr.T("Couldn't add the tag. {0}"));
    }

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    async Task RunAsync(Func<Task> work, string done, string failed)
    {
        if (IsWorking)
            return;
        IsWorking = true;
        try
        {
            await work();
            owner.ClosePanel(this);
            owner.ShowNotice(done);
            owner.OnAssetDetailsChanged();
        }
        catch (Exception ex)
        {
            owner.ShowNotice(string.Format(System.Globalization.CultureInfo.CurrentCulture, failed, NetworkErrors.Describe(ex)));
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    void Close() => owner.ClosePanel(this);
}
