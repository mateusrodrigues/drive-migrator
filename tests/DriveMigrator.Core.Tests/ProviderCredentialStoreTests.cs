using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;
using DriveMigrator.Testing;

namespace DriveMigrator.Core.Tests;

public class ProviderCredentialStoreTests
{
    private readonly InMemorySecretStore _secrets = new();
    private readonly ProviderCredentialStore _store;
    private readonly FakeCloudProvider _provider = new("ms", "Microsoft")
    {
        CredentialFields =
        [
            new CredentialField("clientId", "Client ID"),
            new CredentialField("tenant", "Tenant", IsRequired: false) { DefaultValue = "common" },
            new CredentialField("note", "Note", IsRequired: false),
        ],
    };

    public ProviderCredentialStoreTests() => _store = new ProviderCredentialStore(_secrets);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unconfigured_ProviderReportsNotConfigured()
    {
        Assert.False(await _store.IsConfiguredAsync(_provider, Ct));
        var ex = await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => _store.GetRequiredAsync(_provider, Ct));
        Assert.Contains("Client ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_TrimsAndDropsBlankValues_AndAppliesDefaults()
    {
        await _store.SetAsync("ms", new Dictionary<string, string> { ["clientId"] = "  abc  ", ["tenant"] = " ", ["note"] = "" }, Ct);

        Assert.True(await _store.IsConfiguredAsync(_provider, Ct));
        Assert.Equal(new Dictionary<string, string> { ["clientId"] = "abc" }, await _store.GetAsync("ms", Ct));
        Assert.Equal(new Dictionary<string, string> { ["clientId"] = "abc", ["tenant"] = "common" }, await _store.GetRequiredAsync(_provider, Ct));
        Assert.Equal(["ms/credentials"], _secrets.Keys);
    }
}
