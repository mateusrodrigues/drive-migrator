namespace DriveMigrator.Core;

public interface IProviderRegistry
{
    IReadOnlyList<ICloudProvider> Providers { get; }

    ICloudProvider GetProvider(string providerId);
}

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, ICloudProvider> _byId;

    public ProviderRegistry(IEnumerable<ICloudProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        Providers = [.. providers];
        _byId = new Dictionary<string, ICloudProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Providers)
        {
            if (!_byId.TryAdd(provider.Id, provider))
            {
                throw new InvalidOperationException($"More than one cloud provider is registered with id '{provider.Id}'.");
            }
        }
    }

    public IReadOnlyList<ICloudProvider> Providers { get; }

    public ICloudProvider GetProvider(string providerId)
        => _byId.TryGetValue(providerId, out var provider)
            ? provider
            : throw new KeyNotFoundException($"No cloud provider is registered with id '{providerId}'.");
}
