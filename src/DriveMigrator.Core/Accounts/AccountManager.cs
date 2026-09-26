namespace DriveMigrator.Core.Accounts;

/// <summary>
/// Owns the connected accounts: restores them at start-up, signs new ones in, re-authorizes and removes them,
/// and keeps the persisted list in sync. Operations are serialized.
/// </summary>
public sealed class AccountManager(IProviderRegistry providers, IAccountStore store) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ConnectedAccount> _accounts = [];

    public event EventHandler? AccountsChanged;

    public IReadOnlyList<ConnectedAccount> Accounts => _accounts;

    /// <summary>Loads persisted accounts and tries to restore a session for each one.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var restored = await Task.WhenAll(stored.Select(a => RestoreAsync(a, cancellationToken))).ConfigureAwait(false);
            _accounts = [.. restored];
        }
        finally
        {
            _gate.Release();
        }

        OnAccountsChanged();
    }

    /// <summary>Runs the provider's interactive sign-in and adds (or refreshes) the resulting account.</summary>
    public async Task<ConnectedAccount> AddAccountAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = providers.GetProvider(providerId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ConnectedAccount connected;
        try
        {
            var session = await provider.SignInAsync(loginHint: null, cancellationToken).ConfigureAwait(false);
            connected = new ConnectedAccount(session.Account, AccountStatus.Connected, session);
            await UpsertAsync(connected, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        OnAccountsChanged();
        return connected;
    }

    /// <summary>Signs an existing account in again, e.g. after its refresh token expired or was revoked.</summary>
    public async Task<ConnectedAccount> ReauthorizeAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var provider = providers.GetProvider(account.ProviderId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ConnectedAccount connected;
        try
        {
            var session = await provider.SignInAsync(account.Email, cancellationToken).ConfigureAwait(false);
            if (session.Account.AccountId != account.AccountId)
            {
                // Don't leave credentials behind for an account the user never asked to add.
                if (Find(session.Account) < 0)
                {
                    await provider.SignOutAsync(session.Account, cancellationToken).ConfigureAwait(false);
                }

                throw new AccountMismatchException(
                    $"You signed in as {Describe(session.Account)}, but {Describe(account)} was being re-authorized.");
            }

            connected = new ConnectedAccount(session.Account, AccountStatus.Connected, session);
            await UpsertAsync(connected, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        OnAccountsChanged();
        return connected;
    }

    /// <summary>Signs the account out, deleting its cached credentials, and forgets it.</summary>
    public async Task RemoveAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (providers.Providers.Any(p => string.Equals(p.Id, account.ProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                await providers.GetProvider(account.ProviderId).SignOutAsync(account, cancellationToken).ConfigureAwait(false);
            }

            var index = Find(account);
            if (index >= 0)
            {
                _accounts = [.. _accounts.Where((_, i) => i != index)];
                await store.SaveAsync([.. _accounts.Select(a => a.Info)], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        OnAccountsChanged();
    }

    private async Task<ConnectedAccount> RestoreAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        ICloudProvider provider;
        try
        {
            provider = providers.GetProvider(account.ProviderId);
        }
        catch (KeyNotFoundException)
        {
            return new ConnectedAccount(account, AccountStatus.Error, Error: $"The '{account.ProviderId}' provider is not available in this version.");
        }

        try
        {
            var session = await provider.RestoreSessionAsync(account, cancellationToken).ConfigureAwait(false);
            return new ConnectedAccount(session.Account, AccountStatus.Connected, session);
        }
        catch (Exception ex) when (ex is ReauthenticationRequiredException or ProviderNotConfiguredException)
        {
            return new ConnectedAccount(account, AccountStatus.NeedsReauthorization, Error: ex.Message);
        }
#pragma warning disable CA1031 // One broken account must not stop the others from loading; the error is shown to the user.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return new ConnectedAccount(account, AccountStatus.Error, Error: ex.Message);
        }
    }

    private async Task UpsertAsync(ConnectedAccount connected, CancellationToken cancellationToken)
    {
        var index = Find(connected.Info);
        _accounts = index >= 0
            ? [.. _accounts.Select((a, i) => i == index ? connected : a)]
            : [.. _accounts, connected];
        await store.SaveAsync([.. _accounts.Select(a => a.Info)], cancellationToken).ConfigureAwait(false);
    }

    private int Find(AccountInfo account)
        => _accounts.FindIndex(a =>
            string.Equals(a.Info.ProviderId, account.ProviderId, StringComparison.OrdinalIgnoreCase)
            && a.Info.AccountId == account.AccountId);

    public void Dispose() => _gate.Dispose();

    private static string Describe(AccountInfo account) => account.Email ?? account.DisplayName;

    private void OnAccountsChanged() => AccountsChanged?.Invoke(this, EventArgs.Empty);
}
