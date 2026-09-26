using DriveMigrator.Providers.Google;
using DriveMigrator.Testing;
using Google.Apis.Auth.OAuth2.Responses;

namespace DriveMigrator.Providers.Tests;

public class SecretStoreDataStoreTests
{
    [Fact]
    public async Task StoresTokensUnderPrefixedKeys()
    {
        var secrets = new InMemorySecretStore();
        var store = new SecretStoreDataStore(secrets, "google/client/");
        var token = new TokenResponse { AccessToken = "a", RefreshToken = "r", IdToken = "i" };

        Assert.Null(await store.GetAsync<TokenResponse>("user"));

        await store.StoreAsync("user", token);
        var loaded = await store.GetAsync<TokenResponse>("user");

        Assert.Equal(("a", "r", "i"), (loaded.AccessToken, loaded.RefreshToken, loaded.IdToken));
        Assert.Equal(["google/client/TokenResponse/user"], secrets.Keys);

        await store.DeleteAsync<TokenResponse>("user");
        Assert.Empty(secrets.Keys);
    }
}
