using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Providers.Google.Drive;
using DriveMigrator.Providers.Google.Mail;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;

namespace DriveMigrator.Providers.Google;

public sealed class GoogleAccountSession : IAccountSession
{
    internal GoogleAccountSession(AccountInfo account, UserCredential credential)
    {
        Account = account;
        Credential = credential;

        var services = new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "DriveMigrator" };
        Capabilities = [new GoogleDriveCapability(new DriveService(services)), new GmailCapability(new GmailService(services))];
    }

    public AccountInfo Account { get; }

    /// <summary>Refreshing credential used to construct Google API service clients.</summary>
    public UserCredential Credential { get; }

    // Calendar and People capabilities are added in later phases.
    public IReadOnlyList<ICapability> Capabilities { get; }
}
