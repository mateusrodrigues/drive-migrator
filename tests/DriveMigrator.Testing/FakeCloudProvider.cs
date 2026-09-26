using System.Collections.Concurrent;
using DriveMigrator.Core;

namespace DriveMigrator.Testing;

/// <summary>
/// An in-memory <see cref="ICloudProvider"/>. Sign-in succeeds immediately and every account
/// gets its own empty stores, which tests can seed through <see cref="FakeAccountSession"/>.
/// </summary>
public sealed class FakeCloudProvider : ICloudProvider
{
    private static readonly CapabilityKind[] AllCapabilities = Enum.GetValues<CapabilityKind>();

    private readonly ConcurrentDictionary<string, FakeAccountSession> _sessions = new();
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

    public Task<IAccountSession> SignInAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAccountSession>(AddAccount());
    }

    public Task<IAccountSession> RestoreSessionAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        return _sessions.TryGetValue(account.AccountId, out var session)
            ? Task.FromResult<IAccountSession>(session)
            : Task.FromException<IAccountSession>(new InvalidOperationException($"Account '{account.AccountId}' is not signed in."));
    }

    public Task SignOutAsync(AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        _sessions.TryRemove(account.AccountId, out _);
        return Task.CompletedTask;
    }
}
