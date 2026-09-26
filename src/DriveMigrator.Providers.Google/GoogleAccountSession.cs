using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using Google.Apis.Auth.OAuth2;

namespace DriveMigrator.Providers.Google;

public sealed class GoogleAccountSession : IAccountSession
{
    internal GoogleAccountSession(AccountInfo account, UserCredential credential)
    {
        Account = account;
        Credential = credential;
    }

    public AccountInfo Account { get; }

    /// <summary>Refreshing credential used to construct Google API service clients.</summary>
    public UserCredential Credential { get; }

    // Drive, Gmail, Calendar and People capabilities are added in later phases.
    public IReadOnlyList<ICapability> Capabilities { get; } = [];
}
