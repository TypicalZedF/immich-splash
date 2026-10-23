using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.Core;
using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Services;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Sync;
using Material.Icons;

namespace ImmichSplash.App.ViewModels;

/// <summary>First-run setup: server → sign in → choose folders.</summary>
public sealed partial class OnboardingViewModel : ViewModelBase, IDisposable
{
    readonly MainWindowViewModel _main;
    readonly CancellationTokenSource _lifetime = new();
    ServerInfo? _server;
    OAuthSession? _oauth;

    public OnboardingViewModel(MainWindowViewModel main, bool resumeSignIn)
    {
        _main = main;
        var settings = AppServices.Settings.Current;
        ServerUrl = settings.WebUrl?.TrimEnd('/') ?? "";
        Email = settings.UserEmail ?? "";
        LaunchAtStartup = settings.LaunchAtStartup;

        OAuthCallbacks.Received += OnOAuthCallback;
        if (resumeSignIn && ServerUrl.Length > 0)
            _ = ConnectAsync();
        else
            _ = FindOnNetworkAsync();
    }

    // ---- Step state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerStep), nameof(IsSignInStep), nameof(IsFoldersStep))]
    public partial int Step { get; set; }

    public bool IsServerStep => Step == 0;
    public bool IsSignInStep => Step == 1;
    public bool IsFoldersStep => Step == 2;

