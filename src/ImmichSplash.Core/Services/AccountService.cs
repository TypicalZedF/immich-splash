using ImmichSplash.Core.Api;
using ImmichSplash.Core.Platform;
using ImmichSplash.Core.Settings;
using ImmichSplash.Core.Storage;

namespace ImmichSplash.Core.Services;

/// <summary>Owns the signed-in session: credentials, the API client and the account identity.</summary>
/// <param name="credentialsPath">Where the sign-in is stored (tests use their own file).</param>
public sealed class AccountService(SettingsStore settings, StateDatabase db, string? credentialsPath = null) : IDisposable
{
    const string AccountMetaKey = "account";

    ImmichClient? _client;

    public ImmichClient? Client
    {
        get => _client;
        private set
        {
            if (_client is not null)
                _client.SignedOut -= OnSignedOut;
            _client = value;
            if (value is not null)
                value.SignedOut += OnSignedOut;
        }
    }

    /// <summary>The server stopped accepting this sign-in (until you sign in again).</summary>
    public bool IsRejected { get; private set; }

    /// <summary>Raised once (on any thread) when the server stops accepting the sign-in.</summary>
    public event Action? SessionRejected;

    /// <summary>For tests: what the clients made here talk through.</summary>
    internal HttpMessageHandler? Handler { get; init; }

    readonly Lock _gate = new();

    void OnSignedOut()
    {
        lock (_gate)
        {
            if (IsRejected)
                return;
            // Another copy of the app (an older one still in the tray, or the one the browser started for the SSO
            // link) shares the stored sign-in. When it signs in again it logs out the old session, which this copy
            // still uses: take the new sign-in over instead of calling this one signed out.
            if (TryAdoptStoredSignIn())
                return;
            IsRejected = true;
        }
        Log.Warn("The server no longer accepts this sign-in (logged out, expired or revoked)");
        SessionRejected?.Invoke();
    }

