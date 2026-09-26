using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;

namespace DriveMigrator.Core;

/// <summary>
/// Entry point for one cloud service (Google, Microsoft...). Adding a new service means
/// implementing this interface and registering it with <see cref="ServiceCollectionExtensions.AddCloudProvider{TProvider}"/>.
/// </summary>
public interface ICloudProvider
{
    /// <summary>Stable identifier, persisted with accounts (e.g. "google", "microsoft").</summary>
    string Id { get; }

    string DisplayName { get; }

    IReadOnlySet<CapabilityKind> SupportedCapabilities { get; }

    /// <summary>Values the user must enter in Settings (e.g. OAuth client ID) before accounts can be connected.</summary>
    IReadOnlyList<CredentialField> CredentialFields { get; }

    /// <summary>
    /// Runs the interactive sign-in flow (usually in the system browser) and returns a session for the account
    /// the user signed in with. <paramref name="loginHint"/> pre-fills the account when re-authorizing.
    /// </summary>
    /// <exception cref="ProviderNotConfiguredException">Required <see cref="CredentialFields"/> are missing.</exception>
    Task<IAccountSession> SignInAsync(string? loginHint = null, CancellationToken cancellationToken = default);

    /// <summary>Re-opens a session for a previously connected account using cached credentials, without user interaction.</summary>
    /// <exception cref="ReauthenticationRequiredException">The cached credentials are missing, expired or revoked.</exception>
    Task<IAccountSession> RestoreSessionAsync(AccountInfo account, CancellationToken cancellationToken = default);

    /// <summary>Forgets cached credentials for the account.</summary>
    Task SignOutAsync(AccountInfo account, CancellationToken cancellationToken = default);
}
