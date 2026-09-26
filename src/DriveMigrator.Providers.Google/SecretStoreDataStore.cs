using System.Text;
using DriveMigrator.Core.Security;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace DriveMigrator.Providers.Google;

/// <summary>Google <see cref="IDataStore"/> that keeps OAuth tokens in the OS secret store instead of plain files.</summary>
internal sealed class SecretStoreDataStore(ISecretStore secrets, string keyPrefix) : IDataStore
{
    public Task StoreAsync<T>(string key, T value)
        => secrets.SetAsync(Key<T>(key), Encoding.UTF8.GetBytes(NewtonsoftJsonSerializer.Instance.Serialize(value)));

    public Task DeleteAsync<T>(string key) => secrets.RemoveAsync(Key<T>(key));

    public async Task<T> GetAsync<T>(string key)
    {
        var bytes = await secrets.GetAsync(Key<T>(key)).ConfigureAwait(false);
        return bytes is null ? default! : NewtonsoftJsonSerializer.Instance.Deserialize<T>(Encoding.UTF8.GetString(bytes));
    }

    // The secret store cannot enumerate keys; nothing in the auth flow calls this.
    public Task ClearAsync() => throw new NotSupportedException("Delete tokens individually with DeleteAsync.");

    private string Key<T>(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return $"{keyPrefix}{typeof(T).Name}/{key}";
    }
}
