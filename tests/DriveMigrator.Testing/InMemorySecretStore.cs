using System.Collections.Concurrent;
using DriveMigrator.Core.Security;

namespace DriveMigrator.Testing;

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> _secrets = new();

    public bool IsProtected => true;

    public IReadOnlyCollection<string> Keys => [.. _secrets.Keys];

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.TryGetValue(key, out var value) ? (byte[])value.Clone() : null);

    public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        _secrets[key] = (byte[])value.Clone();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _secrets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
