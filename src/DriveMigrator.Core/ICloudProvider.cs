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

    /// <summary>Runs the interactive sign-in flow and returns a session for the new account.</summary>
    Task<IAccountSession> SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-opens a session for a previously connected account using cached credentials.</summary>
    Task<IAccountSession> RestoreSessionAsync(AccountInfo account, CancellationToken cancellationToken = default);

    /// <summary>Forgets cached credentials for the account.</summary>
    Task SignOutAsync(AccountInfo account, CancellationToken cancellationToken = default);
}
