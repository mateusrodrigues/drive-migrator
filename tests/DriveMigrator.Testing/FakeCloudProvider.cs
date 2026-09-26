using System.Collections.Concurrent;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;

namespace DriveMigrator.Testing;

/// <summary>
/// An in-memory <see cref="ICloudProvider"/>. Sign-in succeeds immediately and every account
/// gets its own empty stores, which tests can seed through <see cref="FakeAccountSession"/>.
/// The session dictionary plays the role of the token cache.
/// </summary>
public sealed class FakeCloudProvider : ICloudProvider
{
    private static readonly CapabilityKind[] AllCapabilities = Enum.GetValues<CapabilityKind>();

    private readonly ConcurrentDictionary<string, FakeAccountSession> _sessions = new();
    private readonly ConcurrentDictionary<string, bool> _expired = new();
    private int _accountCounter;

    public FakeCloudProvider()
        : this("fake", "Fake Cloud")
    {
    }

    public FakeCloudProvider(string id, string displayName, params CapabilityKind[] supportedCapabilities)
    {
        Id = id;
        DisplayName = displayName;
        SupportedCapabilities = (supportedCapabilities.Length == 0 ? AllCapabilities : supportedCapabilities).ToHashSet();
    }

    public string Id { get; }

    public string DisplayName { get; }

    public IReadOnlySet<CapabilityKind> SupportedCapabilities { get; }

    public IReadOnlyList<CredentialField> CredentialFields { get; init; } = [];

    /// <summary>
    /// Email of the account the next <see cref="SignInAsync"/> signs in as, simulating the user picking an account
    /// in the browser. When null, sign-in uses the login hint or creates a new account.
    /// </summary>
    public string? NextSignInEmail { get; set; }

    /// <summary>Called at the start of every sign-in; throw from it to simulate a cancelled or failed sign-in.</summary>
    public Action? OnSignIn { get; set; }

    public IReadOnlyCollection<FakeAccountSession> Sessions => [.. _sessions.Values];

    /// <summary>Creates a signed-in account without going through <see cref="SignInAsync"/>.</summary>
    public FakeAccountSession AddAccount(string? email = null)
    {
        var number = Interlocked.Increment(ref _accountCounter);
        email ??= $"user{number}@{Id}.example";
        var session = new FakeAccountSession(new AccountInfo(Id, $"{Id}-account-{number}", email, email), SupportedCapabilities);
        _sessions[session.Account.AccountId] = session;
        return session;
    }

    /// <summary>Makes the account's cached credentials invalid until it signs in again.</summary>
    public void ExpireCredentials(AccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _expired[account.AccountId] = true;
    }

    public Task<IAccountSession> SignInAsync(string? loginHint = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnSignIn?.Invoke();

        var email = NextSignInEmail ?? loginHint;
        NextSignInEmail = null;
        var session = _sessions.Values.FirstOrDefault(s => email is not null && s.Account.Email == email) ?? AddAccount(email);
        _expired.TryRemove(session.Account.AccountId, out _);
        return Task.FromResult<IAccountSession>(session);
    }

    public Task<IAccountSession> RestoreSessionAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (_expired.ContainsKey(account.AccountId))
        {
            return Task.FromException<IAccountSession>(new ReauthenticationRequiredException($"The session for {account.Email} has expired."));
        }

        return _sessions.TryGetValue(account.AccountId, out var session)
            ? Task.FromResult<IAccountSession>(session)
            : Task.FromException<IAccountSession>(new ReauthenticationRequiredException($"{account.Email} is not signed in."));
    }

    public Task SignOutAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        _sessions.TryRemove(account.AccountId, out _);
        _expired.TryRemove(account.AccountId, out _);
        return Task.CompletedTask;
    }
}
