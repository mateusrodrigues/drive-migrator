using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DriveMigrator.Core.Security;
using Microsoft.Identity.Client.Extensions.Msal;

namespace DriveMigrator.Infrastructure;

/// <summary>
/// <see cref="ISecretStore"/> backed by the OS credential store via MSAL's cross-platform storage:
/// DPAPI-encrypted files on Windows, the login Keychain on macOS, and the Secret Service (libsecret,
/// e.g. GNOME Keyring or KWallet) on Linux. Where no keyring is usable, secrets fall back to files
/// readable only by the current user and <see cref="IsProtected"/> is false.
/// </summary>
public sealed class OsSecretStore : ISecretStore
{
    private const string ServiceName = "DriveMigrator";
    private const string LinuxSchema = "com.drivemigrator.secrets";
    private const string LinuxKeyAttribute = "DriveMigrator.Key";

    private static readonly TraceSource Trace = new("DriveMigrator.SecretStore");

    private readonly string _directory;
    private readonly Lock _gate = new();

    private OsSecretStore(string directory, bool isProtected)
    {
        _directory = directory;
        IsProtected = isProtected;
    }

    public bool IsProtected { get; }

    /// <summary>Probes the OS credential store once and picks protected or fallback storage for this run.</summary>
    public static OsSecretStore Create(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        AppPaths.EnsurePrivateDirectory(paths.DataDirectory);
        AppPaths.EnsurePrivateDirectory(paths.SecretsDirectory);

        var protectedStore = new OsSecretStore(paths.SecretsDirectory, isProtected: true);
        try
        {
            protectedStore.CreateStorage("probe").VerifyPersistence();
            return protectedStore;
        }
        catch (MsalCachePersistenceException ex)
        {
            Trace.TraceEvent(TraceEventType.Warning, 0, "OS credential store unavailable, falling back to user-only files: {0}", ex.Message);
            return new OsSecretStore(paths.SecretsDirectory, isProtected: false);
        }
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Run(key, storage =>
        {
            var data = storage.ReadData();
            return data is { Length: > 0 } ? data : null;
        }, cancellationToken);

    public Task SetAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Run(key, storage =>
        {
            storage.WriteData(value);
            return true;
        }, cancellationToken);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Run(key, storage =>
        {
            storage.Clear(ignoreExceptions: false);
            return true;
        }, cancellationToken);

    // Keyring calls are synchronous (D-Bus, Keychain), so keep them off the UI thread.
    private Task<T> Run<T>(string key, Func<Storage, T> action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Task.Run(
            () =>
            {
                lock (_gate)
                {
                    return action(CreateStorage(key));
                }
            },
            cancellationToken);
    }

    private Storage CreateStorage(string key)
    {
        // The file name only has to be stable and filesystem-safe; the key itself goes into the keyring attributes.
        var fileName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".bin";
        var builder = new StorageCreationPropertiesBuilder(fileName, _directory);

        builder = IsProtected
            ? builder
                .WithMacKeyChain(ServiceName, key)
                .WithLinuxKeyring(
                    LinuxSchema,
                    MsalCacheHelper.LinuxKeyRingDefaultCollection,
                    $"Drive Migrator: {key}",
                    new KeyValuePair<string, string>(LinuxKeyAttribute, key),
                    new KeyValuePair<string, string>("Application", ServiceName))
            : builder.WithUnprotectedFile();

        return Storage.Create(builder.Build(), Trace);
    }
}
