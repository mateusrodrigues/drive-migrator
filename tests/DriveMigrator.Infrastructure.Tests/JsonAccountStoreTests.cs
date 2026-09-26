using DriveMigrator.Core.Accounts;

namespace DriveMigrator.Infrastructure.Tests;

public sealed class JsonAccountStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Load_WithoutFile_ReturnsEmpty()
    {
        var store = new JsonAccountStore(new AppPaths(System.IO.Path.Combine(_temp.Path, "missing")));

        Assert.Empty(await store.LoadAsync(Ct));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var paths = new AppPaths(System.IO.Path.Combine(_temp.Path, "data"));
        AccountInfo[] accounts =
        [
            new("google", "123", "Ada", "ada@example.com"),
            new("microsoft", "oid.tid", "Ada (work)", null),
        ];

        await new JsonAccountStore(paths).SaveAsync(accounts, Ct);
        var loaded = await new JsonAccountStore(paths).LoadAsync(Ct);

        Assert.Equal(accounts, loaded);
        Assert.False(File.Exists(paths.AccountsFile + ".tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(paths.DataDirectory));
        }
    }
}
