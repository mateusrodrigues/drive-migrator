using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;
using Microsoft.Identity.Client;

namespace DriveMigrator.Providers.Microsoft;

/// <summary>
/// OneDrive, Outlook Mail, Calendar and Contacts through Microsoft Graph, for personal Microsoft accounts and
/// work/school (Entra ID) accounts. Signs in through the system browser using the user's own app registration.
/// </summary>
public sealed class MicrosoftCloudProvider(ISecretStore secrets, ProviderCredentialStore credentials) : ICloudProvider, IDisposable
{
    public const string ProviderId = "microsoft";
    internal const string ClientIdField = "clientId";
    internal const string TenantField = "tenant";
    internal const string DefaultTenant = "common";

    internal static readonly string[] Scopes =
    [
        "User.Read",
        "Files.ReadWrite.All",
        "Mail.ReadWrite",
        "Calendars.ReadWrite",
        "Contacts.ReadWrite",
    ];

    private const string SuccessPage =
        "<html><head><title>Drive Migrator</title></head><body style=\"font-family:sans-serif\">" +
        "<p>You're signed in. You can close this tab and return to Drive Migrator.</p></body></html>";

    private readonly SemaphoreSlim _appGate = new(1, 1);
    private (string ClientId, string Tenant, IPublicClientApplication App)? _app;

    public string Id => ProviderId;

    public string DisplayName => "Microsoft";

    public IReadOnlySet<CapabilityKind> SupportedCapabilities { get; } = Enum.GetValues<CapabilityKind>().ToHashSet();

    public IReadOnlyList<CredentialField> CredentialFields { get; } =
    [
        new(ClientIdField, "Application (client) ID")
        {
            Help = "Microsoft Entra admin center → App registrations → your app → Overview.",
        },
        new(TenantField, "Tenant", IsRequired: false)
        {
            DefaultValue = DefaultTenant,
            Help = "Leave empty (\"common\") for personal and work accounts, use \"organizations\" for work accounts only, or a tenant ID/domain to restrict to one organization.",
        },
    ];

    public async Task<IAccountSession> SignInAsync(string? loginHint = null, CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync(cancellationToken).ConfigureAwait(false);
        var request = app.AcquireTokenInteractive(Scopes)
            .WithUseEmbeddedWebView(false)
            .WithSystemWebViewOptions(new SystemWebViewOptions { HtmlMessageSuccess = SuccessPage });
        request = loginHint is null
            ? request.WithPrompt(Prompt.SelectAccount)
            : request.WithLoginHint(loginHint);

        var result = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var displayName = result.ClaimsPrincipal?.FindFirst("name")?.Value ?? result.Account.Username;
        var account = new AccountInfo(Id, result.Account.HomeAccountId.Identifier, displayName, result.Account.Username);
        return new MicrosoftAccountSession(account, app, result.Account);
    }

    public async Task<IAccountSession> RestoreSessionAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var app = await GetAppAsync(cancellationToken).ConfigureAwait(false);
        var msalAccount = await app.GetAccountAsync(account.AccountId).ConfigureAwait(false)
            ?? throw new ReauthenticationRequiredException($"No saved sign-in for {account.Email}. Re-authorize the account.");

        var session = new MicrosoftAccountSession(account, app, msalAccount);
        await session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task SignOutAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!await credentials.IsConfiguredAsync(this, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var app = await GetAppAsync(cancellationToken).ConfigureAwait(false);
        if (await app.GetAccountAsync(account.AccountId).ConfigureAwait(false) is { } msalAccount)
        {
            await app.RemoveAsync(msalAccount).ConfigureAwait(false);
        }
    }

    public void Dispose() => _appGate.Dispose();

    /// <summary>Returns the MSAL client for the current credentials, rebuilding it if they changed in Settings.</summary>
    private async Task<IPublicClientApplication> GetAppAsync(CancellationToken cancellationToken)
    {
        var values = await credentials.GetRequiredAsync(this, cancellationToken).ConfigureAwait(false);
        var clientId = values[ClientIdField];
        var tenant = values[TenantField];

        await _appGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is { } current && current.ClientId == clientId && current.Tenant == tenant)
            {
                return current.App;
            }

            var app = PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}")
                .WithRedirectUri("http://localhost")
                .WithClientName("DriveMigrator")
                .Build();
            BindTokenCache(app.UserTokenCache, $"{Id}/token-cache/{clientId}");
            _app = (clientId, tenant, app);
            return app;
        }
        finally
        {
            _appGate.Release();
        }
    }

    // Persist MSAL's cache in the OS secret store. Tokens are tied to the client ID, so each client gets its own cache.
    private void BindTokenCache(ITokenCache cache, string key)
    {
        cache.SetBeforeAccessAsync(async args =>
        {
            var data = await secrets.GetAsync(key).ConfigureAwait(false);
            args.TokenCache.DeserializeMsalV3(data, shouldClearExistingCache: true);
        });
        cache.SetAfterAccessAsync(async args =>
        {
            if (args.HasStateChanged)
            {
                await secrets.SetAsync(key, args.TokenCache.SerializeMsalV3()).ConfigureAwait(false);
            }
        });
    }
}
