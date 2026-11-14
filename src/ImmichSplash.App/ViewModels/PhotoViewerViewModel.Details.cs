using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotoViewerViewModel
{
    // ---- Editing the info panel: date, place, description and tags (like the web app's info panel) ----

    public bool CanEditDetails => CanEdit && Details is not null;

    public IReadOnlyList<TagInfo> Tags => Details?.Tags ?? [];
    public bool HasTags => Tags.Count > 0;
    public string PlaceOrPrompt => Details?.Place ?? (Details is { Latitude: { } lat, Longitude: { } lon }
        ? $"{lat.ToString("0.####", CultureInfo.CurrentCulture)}, {lon.ToString("0.####", CultureInfo.CurrentCulture)}"
        : Tr.T("Add a place"));
    public bool ShowPlaceRow => Place is not null || Details is { Latitude: not null } || CanEditDetails;

    void OnDetailsEdited()
    {
        foreach (var name in new[] { nameof(CanEditDetails), nameof(Tags), nameof(HasTags), nameof(PlaceOrPrompt), nameof(ShowPlaceRow) })
            OnPropertyChanged(name);
        EditingDescription = Details?.Description ?? "";
        IsEditingDate = IsEditingPlace = false;
    }

    async Task ReloadDetailsAsync()
    {
        if (_owner.Client is not { } client)
            return;
        try
        {
            Details = await client.GetAssetAsync(Asset.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Info($"Details reload failed: {ex.Message}");
        }
    }

    // Description

    [ObservableProperty]
    public partial string EditingDescription { get; set; } = "";

    [RelayCommand]
    async Task SaveDescriptionAsync()
    {
        if (_owner.Client is not { } client || Details is null || EditingDescription.Trim() == (Details.Description ?? ""))
            return;
        try
        {
            await client.UpdateAssetAsync(Asset.Id, null, null, null, EditingDescription.Trim(), CancellationToken.None);
            Details = Details with { Description = EditingDescription.Trim() is { Length: > 0 } d ? d : null };
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't save the description. {0}", NetworkErrors.Describe(ex)));
        }
    }

    // Date and time

    [ObservableProperty]
    public partial bool IsEditingDate { get; set; }

    [ObservableProperty]
    public partial DateTime? EditDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? EditTime { get; set; }

    [RelayCommand]
    void StartEditDate()
    {
        var taken = DateTimeOffset.TryParse(Details?.DateTimeOriginal ?? Asset.FileCreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToLocalTime() : DateTimeOffset.Now;
        EditDate = taken.Date;
        EditTime = taken.TimeOfDay;
        IsEditingDate = true;
    }

    [RelayCommand]
    void CancelEdit() => IsEditingDate = IsEditingPlace = false;

    [RelayCommand]
    async Task SaveDateAsync()
    {
        if (_owner.Client is not { } client || EditDate is not { } day)
            return;
        var local = day.Date + (EditTime ?? TimeSpan.Zero);
        var taken = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        try
        {
            await client.UpdateAssetAsync(Asset.Id, taken, null, null, null, CancellationToken.None);
            IsEditingDate = false;
            Asset = Asset with { FileCreatedAt = taken.ToString("o", CultureInfo.InvariantCulture) };
            DateText = $"{taken.ToString("D", CultureInfo.CurrentCulture)} · {taken.ToString("t", CultureInfo.CurrentCulture)}";
            _owner.ShowNotice(Tr.T("Date changed. The photo moves to its new place in the timeline."));
            _owner.OnAssetDetailsChanged();
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't change the date. {0}", NetworkErrors.Describe(ex)));
        }
    }

    // Place

    [ObservableProperty]
    public partial bool IsEditingPlace { get; set; }

    [ObservableProperty]
    public partial string PlaceSearch { get; set; } = "";

    public ObservableCollection<PlaceResult> PlaceResults { get; } = [];

    CancellationTokenSource? _placeSearch;

    [RelayCommand]
    void StartEditPlace()
    {
        PlaceSearch = "";
        PlaceResults.Clear();
        IsEditingPlace = true;
    }

    async partial void OnPlaceSearchChanged(string value)
    {
        _placeSearch?.Cancel();
        var cts = _placeSearch = new CancellationTokenSource();
        var text = value.Trim();
        if (text.Length < 2 || _owner.Client is not { } client)
        {
            PlaceResults.Clear();
            return;
        }
        try
        {
            await Task.Delay(250, cts.Token);
            // "48.85, 2.35" is used as it is.
            List<PlaceResult> found;
            if (TryParseCoordinates(text) is { } point)
                found = [new PlaceResult(Tr.T("These coordinates"), null, null, point.Lat, point.Lon)];
            else
                found = await client.SearchPlacesAsync(text, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            PlaceResults.Clear();
            foreach (var place in found.Take(8))
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

    internal static (double Lat, double Lon)? TryParseCoordinates(string text)
    {
        var parts = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            && lat is >= -90 and <= 90 && lon is >= -180 and <= 180
            ? (lat, lon) : null;
    }

    [RelayCommand]
    async Task ChoosePlaceAsync(PlaceResult place)
    {
        if (_owner.Client is not { } client)
            return;
        try
        {
            await client.UpdateAssetAsync(Asset.Id, null, place.Latitude, place.Longitude, null, CancellationToken.None);
            IsEditingPlace = false;
            _owner.ShowNotice(Tr.T("Place set to {0}", place.Label));
            // Immich works out the city name in the background; show what it has now.
            await Task.Delay(1500);
            await ReloadDetailsAsync();
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't set the place. {0}", NetworkErrors.Describe(ex)));
        }
    }

    // Tags

    [ObservableProperty]
    public partial string TagSearch { get; set; } = "";

    public ObservableCollection<TagChoice> TagChoices { get; } = [];

    List<TagInfo>? _allTags;

    async partial void OnTagSearchChanged(string value)
    {
        TagChoices.Clear();
        var text = value.Trim();
        if (text.Length == 0 || _owner.Client is not { } client)
            return;
        try
        {
            _allTags ??= await client.GetTagsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Info($"Tags unavailable: {ex.Message}");
            _allTags = [];
        }
        if (TagSearch.Trim() != text)
            return;
        var have = Tags.Select(t => t.Id).ToHashSet();
        foreach (var tag in _allTags.Where(t => !have.Contains(t.Id) && t.Value.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Take(6))
            TagChoices.Add(new TagChoice(tag, tag.Value));
        if (!_allTags.Any(t => string.Equals(t.Value, text, StringComparison.CurrentCultureIgnoreCase)))
            TagChoices.Add(new TagChoice(null, Tr.T("Create tag “{0}”", text)));
    }

    [RelayCommand]
    Task AddTypedTagAsync() => TagChoices.FirstOrDefault() is { } first ? AddTagAsync(first) : Task.CompletedTask;

    [RelayCommand]
    async Task AddTagAsync(TagChoice choice)
    {
        if (_owner.Client is not { } client)
            return;
        try
        {
            var tag = choice.Tag ?? await client.CreateTagAsync(TagSearch.Trim(), CancellationToken.None);
            if (choice.Tag is null)
                _allTags?.Add(tag);
            await client.TagAssetsAsync(tag.Id, [Asset.Id], CancellationToken.None);
            TagSearch = "";
            if (Details is { } d)
                Details = d with { Tags = [.. Tags.Append(tag).OrderBy(t => t.Value, StringComparer.CurrentCultureIgnoreCase)] };
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't add the tag. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    async Task RemoveTagAsync(TagInfo tag)
    {
        if (_owner.Client is not { } client)
            return;
        try
        {
            await client.UntagAssetsAsync(tag.Id, [Asset.Id], CancellationToken.None);
            if (Details is { } d)
                Details = d with { Tags = Tags.Where(t => t.Id != tag.Id).ToList() };
        }
        catch (Exception ex)
        {
            _owner.ShowNotice(Tr.T("Couldn't remove the tag. {0}", NetworkErrors.Describe(ex)));
        }
    }

    [RelayCommand]
    void OpenTag(TagInfo tag) => _owner.OpenTag(tag);
}

/// <summary>A suggestion under the tag box: an existing tag, or (Tag null) making a new one.</summary>
public sealed record TagChoice(TagInfo? Tag, string Label);
