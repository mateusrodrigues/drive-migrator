using DriveMigrator.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DriveMigrator.Core.Tests;

public class ProviderRegistryTests
{
    [Fact]
    public void AddCloudProvider_RegistersProvidersInRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICloudProvider>(new FakeCloudProvider("other", "Other"));
        services.AddCloudProvider<FakeCloudProvider>();

        var registry = services.BuildServiceProvider().GetRequiredService<IProviderRegistry>();

        Assert.Equal(["other", "fake"], registry.Providers.Select(p => p.Id));
        Assert.Equal("Other", registry.GetProvider("OTHER").DisplayName);
    }

    [Fact]
    public void Constructor_RejectsDuplicateIds()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ProviderRegistry([new FakeCloudProvider(), new FakeCloudProvider()]));
        Assert.Contains("'fake'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetProvider_UnknownId_Throws()
    {
        var registry = new ProviderRegistry([new FakeCloudProvider()]);
        Assert.Throws<KeyNotFoundException>(() => registry.GetProvider("nope"));
    }
}
