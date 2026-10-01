using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Testing;

namespace DriveMigrator.Engine.Tests;

public sealed class TransferManagerTests : IDisposable
{
    private readonly EngineFixture _f = new();
    private readonly InMemoryAccountStore _accountStore = new();
    private readonly AccountManager _accounts;

    public TransferManagerTests()
    {
        _accounts = new AccountManager(new ProviderRegistry([_f.Provider]), _accountStore);
        _accountStore.Accounts = [_f.Source.Account, _f.Destination.Account];
    }

    private static CancellationToken Ct => EngineFixture.Ct;

    public void Dispose()
    {
        _accounts.Dispose();
        _f.Dispose();
    }

    [Fact]
    public async Task Start_RunsToCompletion()
    {
        await _accounts.LoadAsync(Ct);
        using var manager = new TransferManager(_f.Store, _accounts);
        var file = _f.Source.Drive.AddFile(null, "a.txt", [1, 2, 3]);

        var job = manager.Start(Request([file]), "copy");
        await WaitForAsync(job, s => s != JobStatus.Running);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal((1, 3L), (job.Counts.Done, job.Counts.Bytes));
        Assert.Equal([job], manager.Jobs);
    }

    [Fact]
    public async Task PauseAndResume()
    {
        await _accounts.LoadAsync(Ct);
        using var manager = new TransferManager(_f.Store, _accounts, parallelism: 1);
        for (var i = 0; i < 5; i++)
        {
            _f.Source.Drive.AddFile(null, $"{i}.txt", [1]);
        }

        var gate = new SemaphoreSlim(0);
        _f.Destination.Drive.OnUpload = (_, ct) => gate.WaitAsync(ct);
        var job = manager.Start(Request([null]), "copy");
        await WaitForAsync(job, _ => _f.Destination.Drive.Count == 0 && job.CurrentItem is not null);

        manager.Pause(job);
        await WaitForAsync(job, s => s == JobStatus.Paused);
        Assert.True(job.Counts.Pending > 0);

        _f.Destination.Drive.OnUpload = null;
        manager.Resume(job);
        await WaitForAsync(job, s => s == JobStatus.Completed);
        Assert.Equal(5, _f.Destination.Drive.Count);
    }

    [Fact]
    public async Task Load_BringsInterruptedJobsBackPaused_AndResumeNeedsAccounts()
    {
        var file = _f.Source.Drive.AddFile(null, "a.txt", [1]);
        _f.CreateJob([file]);
        _f.ReopenStore();
        using var manager = new TransferManager(_f.Store, _accounts);

        manager.Load();
        var job = Assert.Single(manager.Jobs);
        Assert.Equal(JobStatus.Paused, job.Status);

        // Accounts not restored yet.
        manager.Resume(job);
        Assert.Contains("Reconnect", job.Problem, StringComparison.Ordinal);

        await _accounts.LoadAsync(Ct);
        manager.Resume(job);
        await WaitForAsync(job, s => s == JobStatus.Completed);
        Assert.Equal(1, _f.Destination.Drive.Count);
    }

    [Fact]
    public async Task RetryFailed_AndRemove()
    {
        await _accounts.LoadAsync(Ct);
        using var manager = new TransferManager(_f.Store, _accounts);
        _f.Source.Drive.AddFile(null, "a.txt", [1]);
        var fail = true;
        _f.Destination.Drive.OnUpload = (_, _) => fail ? throw new IOException("nope") : Task.CompletedTask;

        var job = manager.Start(Request([null]), "copy");
        await WaitForAsync(job, s => s == JobStatus.CompletedWithErrors);
        Assert.Equal("nope", Assert.Single(manager.GetFailures(job)).Error);

        fail = false;
        manager.RetryFailed(job);
        await WaitForAsync(job, s => s == JobStatus.Completed);

        await manager.RemoveAsync(job);
        Assert.Empty(manager.Jobs);
        Assert.Empty(_f.Store.LoadJobs());
        Assert.Equal(1, _f.Destination.Drive.Count);
    }

    [Fact]
    public async Task RetryMismatched_OverwritesTheDamagedCopy()
    {
        await _accounts.LoadAsync(Ct);
        using var manager = new TransferManager(_f.Store, _accounts);
        _f.Source.Drive.AddFile(null, "a.txt", [1, 2, 3]);
        _f.Destination.Drive.CorruptUpload = bytes => [.. bytes, 0];

        var job = manager.Start(Request([null]), "copy");
        await WaitForAsync(job, s => s == JobStatus.CompletedWithErrors);
        Assert.Equal(FailureKind.ChecksumMismatch, Assert.Single(manager.GetFailures(job)).Failure);

        // "Retry failed" is for ordinary errors: it doesn't touch the damaged copy.
        manager.RetryFailed(job);
        await WaitForAsync(job, s => s == JobStatus.CompletedWithErrors);

        _f.Destination.Drive.CorruptUpload = null;
        manager.RetryMismatched(job, FailureKind.ChecksumMismatch, ConflictPolicy.Overwrite);
        await WaitForAsync(job, s => s == JobStatus.Completed);

        var copy = Assert.Single(await _f.Destination.Drive.GetChildrenAsync(null, Ct).ToListAsync(Ct));
        Assert.Equal([1, 2, 3], _f.Destination.Drive.GetContent(copy));
    }

    [Fact]
    public async Task KeepDestinationVersions_SettlesDifferencesAsSkipped()
    {
        await _accounts.LoadAsync(Ct);
        using var manager = new TransferManager(_f.Store, _accounts);
        _f.Source.Drive.AddFile(null, "a.txt", [1]);
        _f.Destination.Drive.AddFile(null, "a.txt", [2]);

        var job = manager.Start(Request([null]) with { Options = new TransferOptions { CompareExisting = true } }, "copy");
        await WaitForAsync(job, s => s == JobStatus.CompletedWithErrors);

        manager.KeepDestinationVersions(job, FailureKind.DiffersFromExisting);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal((0, 1), (job.Counts.Failed, job.Counts.Skipped));
        Assert.Equal(JobStatus.Completed, Assert.Single(_f.Store.LoadJobs()).Status);
    }

    private TransferRequest Request(IReadOnlyList<MigrationNode?> nodes)
        => new(_f.Source, _f.Destination, [.. nodes.Select(n => new TransferItem(CapabilityKind.Drive, n))], [new TransferTarget(CapabilityKind.Drive, null)]);

    private static async Task WaitForAsync(TransferJob job, Func<JobStatus, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition(job.Status))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out; job is {job.Status}.");
            await Task.Delay(10, Ct);
        }
    }
}
