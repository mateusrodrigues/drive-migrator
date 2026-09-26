using DriveMigrator.Core;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Testing;

namespace DriveMigrator.Engine.Tests;

/// <summary>Two fake accounts, a temporary SQLite store and helpers to run jobs synchronously.</summary>
public sealed class EngineFixture : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("drivemigrator-engine-").FullName;

    public EngineFixture()
    {
        Provider = new FakeCloudProvider("fake", "Fake", CapabilityKind.Drive, CapabilityKind.Mail);
        Source = Provider.AddAccount("source@example.com");
        Destination = Provider.AddAccount("dest@example.com");
        StorePath = Path.Combine(_directory, "transfers.db");
        Store = TransferStore.Open(StorePath);
    }

    public FakeCloudProvider Provider { get; }

    public FakeAccountSession Source { get; }

    public FakeAccountSession Destination { get; }

    public string StorePath { get; }

    public TransferStore Store { get; private set; }

    public RecordingObserver Observer { get; } = new();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JobRecord CreateJob(IEnumerable<MigrationNode?> selection, MigrationNode? targetFolder = null, TransferOptions? options = null, CapabilityKind kind = CapabilityKind.Drive)
    {
        var items = selection.Select(n => new NewItem(kind, n, targetFolder, n?.Name ?? kind.ToString())).ToList();
        return Store.CreateJob(
            "test",
            new AccountRef("fake", Source.Account.AccountId),
            new AccountRef("fake", Destination.Account.AccountId),
            options ?? TransferOptions.Default,
            [new TransferTarget(kind, targetFolder)],
            items);
    }

    public Task RunAsync(JobRecord job, CancellationToken? cancellationToken = null, int parallelism = 4)
        => new TransferEngine(Store, parallelism).RunAsync(job, Source, Destination, Observer, cancellationToken ?? Ct);

    /// <summary>Simulates an app restart.</summary>
    public void ReopenStore()
    {
        Store.Dispose();
        Store = TransferStore.Open(StorePath);
    }

    /// <summary>"Folder/Sub/file.txt" paths of everything in a drive, for easy assertions.</summary>
    public static async Task<List<string>> TreeAsync(FakeDriveCapability drive, MigrationNode? root = null, string prefix = "")
    {
        var paths = new List<string>();
        await foreach (var child in drive.GetChildrenAsync(root, Ct))
        {
            var path = prefix + child.Name;
            paths.Add(child.IsContainer ? path + "/" : path);
            if (child.IsContainer)
            {
                paths.AddRange(await TreeAsync(drive, child, path + "/"));
            }
        }

        return [.. paths.Order(StringComparer.Ordinal)];
    }

    public void Dispose()
    {
        Store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}

public sealed class RecordingObserver : ITransferObserver
{
    private readonly Lock _gate = new();

    public List<(string Name, ItemStatus Status, string? Message)> Finished { get; } = [];

    private long _bytes;

    public long Bytes => Interlocked.Read(ref _bytes);

    public void ItemStarted(ItemRecord item)
    {
    }

    public void ItemFinished(ItemRecord item, ItemStatus status, string? message)
    {
        lock (_gate)
        {
            Finished.Add((item.Name, status, message));
        }
    }

    public void ItemsDiscovered(int count)
    {
    }

    public void BytesTransferred(long delta) => Interlocked.Add(ref _bytes, delta);
}
