using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotosViewModel
{
    // ---- Albums, Explore (people and places), Sharing ----

    /// <summary>How many people and places Explore shows before "Show all".</summary>
    const int ExplorePreview = 12;

    public ObservableCollection<AlbumCardViewModel> Albums { get; } = [];
    public ObservableCollection<PersonCardViewModel> People { get; } = [];
    public ObservableCollection<PlaceCardViewModel> Places { get; } = [];
    readonly List<PersonCardViewModel> _allPeople = [];
    readonly List<PlaceCardViewModel> _allPlaces = [];

    /// <summary>Asks for a name for a person (Immich then groups their photos under it).</summary>
    internal void NamePerson(PersonInfo person)
    {
        if (_main is null)
            return;
        _main.ShowOverlay(new NameDialogViewModel(_main,
            person.Name.Length > 0 ? Tr.T("Change this person's name") : Tr.T("Who is this?"),
            Tr.T("Name"), person.Name, Tr.T("Save"),
            async name =>
            {
                if (Client is not { } client)
                    return Tr.T("Not signed in.");
                try
                {
                    await client.UpdatePersonAsync(person.Id, name, CancellationToken.None);
                    ShowNotice(Tr.T("Named them {0}", name));
                    ReplacePerson(person.Id, person with { Name = name });
                    return null;
                }
                catch (Exception ex)
                {
                    return Tr.T("Couldn't save the name. {0}", NetworkErrors.Describe(ex));
                }
            }));
    }

    /// <summary>
    /// Discards a face: Immich forgets this person (their photos stay), and it won't suggest them again
    /// unless the server is made to scan everything for faces anew.
    /// </summary>
    internal async Task DiscardPersonAsync(PersonInfo person)
    {
        if (Client is not { } client || _main is null)
            return;
        var who = person.Name.Length > 0 ? $"“{person.Name}”" : Tr.T("this face");
        if (!await _main.ConfirmAsync(Tr.T("Discard {0}?", who),
                Tr.T("Immich forgets this person. The photos stay in your library, and the face isn't offered again unless you make the server scan every photo for faces anew."),
                Tr.T("Discard"), danger: true))
            return;
        try
        {
            await client.DeletePersonAsync(person.Id, CancellationToken.None);
            ShowNotice(Tr.T("Discarded {0}", who));
            ReplacePerson(person.Id, null);
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't discard that. {0}", NetworkErrors.Describe(ex)));
        }
    }

    /// <summary>Merges a person into someone else (picked on a dialog that warns it can't be undone).</summary>
    internal void MergePerson(PersonInfo person)
    {
        if (_main is null)
            return;
        var people = (_allPeople.Count > 0 ? _allPeople : People.ToList()).Select(p => p.Person);
        _main.ShowOverlay(new MergePeopleViewModel(this, _main, person, people, Thumbnails));
    }

    internal void OnPeopleMerged(PersonInfo merged, PersonInfo into)
    {
        ShowNotice(Tr.T("Merged into {0}", into.Name.Length > 0 ? $"“{into.Name}”" : Tr.T("the other face")));
        ReplacePerson(merged.Id, null);
    }

    /// <summary>
    /// Changes one person's card where it is (or takes it away with null), without reloading the list: it stays
    /// expanded after "Show all" and nothing jumps.
    /// </summary>
    void ReplacePerson(string personId, PersonInfo? updated)
    {
        PersonCardViewModel? old = null;
        PersonCardViewModel? fresh = null;
        if (updated is not null)
            fresh = new PersonCardViewModel(this, updated);
        foreach (var list in new IList<PersonCardViewModel>[] { People, _allPeople })
        {
            var index = -1;
            for (var i = 0; i < list.Count; i++)
                if (list[i].Person.Id == personId)
                    index = i;
            if (index < 0)
                continue;
            old ??= list[index];
            if (fresh is null)
                list.RemoveAt(index);
            else
                list[index] = fresh;
        }
        if (old is null)
            return;
        if (fresh is not null)
        {
            fresh.Cover = old.Cover; // same face: keep the picture
            old.Cover = null;
        }
        old.Dispose();
        // Discarding from the preview pulls the next face in.
        if (fresh is null && HasMorePeople && People.Count < ExplorePreview && _allPeople.Count > People.Count)
        {
            var next = _allPeople[People.Count];
            People.Add(next);
            LoadCovers([next]);
            HasMorePeople = _allPeople.Count > People.Count;
        }
        if (_allPeople.Count == 0)
            PeopleMessage = Tr.T("No people yet. Immich finds faces when machine learning is turned on in your server settings.");
    }

    [ObservableProperty]
    public partial bool IsLoadingCollection { get; set; }

    [ObservableProperty]
    public partial string? CollectionMessage { get; set; }

    [ObservableProperty]
    public partial string? PeopleMessage { get; set; }

    [ObservableProperty]
    public partial string? PlacesMessage { get; set; }

    [ObservableProperty]
    public partial bool HasMorePeople { get; set; }

    [ObservableProperty]
    public partial bool HasMorePlaces { get; set; }

    async Task LoadAlbumsAsync()
    {
        // The page's own empty message is shown by the album list (it knows about filters and search).
        await LoadCardsAsync(async c =>
        {
            var albums = await c.GetAlbumsAsync(_lifetime.Token);
            return albums.Select(a => new AlbumCardViewModel(this, a)).ToList();
        }, Albums, null, "", m => CollectionMessage = string.IsNullOrEmpty(m) ? null : m);
        RebuildAlbumGroups();
        AppServices.RaiseAlbumsChanged();
    }

    Task LoadPeopleAsync() => LoadCardsAsync(async c =>
    {
        var people = await c.GetPeopleAsync(_lifetime.Token);
        // Named people first, like the web app.
        return people.OrderByDescending(p => p.Name.Length > 0).Select(p => new PersonCardViewModel(this, p)).ToList();
    }, People, _allPeople, Tr.T("No people yet. Immich finds faces when machine learning is turned on in your server settings."),
        m => PeopleMessage = m, more => HasMorePeople = more);

    Task LoadPlacesAsync() => LoadCardsAsync(async c =>
    {
        var places = await c.GetPlacesAsync(_lifetime.Token);
        return places.Select(p => new PlaceCardViewModel(this, p)).ToList();
    }, Places, _allPlaces, Tr.T("No places yet. Photos with a location show up here."),
        m => PlacesMessage = m, more => HasMorePlaces = more);

    /// <summary>
    /// Loads cards into <paramref name="shown"/>. With <paramref name="all"/>, only a preview is shown
    /// (Explore) and the rest waits for "Show all"; covers load only for cards on screen.
    /// </summary>
    async Task LoadCardsAsync<T>(Func<ImmichClient, Task<List<T>>> load, ObservableCollection<T> shown, List<T>? all,
        string emptyText, Action<string?> setMessage, Action<bool>? setHasMore = null)
        where T : LibraryCardViewModel
    {
        if (Client is not { } client)
            return;
        IsLoadingCollection = true;
        setMessage(null);
        try
        {
            var items = await load(client);
            var expanded = all is { Count: > ExplorePreview } && shown.Count == all.Count;
            foreach (var old in shown.Concat(all ?? []).Distinct())
                old.Dispose();
            shown.Clear();
            all?.Clear();
            all?.AddRange(items);
            var preview = all is null || expanded ? items : items.Take(ExplorePreview).ToList();
            foreach (var item in preview)
                shown.Add(item);
            setHasMore?.Invoke(items.Count > preview.Count);
            if (items.Count == 0)
                setMessage(emptyText);
            LoadCovers(preview);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the library page", ex);
            setMessage(Tr.T("Couldn't load this page. {0}", NetworkErrors.Describe(ex)));
        }
        finally
        {
            IsLoadingCollection = false;
        }
    }

    void LoadCovers(IEnumerable<LibraryCardViewModel> cards)
    {
        if (Thumbnails is not { } loader)
            return;
        foreach (var card in cards)
            _ = card.LoadCoverAsync(loader, CardDecodeWidth, _lifetime.Token);
    }

    [RelayCommand]
    void ShowAllPeople() => ShowAll(_allPeople, People, more => HasMorePeople = more);

    [RelayCommand]
    void ShowAllPlaces() => ShowAll(_allPlaces, Places, more => HasMorePlaces = more);

    void ShowAll<T>(List<T> all, ObservableCollection<T> shown, Action<bool> setHasMore) where T : LibraryCardViewModel
    {
        var rest = all.Skip(shown.Count).ToList();
        foreach (var item in rest)
            shown.Add(item);
        setHasMore(false);
        LoadCovers(rest);
    }

    /// <summary>
    /// The album on screen, put in the sidebar's recent list once you leave it: adding it while you look at it
    /// would show it twice (the page and the sidebar) and shuffle the list under the pointer.
    /// </summary>
    string? _albumToRemember;

    internal void CommitRecentAlbum()
    {
        if (_albumToRemember is { } id)
        {
            _albumToRemember = null;
            RememberAlbum(id);
        }
    }

    internal void OpenAlbum(AlbumInfo album)
    {
        CommitRecentAlbum();
        _albumToRemember = album.Id;
        OpenDrill(new GridQuery(album.AlbumName, (c, ct) => TimelineAsync(c, new TimelineFilter(AlbumId: album.Id), ct), album));
    }

    /// <summary>Puts an album first in the sidebar's list of recent albums.</summary>
    internal static void RememberAlbum(string albumId) =>
        AppServices.Settings.Update(s =>
        {
            s.RecentAlbumIds.Remove(albumId);
            s.RecentAlbumIds.Insert(0, albumId);
            if (s.RecentAlbumIds.Count > 10)
                s.RecentAlbumIds.RemoveRange(10, s.RecentAlbumIds.Count - 10);
        });

    internal void OpenPerson(PersonInfo person) =>
        OpenDrill(new GridQuery(person.Name.Length > 0 ? person.Name : Tr.T("Unnamed person"), (c, ct) => TimelineAsync(c, new TimelineFilter(PersonId: person.Id), ct)));

    internal void OpenPlace(PlaceInfo place) =>
        OpenDrill(new GridQuery(place.City, async (c, ct) => BucketState.FromAssets(await c.SearchAsync(null, place.City, 2000, ct))));

    internal void OpenPartner(PartnerInfo partner) =>
        OpenDrill(new GridQuery(partner.Name, (c, ct) => TimelineAsync(c, new TimelineFilter(UserId: partner.Id), ct)));

    internal void OnAlbumsChanged()
    {
        if (Page == LibraryPage.Albums)
            _ = LoadAlbumsAsync();
    }

    // Sharing: albums shared with you or by you, your shared links, and partners sharing with you.

    public ObservableCollection<AlbumCardViewModel> SharedAlbums { get; } = [];
    public ObservableCollection<SharedLinkViewModel> SharedLinks { get; } = [];
    public ObservableCollection<PartnerViewModel> Partners { get; } = [];

    [ObservableProperty]
    public partial string? SharedAlbumsMessage { get; set; }

    [ObservableProperty]
    public partial string? SharedLinksMessage { get; set; }

    [ObservableProperty]
    public partial bool HasPartners { get; set; }

    async Task LoadSharingAsync()
    {
        var me = AppServices.Settings.Current.UserId;
        var albums = LoadCardsAsync(async c => (await c.GetSharedAlbumsAsync(_lifetime.Token))
                .Where(a => a.IsSharedWithPeople(me))
                .OrderBy(a => a.AlbumName, StringComparer.CurrentCultureIgnoreCase).Select(a => new AlbumCardViewModel(this, a)).ToList(),
            SharedAlbums, null, Tr.T("No shared albums yet."), m => SharedAlbumsMessage = m);
        await Task.WhenAll(albums, LoadSharedLinksAsync(), LoadPartnersAsync());
    }

    async Task LoadSharedLinksAsync()
    {
        if (Client is not { } client)
            return;
        try
        {
            var links = await client.GetSharedLinksAsync(_lifetime.Token);
            var baseUrl = await client.GetShareBaseUrlAsync(AppServices.Settings.Current.WebUrl ?? client.ApiUrl.ToString(), _lifetime.Token);
            foreach (var old in SharedLinks)
                old.Dispose();
            SharedLinks.Clear();
            foreach (var link in links)
                SharedLinks.Add(new SharedLinkViewModel(this, link, $"{baseUrl}/share/{link.Key}"));
            SharedLinksMessage = links.Count == 0 ? Tr.T("No shared links yet. Select photos (or open an album) and choose Share.") : null;
            if (Thumbnails is { } loader)
                foreach (var link in SharedLinks)
                    _ = link.LoadCoverAsync(loader, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load shared links", ex);
            SharedLinksMessage = Tr.T("Couldn't load your shared links. {0}", NetworkErrors.Describe(ex));
        }
    }

    async Task LoadPartnersAsync()
    {
        if (Client is not { } client)
            return;
        try
        {
            var partners = await client.GetPartnersAsync(_lifetime.Token);
            Partners.Clear();
            foreach (var partner in partners)
                Partners.Add(new PartnerViewModel(this, partner));
            HasPartners = Partners.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Partners unavailable: {ex.Message}");
            HasPartners = false;
        }
    }

    internal async Task DeleteSharedLinkAsync(SharedLinkViewModel link)
    {
        if (Client is not { } client || _main is null)
            return;
        if (!await _main.ConfirmAsync(Tr.T("Delete this link?"), Tr.T("Anyone who has it won't be able to open it anymore. The photos stay in Immich."),
                Tr.T("Delete link"), danger: true))
            return;
        try
        {
            await client.DeleteSharedLinkAsync(link.Link.Id, CancellationToken.None);
            ForgetLink(link.Link.Id);
            SharedLinks.Remove(link);
            link.Dispose();
            if (SharedLinks.Count == 0)
                SharedLinksMessage = Tr.T("No shared links yet. Select photos (or open an album) and choose Share.");
            ShowNotice(Tr.T("Link deleted"));
        }
        catch (Exception ex)
        {
            ShowNotice(Tr.T("Couldn't delete the link. {0}", NetworkErrors.Describe(ex)));
        }
    }
}
