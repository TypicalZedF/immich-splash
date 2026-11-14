using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- Tags (Explore lists them; a tag opens its photos) and editing details ----

    public ObservableCollection<TagInfo> ExploreTags { get; } = [];

    [ObservableProperty]
    public partial bool HasExploreTags { get; set; }

    async Task LoadTagsAsync()
    {
        if (Client is not { } client)
            return;
        try
        {
            var tags = await client.GetTagsAsync(_lifetime.Token);
            ExploreTags.Clear();
            foreach (var tag in tags)
                ExploreTags.Add(tag);
            HasExploreTags = ExploreTags.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Tags unavailable: {ex.Message}");
            HasExploreTags = false;
        }
    }

    [RelayCommand]
    internal void OpenTag(TagInfo tag)
    {
        Viewer = null;
        OpenDrill(new GridQuery(tag.Value, (c, ct) => TimelineAsync(c, new TimelineFilter(TagId: tag.Id), ct)));
    }

    /// <summary>Dates or places changed: the grid shows the photos in their new order.</summary>
    internal void OnAssetDetailsChanged()
    {
        if (ShowGrid)
            _ = LoadGridAsync();
    }

    [RelayCommand]
    void EditSelectionDetails() => Panel = new EditDetailsPanelViewModel(this, SelectedIds);
}
