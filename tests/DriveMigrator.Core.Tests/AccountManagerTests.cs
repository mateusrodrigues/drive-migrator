using DriveMigrator.Core.Accounts;
using DriveMigrator.Testing;

namespace DriveMigrator.Core.Tests;

public sealed class AccountManagerTests : IDisposable
{
    private readonly FakeCloudProvider _google = new("google", "Google");
    private readonly FakeCloudProvider _microsoft = new("microsoft", "Microsoft");
    private readonly InMemoryAccountStore _store = new();
    private readonly AccountManager _manager;
    private int _changes;

    public AccountManagerTests()
    {
        _manager = new AccountManager(new ProviderRegistry([_google, _microsoft]), _store);
        _manager.AccountsChanged += (_, _) => _changes++;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _manager.Dispose();

    [Fact]
    public async Task AddAccount_SignsInAndPersists()
    {
        var added = await _manager.AddAccountAsync("google", Ct);

        Assert.Equal(AccountStatus.Connected, added.Status);
        Assert.NotNull(added.Session);
        Assert.Equal([added], _manager.Accounts);
        Assert.Equal([added.Info], _store.Accounts);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task AddAccount_SameAccountTwice_DoesNotDuplicate()
    {
        _google.NextSignInEmail = "me@example.com";
        await _manager.AddAccountAsync("google", Ct);
        _google.NextSignInEmail = "me@example.com";
        await _manager.AddAccountAsync("google", Ct);

        Assert.Single(_manager.Accounts);
        Assert.Single(_store.Accounts);
    }

    [Fact]
    public async Task AddAccount_MultipleAccountsOfSameProvider()
    {
        await _manager.AddAccountAsync("microsoft", Ct);
        await _manager.AddAccountAsync("microsoft", Ct);
        await _manager.AddAccountAsync("google", Ct);

        Assert.Equal(3, _manager.Accounts.Count);
    }

    [Fact]
    public async Task AddAccount_WhenSignInFails_LeavesStateUnchanged()
    {
        _google.OnSignIn = () => throw new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => _manager.AddAccountAsync("google", Ct));

        Assert.Empty(_manager.Accounts);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task Load_RestoresSessionsAndFlagsProblemAccounts()
    {
        var ok = _google.AddAccount();
        var expired = _microsoft.AddAccount();
        _microsoft.ExpireCredentials(expired.Account);
        var orphan = new AccountInfo("dropbox", "x", "Old", "old@example.com");
        _store.Accounts = [ok.Account, expired.Account, orphan];

        await _manager.LoadAsync(Ct);

        Assert.Collection(
            _manager.Accounts,
            a => Assert.Equal((AccountStatus.Connected, (IAccountSession?)ok), (a.Status, a.Session)),
            a => Assert.Equal((AccountStatus.NeedsReauthorization, (IAccountSession?)null), (a.Status, a.Session)),
            a =>
            {
                Assert.Equal(AccountStatus.Error, a.Status);
                Assert.Contains("dropbox", a.Error, StringComparison.Ordinal);
            });
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task Reauthorize_RestoresConnection()
    {
        var session = _microsoft.AddAccount("work@contoso.com");
        _microsoft.ExpireCredentials(session.Account);
        _store.Accounts = [session.Account];
        await _manager.LoadAsync(Ct);

        var result = await _manager.ReauthorizeAsync(session.Account, Ct);

        Assert.Equal(AccountStatus.Connected, result.Status);
        Assert.Equal([result], _manager.Accounts);
    }

    [Fact]
    public async Task Reauthorize_AsDifferentAccount_ThrowsAndSignsOutTheStranger()
    {
        var session = _microsoft.AddAccount("work@contoso.com");
        _store.Accounts = [session.Account];
        await _manager.LoadAsync(Ct);
        _microsoft.NextSignInEmail = "someone-else@contoso.com";

        var ex = await Assert.ThrowsAsync<AccountMismatchException>(() => _manager.ReauthorizeAsync(session.Account, Ct));

        Assert.Contains("someone-else@contoso.com", ex.Message, StringComparison.Ordinal);
        Assert.Equal([session.Account], _manager.Accounts.Select(a => a.Info));
        Assert.DoesNotContain(_microsoft.Sessions, s => s.Account.Email == "someone-else@contoso.com");
    }

    [Fact]
    public async Task Remove_SignsOutAndForgets()
    {
        var added = await _manager.AddAccountAsync("google", Ct);

        await _manager.RemoveAsync(added.Info, Ct);

        Assert.Empty(_manager.Accounts);
        Assert.Empty(_store.Accounts);
        Assert.Empty(_google.Sessions);
    }

    [Fact]
    public async Task Remove_AccountOfUnknownProvider_StillForgetsIt()
    {
        var orphan = new AccountInfo("dropbox", "x", "Old", "old@example.com");
        _store.Accounts = [orphan];
        await _manager.LoadAsync(Ct);

        await _manager.RemoveAsync(orphan, Ct);

        Assert.Empty(_manager.Accounts);
    }
}