    bool TryAdoptStoredSignIn()
    {
        if (Client is not { } current || CredentialStore.Load(credentialsPath) is not { } stored || stored == current.Credential)
            return false;
        Client = new ImmichClient(current.ApiUrl, stored, Handler) { ServerVersion = current.ServerVersion, DeviceId = current.DeviceId };
        Log.Info("The server dropped this sign-in; using the newer one signed in elsewhere in the app");
        SessionChanged?.Invoke();
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => current.Dispose(), TaskScheduler.Default);
        return true;
    }

    public bool IsSignedIn => Client is not null;

    /// <summary>Raised when the user signs in or out. May fire on any thread.</summary>
    public event Action? SessionChanged;

    /// <summary>Restores the previous session from disk, if any.</summary>
    public void Restore()
    {
        var s = settings.Current;
        if (!s.IsConfigured || !Uri.TryCreate(s.ServerUrl, UriKind.Absolute, out var apiUrl))
            return;

        var credential = CredentialStore.Load(credentialsPath);
        if (credential is null)
            return;

        Client = new ImmichClient(apiUrl, credential, Handler) { DeviceId = Environment.MachineName };
        Log.Info($"Restored session for {s.UserEmail} on {apiUrl.Host}");
    }

    public async Task<UserInfo> SignInWithPasswordAsync(ServerInfo server, string email, string password, CancellationToken ct)
    {
        string token;
        using (var anonymous = new ImmichClient(server.ApiUrl, null))
            token = await anonymous.LoginAsync(email, password, ct);
        return await CompleteSignInAsync(server, new AuthCredential(AuthKind.AccessToken, token), ct);
    }

    /// <summary>Completes an SSO sign-in once the browser has redirected back to the app.</summary>
    public async Task<UserInfo> SignInWithOAuthAsync(ServerInfo server, OAuthSession session, string callbackUrl, CancellationToken ct)
    {
        Log.Info($"SSO callback received: {OAuthSession.Describe(callbackUrl)}");
        if (OAuthSession.ProviderError(callbackUrl) is { } providerError)
            throw new InvalidOperationException($"Your sign-in provider refused the request: {providerError}");

        string token;
        try
        {
            using var anonymous = new ImmichClient(server.ApiUrl, null);
            token = await anonymous.FinishOAuthAsync(OAuthSession.Normalize(callbackUrl), session.State, session.CodeVerifier, ct);
        }
        catch (ImmichApiException ex)
        {
            Log.Warn("Server rejected the SSO sign-in", ex);
            throw new ImmichApiException(ex.StatusCode,
                $"{ex.Message}. Your Immich server's log (\"OAuth login failed\") shows the exact reason.");
        }
        return await CompleteSignInAsync(server, new AuthCredential(AuthKind.AccessToken, token), ct);
    }

    public Task<UserInfo> SignInWithApiKeyAsync(ServerInfo server, string apiKey, CancellationToken ct) =>
        CompleteSignInAsync(server, new AuthCredential(AuthKind.ApiKey, apiKey.Trim()), ct);

    async Task<UserInfo> CompleteSignInAsync(ServerInfo server, AuthCredential credential, CancellationToken ct)
    {
        var client = new ImmichClient(server.ApiUrl, credential, Handler) { ServerVersion = server.Version, DeviceId = Environment.MachineName };
        try
        {
            var me = await client.GetMeAsync(ct);

            // The local index belongs to one account on one server; start fresh if that changes.
            var accountKey = $"{server.ApiUrl}|{me.Id}";
            if (db.GetMeta(AccountMetaKey) != accountKey)
            {
                db.ResetAll();
                db.SetMeta(AccountMetaKey, accountKey);
            }

            CredentialStore.Save(credential, credentialsPath);
            settings.Update(s =>
            {
                s.ServerUrl = server.ApiUrl.ToString();
                s.WebUrl = server.WebUrl.ToString();
                s.ServerVersion = server.Version.ToString();
                s.UserId = me.Id;
                s.UserName = me.Name;
                s.UserEmail = me.Email;
            });

            var previous = Client;
            IsRejected = false;
            Client = client;
            previous?.Dispose();
            Log.Info($"Signed in as {me.Email} on {server.Host} ({server.Version})");
            SessionChanged?.Invoke();
            return me;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <param name="keepAppLock">
    /// Signing in again after the server dropped the sign-in: the app lock stays as it is. (A plain sign-out also
    /// turns it off: that's the "forgot my app password" way out.)
    /// </param>
    public async Task SignOutAsync(bool keepAppLock = false)
    {
        var client = Client;
        Client = null;
        IsRejected = false;
        SessionChanged?.Invoke();

        if (client is not null)
        {
            if (client.Credential?.Kind == AuthKind.AccessToken)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.LogoutAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    Log.Warn("Server logout failed (signing out locally anyway)", ex);
                }
            }
            client.Dispose();
        }

        // Only our own sign-in: another copy of the app may have stored a newer one since.
        var stored = CredentialStore.Load(credentialsPath);
        if (stored is null || stored == client?.Credential)
            CredentialStore.Delete(credentialsPath);
        LockedPinStore.Delete();
        settings.Update(s =>
        {
            s.UserId = null;
            s.UserName = null;
            if (keepAppLock)
                return; // signing in again (the email stays filled in too)
            s.UserEmail = null;
            // The app lock belongs to the signed-in user; signing out is also the "forgot my password" path.
            s.AppLock = AppLockMode.Off;
            s.LockPasswordHash = null;
        });
        Log.Info("Signed out");
    }

    /// <summary>Raised when the server address in use changes (e.g. moving between home and away).</summary>
    public event Action? EndpointChanged;

    /// <summary>
    /// Talks to the same server and account through another address. The old client keeps working
    /// for a while for anything still using it.
    /// </summary>
    public void SwitchEndpoint(Uri apiUrl)
    {
        if (Client is not { } current || current.ApiUrl == apiUrl)
            return;
        Client = new ImmichClient(apiUrl, current.Credential, Handler) { ServerVersion = current.ServerVersion, DeviceId = current.DeviceId };
        Log.Info($"Now connecting through {apiUrl.Host}");
        EndpointChanged?.Invoke();
        _ = Task.Delay(TimeSpan.FromMinutes(10)).ContinueWith(_ => current.Dispose(), TaskScheduler.Default);
    }

    /// <summary>For tests: use a pre-built client without touching disk.</summary>
    internal void UseClient(ImmichClient client)
    {
        IsRejected = false;
        Client = client;
        SessionChanged?.Invoke();
    }

    public void Dispose()
    {
        var client = Client;
        Client = null;
        client?.Dispose();
    }
}
