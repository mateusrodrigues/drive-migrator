using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;
using DriveMigrator.Providers.Google;
using DriveMigrator.Providers.Microsoft;
using DriveMigrator.Testing;

namespace DriveMigrator.Providers.Tests;

/// <summary>Offline checks of provider behaviour around credentials and cached sign-ins; no network or browser needed.</summary>
public sealed class ProviderConfigurationTests : IDisposable
{
    private readonly InMemorySecretStore _secrets = new();
    private readonly ProviderCredentialStore _credentials;
    private readonly MicrosoftCloudProvider _microsoft;
    private readonly GoogleCloudProvider _google;

    public ProviderConfigurationTests()
    {
        _credentials = new ProviderCredentialStore(_secrets);
        _microsoft = new MicrosoftCloudProvider(_secrets, _credentials);
        _google = new GoogleCloudProvider(_secrets, _credentials);
    }

    public static TheoryData<string> ProviderIds => [MicrosoftCloudProvider.ProviderId, GoogleCloudProvider.ProviderId];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _microsoft.Dispose();

    [Theory]
    [MemberData(nameof(ProviderIds))]
    public async Task SignIn_WithoutCredentials_ThrowsNotConfigured(string providerId)
    {
        await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => Provider(providerId).SignInAsync(cancellationToken: Ct));
    }

    [Theory]
    [MemberData(nameof(ProviderIds))]
    public async Task Restore_WithoutCachedToken_RequiresReauthentication(string providerId)
    {
        await ConfigureAsync(providerId);
        var account = new AccountInfo(providerId, "unknown-account", "Someone", "someone@example.com");

        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => Provider(providerId).RestoreSessionAsync(account, Ct));
    }

    [Theory]
    [MemberData(nameof(ProviderIds))]
    public async Task SignOut_UnknownAccountOrUnconfigured_IsNoOp(string providerId)
    {
        var account = new AccountInfo(providerId, "unknown-account", "Someone", null);

        await Provider(providerId).SignOutAsync(account, Ct);
        await ConfigureAsync(providerId);
        await Provider(providerId).SignOutAsync(account, Ct);
    }

    [Fact]
    public void Microsoft_TenantIsOptionalWithCommonDefault()
    {
        var tenant = Assert.Single(_microsoft.CredentialFields, f => f.Key == "tenant");
        Assert.False(tenant.IsRequired);
        Assert.Equal("common", tenant.DefaultValue);
    }

    private ICloudProvider Provider(string id) => id == MicrosoftCloudProvider.ProviderId ? _microsoft : _google;

    private Task ConfigureAsync(string providerId) => _credentials.SetAsync(providerId, providerId == MicrosoftCloudProvider.ProviderId
        ? new Dictionary<string, string> { ["clientId"] = "00000000-0000-0000-0000-000000000001" }
        : new Dictionary<string, string> { ["clientId"] = "test.apps.googleusercontent.com", ["clientSecret"] = "secret" }, Ct);
}
