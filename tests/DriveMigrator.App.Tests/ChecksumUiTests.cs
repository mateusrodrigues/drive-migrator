using Avalonia.Headless.XUnit;
using DriveMigrator.App.ViewModels;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Engine;
using DriveMigrator.Testing;

namespace DriveMigrator.App.Tests;

public sealed class ChecksumUiTests : IDisposable
{
    private readonly FakeCloudProvider _provider = new("google", "Google", CapabilityKind.Drive);
    private readonly InMemoryAccountStore _store = new();
    private readonly AccountManager _accounts;
    private readonly FakeDialogService _dialogs = new();
    private readonly TempTransfers _transfers;
    private readonly FakeAccountSession _source;
    private readonly FakeAccountSession _target;

    public ChecksumUiTests()
    {
        _accounts = new AccountManager(new ProviderRegistry([_provider]), _store);
        _transfers = new TempTransfers(_accounts);
        _source = _provider.AddAccount("me@gmail.com");
        _target = _provider.AddAccount("work@example.com");
        _store.Accounts = [_source.Account, _target.Account];
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _transfers.Dispose();
        _accounts.Dispose();
    }

    [AvaloniaFact]
    public void Options_VerifyByDefault_CompareExistingOnlyWhenSkipping()
    {
        var vm = new TransferOptionsViewModel("summary", Request());

        Assert.True(vm.VerifyHashes);
        Assert.False(vm.CompareExisting);
        Assert.True(vm.ToOptions().VerifyHashes);

        vm.CompareExisting = true;
        Assert.True(vm.ToOptions().CompareExisting);

        vm.ConflictKeepBoth = true;
        Assert.False(vm.CanCompareExisting);
        Assert.False(vm.ToOptions().CompareExisting);
    }

    [AvaloniaFact]
    public async Task DamagedCopies_AreListed_AndCopiedAgainAfterConfirming()
    {
        await _accounts.LoadAsync(Ct);
        _source.Drive.AddFile(null, "a.bin", [1, 2, 3]);
        _target.Drive.CorruptUpload = bytes => [.. bytes, 0];
        var job = await RunAsync(TransferOptions.Default);
        using var vm = new TransferJobViewModel(job, _transfers.Manager, _dialogs);

        Assert.True(vm.ShowMismatches);
        Assert.Equal("a.bin", Assert.Single(vm.Mismatches).Name);
        Assert.Empty(vm.Failures);
        Assert.False(vm.CanRetryFailed);

        _dialogs.ConfirmResult = false;
        await vm.CopyMismatchesAgainCommand.ExecuteAsync(null);
        Assert.Equal(JobStatus.CompletedWithErrors, job.Status);

        _target.Drive.CorruptUpload = null;
        _dialogs.ConfirmResult = true;
        await vm.CopyMismatchesAgainCommand.ExecuteAsync(null);
        await WaitForAsync(job);
        vm.Refresh();

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.False(vm.ShowMismatches);
        Assert.Contains("overwritten", _dialogs.Confirmations[^1], StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], _target.Drive.GetContent(Assert.Single(await _target.Drive.GetChildrenAsync(null, Ct).ToListAsync(Ct))));
    }

    [AvaloniaFact]
    public async Task DifferentExistingFiles_AreListedForTheUserToDecide()
    {
        await _accounts.LoadAsync(Ct);
        _source.Drive.AddFile(null, "a.txt", [1]);
        _source.Drive.AddFile(null, "b.txt", [1]);
        _target.Drive.AddFile(null, "a.txt", [2]);
        _target.Drive.AddFile(null, "b.txt", [1]);
        var job = await RunAsync(new TransferOptions { CompareExisting = true });
        using var vm = new TransferJobViewModel(job, _transfers.Manager, _dialogs);

        Assert.True(vm.ShowDifferences);
        Assert.False(vm.ShowMismatches);
        Assert.Equal("a.txt", Assert.Single(vm.Differences).Name);
        Assert.Contains("1 file is different", vm.DifferencesSummary, StringComparison.Ordinal);

        vm.KeepBothDifferencesCommand.Execute(null);
        await WaitForAsync(job);
        vm.Refresh();

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.False(vm.ShowDifferences);
        Assert.Equal(["a (1).txt", "a.txt", "b.txt"], (await _target.Drive.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Select(n => n.Name).Order(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public async Task DifferentExistingFiles_CanBeLeftAsTheyAre()
    {
        await _accounts.LoadAsync(Ct);
        _source.Drive.AddFile(null, "a.txt", [1]);
        _target.Drive.AddFile(null, "a.txt", [2]);
        var job = await RunAsync(new TransferOptions { CompareExisting = true });
        using var vm = new TransferJobViewModel(job, _transfers.Manager, _dialogs);

        vm.KeepDestinationDifferencesCommand.Execute(null);
        vm.Refresh();

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.False(vm.ShowDifferences);
        Assert.Contains("1 skipped", vm.Details, StringComparison.Ordinal);
    }

    private TransferRequest Request(TransferOptions? options = null)
        => new(_source, _target, [new(CapabilityKind.Drive, null)], [new(CapabilityKind.Drive, null)]) { Options = options ?? TransferOptions.Default };

    private async Task<TransferJob> RunAsync(TransferOptions options)
    {
        var job = _transfers.Manager.Start(Request(options), "copy");
        await WaitForAsync(job);
        return job;
    }

    private static async Task WaitForAsync(TransferJob job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (job.Status == JobStatus.Running)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the transfer.");
            await Task.Delay(10, Ct);
        }
    }
}
