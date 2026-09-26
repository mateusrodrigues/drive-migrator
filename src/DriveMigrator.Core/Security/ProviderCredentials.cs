using System.Text.Json;

namespace DriveMigrator.Core.Security;

/// <summary>
/// One value a provider needs from the user before it can sign anyone in, such as an OAuth client ID.
/// Users bring their own app registrations, so these are entered in Settings.
/// </summary>
public sealed record CredentialField(string Key, string Label, bool IsSecret = false, bool IsRequired = true)
{
    /// <summary>Hint shown under the input, e.g. where to find the value.</summary>
    public string? Help { get; init; }

    /// <summary>Value used when the user leaves an optional field empty.</summary>
    public string? DefaultValue { get; init; }
}

/// <summary>Reads and writes each provider's <see cref="CredentialField"/> values, kept in the secret store.</summary>
public sealed class ProviderCredentialStore(ISecretStore secrets)
{
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var bytes = await secrets.GetAsync(Key(providerId), cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? [];
    }

    public Task SetAsync(string providerId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        var trimmed = values
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        return secrets.SetAsync(Key(providerId), JsonSerializer.SerializeToUtf8Bytes(trimmed), cancellationToken);
    }

    /// <summary>
    /// Returns the configured values with defaults applied, or throws <see cref="ProviderNotConfiguredException"/>
    /// naming the first missing required field.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetRequiredAsync(ICloudProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var values = await GetAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>();
        foreach (var field in provider.CredentialFields)
        {
            if (values.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                result[field.Key] = value;
            }
            else if (field.DefaultValue is not null)
            {
                result[field.Key] = field.DefaultValue;
            }
            else if (field.IsRequired)
            {
                throw new Accounts.ProviderNotConfiguredException(
                    $"{provider.DisplayName} needs a {field.Label} before accounts can be connected. Enter it in Settings → Credentials.");
            }
        }

        return result;
    }

    public async Task<bool> IsConfiguredAsync(ICloudProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var values = await GetAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        return provider.CredentialFields
            .Where(f => f.IsRequired && f.DefaultValue is null)
            .All(f => values.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v));
    }

    private static string Key(string providerId) => $"{providerId}/credentials";
}
