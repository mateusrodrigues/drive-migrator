namespace DriveMigrator.Infrastructure.Tests;

/// <summary>
/// Runs against the real OS credential store (Keychain, libsecret, DPAPI), or the file fallback where none is
/// available, so it exercises whichever path this machine takes.
/// </summary>
public sealed class OsSecretStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _key = $"tests/{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SetGetRemove_RoundTrips()
    {
        var store = OsSecretStore.Create(new AppPaths(_temp.Path));
        TestContext.Current.SendDiagnosticMessage($"OS secret store protected: {store.IsProtected}");
        try
        {
            Assert.Null(await store.GetAsync(_key, Ct));

            await store.SetAsync(_key, [1, 2, 3], Ct);
            Assert.Equal([1, 2, 3], await store.GetAsync(_key, Ct));

            await store.SetAsync(_key, [4], Ct);
            Assert.Equal([4], await store.GetAsync(_key, Ct));
        }
        finally
        {
            await store.RemoveAsync(_key, Ct);
        }

        Assert.Null(await store.GetAsync(_key, Ct));
    }

    [Fact]
    public void Create_MakesDataDirectoriesPrivate()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions only.");
            return;
        }

        var paths = new AppPaths(Path.Combine(_temp.Path, "data"));

        OsSecretStore.Create(paths);

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(Private, File.GetUnixFileMode(paths.DataDirectory));
        Assert.Equal(Private, File.GetUnixFileMode(paths.SecretsDirectory));
    }

    [Fact]
    public async Task DifferentKeys_AreIndependent()
    {
        var store = OsSecretStore.Create(new AppPaths(_temp.Path));
        var other = _key + "-other";
        try
        {
            await store.SetAsync(_key, [1], Ct);
            await store.SetAsync(other, [2], Ct);

            Assert.Equal([1], await store.GetAsync(_key, Ct));
            Assert.Equal([2], await store.GetAsync(other, Ct));
        }
        finally
        {
            await store.RemoveAsync(_key, Ct);
            await store.RemoveAsync(other, Ct);
        }
    }
}
