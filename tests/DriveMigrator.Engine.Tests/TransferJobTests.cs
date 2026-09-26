using DriveMigrator.Core;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.Engine.Tests;

public class TransferJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void CurrentItems_TracksEveryItemInFlight()
    {
        var job = NewJob();
        ITransferObserver observer = job;

        observer.ItemStarted(Item(1, "big.iso"));
        observer.ItemStarted(Item(2, "a.txt"));
        observer.ItemStarted(Item(3, "b.txt"));
        observer.ItemFinished(Item(2, "a.txt"), ItemStatus.Done, null);

        Assert.Equal(["big.iso", "b.txt"], job.CurrentItems);
        Assert.Equal("big.iso", job.CurrentItem);

        // Finishing the item that started last must not leave it on display.
        observer.ItemFinished(Item(3, "b.txt"), ItemStatus.Done, null);
        Assert.Equal(["big.iso"], job.CurrentItems);

        job.SetStatus(JobStatus.Paused);
        Assert.Empty(job.CurrentItems);
    }

    [Fact]
    public async Task BurstOfChanges_AlwaysDeliversTheLastOne()
    {
        var job = NewJob();
        ITransferObserver observer = job;
        var notifications = 0;
        IReadOnlyList<string> lastSeen = [];
        job.Changed += (_, _) =>
        {
            Interlocked.Increment(ref notifications);
            lastSeen = job.CurrentItems;
        };

        // Many starts well inside one throttle interval.
        for (var i = 1; i <= 10; i++)
        {
            observer.ItemStarted(Item(i, $"f{i}.txt"));
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (lastSeen.Count != 10 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(10, lastSeen.Count);
        Assert.InRange(notifications, 1, 3);
    }

    private static TransferJob NewJob()
        => new(
            new JobRecord(1, DateTimeOffset.UtcNow, "t", new AccountRef("p", "a"), new AccountRef("p", "b"), TransferOptions.Default, [], JobStatus.Running),
            new JobCounts(10, 0, 0, 0, 0));

    private static ItemRecord Item(long id, string name)
        => new(id, 1, null, CapabilityKind.Drive, name, 0, new MigrationNode(id.ToString(System.Globalization.CultureInfo.InvariantCulture), name, NodeKind.File), null, ItemStatus.Pending);
}
