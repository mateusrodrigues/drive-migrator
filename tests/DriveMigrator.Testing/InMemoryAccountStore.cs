using DriveMigrator.Core.Accounts;

namespace DriveMigrator.Testing;

public sealed class InMemoryAccountStore : IAccountStore
{
    public IReadOnlyList<AccountInfo> Accounts { get; set; } = [];

    public int SaveCount { get; private set; }

    public Task<IReadOnlyList<AccountInfo>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Accounts);

    public Task SaveAsync(IReadOnlyList<AccountInfo> accounts, CancellationToken cancellationToken = default)
    {
        Accounts = [.. accounts];
        SaveCount++;
        return Task.CompletedTask;
    }
}
