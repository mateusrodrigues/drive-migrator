using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Providers.Microsoft.Contacts;
using DriveMigrator.Providers.Microsoft.Drive;
using DriveMigrator.Providers.Microsoft.Graph;
using DriveMigrator.Providers.Microsoft.Mail;
using Microsoft.Identity.Client;

namespace DriveMigrator.Providers.Microsoft;

public sealed class MicrosoftAccountSession : IAccountSession
{
    private readonly IPublicClientApplication _app;
    private readonly IAccount _msalAccount;

    internal MicrosoftAccountSession(AccountInfo account, IPublicClientApplication app, IAccount msalAccount, HttpClient http)
    {
        Account = account;
        _app = app;
        _msalAccount = msalAccount;

        var graph = new GraphClient(http, GetAccessTokenAsync);
        Capabilities = [new OneDriveCapability(graph), new OutlookMailCapability(graph), new OutlookContactsCapability(graph)];
    }

    public AccountInfo Account { get; }

    // The Calendar capability is added in a later phase.
    public IReadOnlyList<ICapability> Capabilities { get; }

    /// <summary>Gets a Microsoft Graph access token, refreshing it silently when needed.</summary>
    /// <exception cref="ReauthenticationRequiredException">The refresh token expired or consent was revoked.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _app.AcquireTokenSilent(MicrosoftCloudProvider.Scopes, _msalAccount)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new ReauthenticationRequiredException($"Microsoft needs {Account.Email} to sign in again.", ex);
        }
    }
}