    [ObservableProperty]
    // Every button that waits for the screen not to be busy (the SSO one stayed greyed out after connecting on its own).
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(SignInCommand), nameof(SignInWithSsoCommand), nameof(FinishCommand))]
    public partial bool IsBusy { get; set; }

    // ---- Step 1: server ----

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "";

    [ObservableProperty]
    public partial string? ServerError { get; set; }

    [ObservableProperty]
    public partial string ServerHost { get; set; } = "";

    [ObservableProperty]
    public partial string ServerVersion { get; set; } = "";

    [ObservableProperty]
    public partial bool IsInsecure { get; set; }

    /// <summary>Immich servers found on the local network, to pick instead of typing an address.</summary>
    public ObservableCollection<string> FoundServers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFoundServers))]
    public partial bool IsSearchingNetwork { get; set; }

    public bool ShowFoundServers => IsSearchingNetwork || FoundServers.Count > 0;

    async Task FindOnNetworkAsync()
    {
        IsSearchingNetwork = true;
        try
        {
            foreach (var url in await Core.Services.LocalServerFinder.FindAsync(_lifetime.Token))
                FoundServers.Add(url.ToString().TrimEnd('/'));
            Log.Info($"Found {FoundServers.Count} Immich server(s) on the local network");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't look for servers on the network: {ex.Message}");
        }
        finally
        {
            IsSearchingNetwork = false;
            OnPropertyChanged(nameof(ShowFoundServers));
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    Task UseFoundServerAsync(string url)
    {
        ServerUrl = url;
        return ConnectAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task ConnectAsync()
    {
        ServerError = null;
        IsBusy = true;
        try
        {
            _server = await ImmichClient.DiscoverAsync(ServerUrl, _lifetime.Token);
            ServerHost = _server.Host;
            ServerVersion = _server.Version.ToString();
            IsInsecure = _server.WebUrl.Scheme == Uri.UriSchemeHttp;
            ServerUrl = _server.WebUrl.ToString().TrimEnd('/');
            SignInError = null;
            await LoadLoginOptionsAsync(_server);
            Step = 1;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ServerError = ex is ServerDiscoveryException ? ex.Message : NetworkErrors.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    bool CanAct() => !IsBusy;

    // ---- Step 2: sign in ----

    [ObservableProperty]
    public partial bool OffersSso { get; set; }

    [ObservableProperty]
    public partial bool OffersPassword { get; set; } = true;

    [ObservableProperty]
    public partial string SsoButtonText { get; set; } = Tr.T("Sign in with SSO");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignInForm))]
    public partial bool IsWaitingForBrowser { get; set; }

    public bool ShowSignInForm => !IsWaitingForBrowser;

    async Task LoadLoginOptionsAsync(ServerInfo server)
    {
        try
        {
            using var anonymous = new ImmichClient(server.ApiUrl, null);
            var options = await anonymous.GetLoginOptionsAsync(_lifetime.Token);
            OffersSso = options.OAuth;
            OffersPassword = options.PasswordLogin;
            SsoButtonText = options.OAuthButtonText;
            if (!options.PasswordLogin)
                UseApiKey = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Older servers may not expose these; fall back to password + API key.
            OffersSso = false;
            OffersPassword = true;
        }
    }

    /// <summary>Opens the identity provider in the default browser; it hands the sign-in back via app.immich://.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task SignInWithSsoAsync()
    {
        if (_server is null)
            return;
        SignInError = null;
        IsBusy = true;
        try
        {
            UrlProtocol.Register();
            _oauth = OAuthSession.Create();
            using var anonymous = new ImmichClient(_server.ApiUrl, null);
            var url = await anonymous.StartOAuthAsync(OAuthSession.RedirectUri, _oauth.CodeChallenge, _oauth.State, _lifetime.Token);
            ShellLauncher.Open(url);
            IsWaitingForBrowser = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _oauth = null;
            SignInError = Tr.T("Couldn't start SSO sign-in: {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void CancelSso()
    {
        _oauth = null;
        IsWaitingForBrowser = false;
    }

    async void OnOAuthCallback(string url)
    {
        var session = _oauth;
        if (session is null || _server is null)
        {
            Log.Info($"SSO callback not for this sign-in screen (waiting: {session is not null}, server: {_server is not null})");
            return;
        }
        if (!session.Matches(url))
        {
            Log.Warn("Ignored an SSO callback that doesn't match the current sign-in");
            return;
        }

        _oauth = null;
        IsBusy = true;
        SignInError = null;
        try
        {
            await AppServices.Account.SignInWithOAuthAsync(_server, session, url, _lifetime.Token);
            IsWaitingForBrowser = false;
            AfterSignIn();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            IsWaitingForBrowser = false;
            SignInError = Tr.T("SSO sign-in didn't complete: {0}", NetworkErrors.Describe(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    void AfterSignIn()
    {
        Password = "";
        ApiKey = "";

        var settings = AppServices.Settings.Current;
        // Folders already chosen (signing in again, however you got here): back to the app, backup carries on.
        if (settings.Folders.Count > 0)
        {
            AppServices.Engine.SyncNow();
            _main.ShowShell();
            return;
        }

        LoadFolderChoices(settings);
        Step = 2;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsePassword))]
    public partial bool UseApiKey { get; set; }

    public bool UsePassword
    {
        get => !UseApiKey;
        set => UseApiKey = !value;
    }

    [ObservableProperty]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string ApiKey { get; set; } = "";

    [ObservableProperty]
    public partial string? SignInError { get; set; }

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task SignInAsync()
    {
        if (_server is null)
            return;

        SignInError = null;
        if (UseApiKey ? string.IsNullOrWhiteSpace(ApiKey) : string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
        {
            SignInError = UseApiKey ? Tr.T("Paste your API key.") : Tr.T("Enter your email and password.");
            return;
        }

        IsBusy = true;
        try
        {
            if (UseApiKey)
                await AppServices.Account.SignInWithApiKeyAsync(_server, ApiKey, _lifetime.Token);
            else
                await AppServices.Account.SignInWithPasswordAsync(_server, Email, Password, _lifetime.Token);

            AfterSignIn();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ImmichApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest)
        {
            SignInError = UseApiKey ? Tr.T("That API key wasn't accepted.") : Tr.T("Incorrect email or password.");
        }
        catch (Exception ex)
        {
            SignInError = NetworkErrors.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void Back()
    {
        CancelSso();
        if (Step > 0)
            Step--;
    }

    [RelayCommand]
    void OpenServer()
    {
        if (_server is not null)
            ShellLauncher.Open(new Uri(_server.WebUrl, "user-settings?isOpen=api-keys").ToString());
    }

    // ---- Step 3: folders ----

    public ObservableCollection<FolderChoiceViewModel> FolderChoices { get; } = [];

    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    [ObservableProperty]
    public partial string? FolderError { get; set; }

    public bool AutostartSupported => Autostart.IsSupported;

    public string AutostartLabel => OperatingSystem.IsWindows()
        ? Tr.T("Start automatically when I sign in to Windows")
        : Tr.T("Start automatically when I log in");

    void LoadFolderChoices(AppSettings settings)
    {
        FolderChoices.Clear();
        foreach (var existing in settings.Folders)
            AddChoice(existing.Path, selected: true);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddChoice(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), selected: settings.Folders.Count == 0, MaterialIconKind.ImageMultipleOutline);
        AddChoice(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), selected: settings.Folders.Count == 0, MaterialIconKind.VideoOutline);
        AddChoice(Path.Combine(profile, "Downloads"), selected: false, MaterialIconKind.DownloadOutline);
        AddChoice(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), selected: false, MaterialIconKind.Monitor);
    }

    void AddChoice(string path, bool selected, MaterialIconKind icon = MaterialIconKind.FolderOutline)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        if (FolderChoices.Any(c => string.Equals(FolderRules.Normalize(c.Path), FolderRules.Normalize(path), FolderRules.PathComparison)))
            return;
        // Never suggest folders that overlap something already selected.
        var selectedFolders = FolderChoices.Where(c => c.IsSelected).Select(c => new SyncFolder { Path = c.Path });
        if (FolderRules.ValidateNewFolder(path, selectedFolders) is not null)
            selected = false;

        var choice = new FolderChoiceViewModel(path, icon) { IsSelected = selected };
        FolderChoices.Add(choice);
        _ = choice.CountAsync(_lifetime.Token);
    }

    [RelayCommand]
    async Task AddFolderAsync()
    {
        FolderError = null;
        var path = await _main.PickFolderAsync(Tr.T("Choose a folder to back up"));
        if (path is null)
            return;

        var others = FolderChoices.Where(c => c.IsSelected).Select(c => new SyncFolder { Path = c.Path });
        if (FolderRules.ValidateNewFolder(path, others) is { } error)
        {
            FolderError = error;
            return;
        }
        AddChoice(path, selected: true);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    void Finish()
    {
        FolderError = null;
        var selected = FolderChoices.Where(c => c.IsSelected).ToList();
        for (var i = 0; i < selected.Count; i++)
        {
            for (var j = 0; j < selected.Count; j++)
            {
                if (i != j && FolderRules.IsInside(selected[i].Path, selected[j].Path))
                {
                    FolderError = Tr.T("“{0}” is inside “{1}”. Choose just one of them.", selected[i].Name, selected[j].Name);
                    return;
                }
            }
        }

        AppServices.Settings.Update(s =>
        {
            var existing = s.Folders.ToDictionary(f => FolderRules.Normalize(f.Path), FolderRules.PathComparer);
            s.Folders = selected
                .Select(c => existing.TryGetValue(FolderRules.Normalize(c.Path), out var keep) ? keep : new SyncFolder { Path = c.Path })
                .ToList();
            s.LaunchAtStartup = LaunchAtStartup;
            s.Paused = false;
            s.PausedUntil = null;
            s.UpdatePreferencesPending = true; // asked once the main window shows
        });
        Autostart.SetEnabled(LaunchAtStartup);

        var count = selected.Count;
        AppServices.Activity.Add(ActivityKind.Info,
            count == 0 ? Tr.T("Setup complete") : Tr.T("Setup complete — backing up {0}", Format.Plural(count, "folder")));
        AppServices.Engine.SyncNow();
        _main.ShowShell();
    }

    public void Dispose()
    {
        OAuthCallbacks.Received -= OnOAuthCallback;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed partial class FolderChoiceViewModel(string path, MaterialIconKind icon) : ViewModelBase
{
    public string Path { get; } = path;
    public MaterialIconKind Icon { get; } = icon;
    public string Name { get; } = new SyncFolder { Path = path }.DisplayName;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = Tr.T("Counting…");

    /// <summary>Counts photos and videos in the background so people know what they're choosing.</summary>
    public async Task CountAsync(CancellationToken ct)
    {
        const int cap = 100_000;
        try
        {
            var count = await Task.Run(() =>
            {
                var media = MediaTypes.Default;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Offline,
                };
                var n = 0;
                foreach (var file in Directory.EnumerateFiles(Path, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (media.Classify(file) != MediaKind.None && ++n >= cap)
                        break;
                }
                return n;
            }, ct);

            Detail = count switch
            {
                0 => Tr.T("No photos or videos yet"),
                >= cap => Tr.T("{0}+ photos and videos", Format.Count(cap)),
                _ => Format.Plural(count, Tr.T("photo or video"), Tr.T("photos and videos")),
            };
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            Detail = Path;
        }
    }
}
