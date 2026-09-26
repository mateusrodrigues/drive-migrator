using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace DriveMigrator.Providers.Google;

/// <summary>
/// Google Drive, Gmail, Calendar and Contacts. Signs in through the system browser with a loopback redirect,
/// using the Desktop OAuth client the user registered in their own Google Cloud project.
/// </summary>
public sealed class GoogleCloudProvider(ISecretStore secrets, ProviderCredentialStore credentials) : ICloudProvider
{
    public const string ProviderId = "google";
    internal const string ClientIdField = "clientId";
    internal const string ClientSecretField = "clientSecret";

    internal static readonly string[] Scopes =
    [
        "openid",
        "email",
        "profile",
        "https://www.googleapis.com/auth/drive",
        "https://www.googleapis.com/auth/gmail.modify",
        "https://www.googleapis.com/auth/calendar",
        "https://www.googleapis.com/auth/contacts",
    ];

    private const string SuccessPage =
        "<html><head><title>Drive Migrator</title></head><body style=\"font-family:sans-serif\">" +
        "<p>You're signed in. You can close this tab and return to Drive Migrator.</p></body></html>";

    public string Id => ProviderId;

    public string DisplayName => "Google";

    public IReadOnlySet<CapabilityKind> SupportedCapabilities { get; } = Enum.GetValues<CapabilityKind>().ToHashSet();

    public IReadOnlyList<CredentialField> CredentialFields { get; } =
    [
        new(ClientIdField, "OAuth client ID")
        {
            Help = "Google Cloud console → APIs & Services → Credentials → OAuth client ID of type \"Desktop app\".",
        },
        new(ClientSecretField, "OAuth client secret", IsSecret: true)
        {
            Help = "Shown next to the client ID. Desktop clients need it, but Google does not treat it as confidential.",
        },
    ];

    public async Task<IAccountSession> SignInAsync(string? loginHint = null, CancellationToken cancellationToken = default)
    {
        var flow = await CreateFlowAsync(loginHint, cancellationToken).ConfigureAwait(false);

        // The account's stable id (the OpenID "sub") is only known after sign-in, so authorize under a
        // temporary key and move the token once we know who signed in.
        var pendingKey = $"pending-{Guid.NewGuid():N}";
        try
        {
            var app = new AuthorizationCodeInstalledApp(flow, new LocalServerCodeReceiver(SuccessPage));
            var pending = await app.AuthorizeAsync(pendingKey, cancellationToken).ConfigureAwait(false);

            var identity = await GoogleJsonWebSignature.ValidateAsync(
                pending.Token.IdToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = [flow.ClientSecrets.ClientId] }).ConfigureAwait(false);

            await flow.DataStore.StoreAsync(identity.Subject, pending.Token).ConfigureAwait(false);
            var account = new AccountInfo(Id, identity.Subject, identity.Name ?? identity.Email, identity.Email);
            return new GoogleAccountSession(account, new UserCredential(flow, identity.Subject, pending.Token));
        }
        finally
        {
            await flow.DataStore.DeleteAsync<TokenResponse>(pendingKey).ConfigureAwait(false);
        }
    }

    public async Task<IAccountSession> RestoreSessionAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var flow = await CreateFlowAsync(loginHint: null, cancellationToken).ConfigureAwait(false);
        var token = await flow.LoadTokenAsync(account.AccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new ReauthenticationRequiredException($"No saved sign-in for {account.Email}. Re-authorize the account.");

        var credential = new UserCredential(flow, account.AccountId, token);
        try
        {
            // Refreshes the access token if needed, which proves the grant is still valid.
            await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (TokenResponseException ex) when (ex.Error?.Error is "invalid_grant" or "unauthorized_client" or "invalid_client")
        {
            throw new ReauthenticationRequiredException($"Google no longer accepts the saved sign-in for {account.Email} ({ex.Error.Error}).", ex);
        }

        return new GoogleAccountSession(account, credential);
    }

    public async Task SignOutAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var flow = await TryCreateFlowAsync(cancellationToken).ConfigureAwait(false);
        if (flow is null)
        {
            return;
        }

        var token = await flow.LoadTokenAsync(account.AccountId, cancellationToken).ConfigureAwait(false);
        if (token is not null)
        {
            try
            {
                await new UserCredential(flow, account.AccountId, token).RevokeTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TokenResponseException or HttpRequestException)
            {
                // Already revoked, or offline. Forgetting the token locally is what matters.
            }
        }

        await flow.DeleteTokenAsync(account.AccountId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GoogleAuthorizationCodeFlow> CreateFlowAsync(string? loginHint, CancellationToken cancellationToken)
    {
        var values = await credentials.GetRequiredAsync(this, cancellationToken).ConfigureAwait(false);
        return CreateFlow(values[ClientIdField], values[ClientSecretField], loginHint);
    }

    private async Task<GoogleAuthorizationCodeFlow?> TryCreateFlowAsync(CancellationToken cancellationToken)
    {
        var values = await credentials.GetAsync(Id, cancellationToken).ConfigureAwait(false);
        return values.TryGetValue(ClientIdField, out var clientId) && values.TryGetValue(ClientSecretField, out var clientSecret)
            ? CreateFlow(clientId, clientSecret, loginHint: null)
            : null;
    }

    private GoogleAuthorizationCodeFlow CreateFlow(string clientId, string clientSecret, string? loginHint)
        => new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = Scopes,

            // Tokens are tied to the OAuth client, so changing the client ID orphans them (accounts then need re-authorizing).
            DataStore = new SecretStoreDataStore(secrets, $"{Id}/{clientId}/"),
            LoginHint = loginHint,

            // "consent" makes Google return a refresh token even if this client was authorized before.
            Prompt = loginHint is null ? "select_account consent" : "consent",
        });
}
