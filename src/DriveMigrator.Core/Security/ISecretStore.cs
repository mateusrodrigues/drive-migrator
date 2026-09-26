namespace DriveMigrator.Core.Security;

/// <summary>
/// Stores small secrets (token caches, client secrets) in the operating system's credential store.
/// Keys are free-form strings such as "microsoft/token-cache/&lt;client id&gt;".
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// False when no OS credential store was available and secrets fall back to a file
    /// readable only by the current user. The UI warns about this.
    /// </summary>
    bool IsProtected { get; }

    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
