namespace DriveMigrator.Core.Accounts;

/// <summary>Persists the list of connected accounts (not their tokens, which providers keep in the secret store).</summary>
public interface IAccountStore
{
    Task<IReadOnlyList<AccountInfo>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<AccountInfo> accounts, CancellationToken cancellationToken = default);
}
