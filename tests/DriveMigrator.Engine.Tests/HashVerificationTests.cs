using System.Text;
using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Testing;

namespace DriveMigrator.Engine.Tests;

public sealed class HashVerificationTests : IDisposable
{
    private static readonly TransferOptions Compare = new() { CompareExisting = true };

    private readonly EngineFixture _f = new();

    private FakeDriveCapability Src => _f.Source.Drive;

    private FakeDriveCapability Dst => _f.Destination.Drive;

    private static CancellationToken Ct => EngineFixture.Ct;

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Copy_IsVerifiedAcrossDifferentAlgorithms()
    {
        // Google reports MD5, OneDrive for Business only QuickXorHash: both are checked against what was sent.
        Src.ReportHashes = h => new FileHashes(Md5: h.Md5);
        Dst.ReportHashes = h => new FileHashes(QuickXor: h.QuickXor);
        var file = Src.AddFile(null, "a.bin", Encoding.UTF8.GetBytes(new string('x', 100_000)));

        var job = _f.CreateJob([file]);
        await _f.RunAsync(job);

        Assert.Equal(ItemStatus.Done, Assert.Single(_f.Observer.Finished).Status);
    }

    [Fact]
    public async Task DamagedUpload_FailsAsMismatch_AndLeavesTheCopy()
    {
        Dst.CorruptUpload = bytes => [.. bytes, 0];
        var file = Src.AddFile(null, "a.bin", [1, 2, 3]);

        var job = _f.CreateJob([file]);
        await _f.RunAsync(job);

        var failed = Assert.Single(_f.Store.LoadItems(job.Id, ItemStatus.Failed));
        Assert.Equal(FailureKind.ChecksumMismatch, failed.Failure);
        Assert.Contains("uploading", failed.Error, StringComparison.Ordinal);
        Assert.Contains("SHA-256 expected", failed.Details, StringComparison.Ordinal);
        var copy = Assert.Single(await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct));
        Assert.Equal(copy, failed.Target);
    }

    [Fact]
    public async Task SourceChecksumNotMatchingContent_FailsAsMismatch()
    {
        var file = Src.AddFile(null, "a.bin", [1, 2, 3], reportedHashes: new FileHashes(Md5: "00000000000000000000000000000000"));

        var job = _f.CreateJob([file]);
        await _f.RunAsync(job);

        var failed = Assert.Single(_f.Store.LoadItems(job.Id, ItemStatus.Failed));
        Assert.Equal(FailureKind.ChecksumMismatch, failed.Failure);
        Assert.Contains("downloading", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerificationOff_DoesNotCheck()
    {
        Dst.CorruptUpload = bytes => [.. bytes, 0];
        var file = Src.AddFile(null, "a.bin", [1, 2, 3]);

        await _f.RunAsync(_f.CreateJob([file], options: new TransferOptions { VerifyHashes = false }));

        Assert.Equal(ItemStatus.Done, Assert.Single(_f.Observer.Finished).Status);
    }

    [Fact]
    public async Task DestinationWithoutChecksums_CopiesUnchecked()
    {
        Dst.ReportHashes = _ => null;
        Dst.CorruptUpload = bytes => [.. bytes, 0];
        var file = Src.AddFile(null, "a.bin", [1, 2, 3]);

        await _f.RunAsync(_f.CreateJob([file]));

        Assert.Equal(ItemStatus.Done, Assert.Single(_f.Observer.Finished).Status);
    }

    [Fact]
    public async Task ChecksumMissingFromUploadResponse_IsLookedUpOnce()
    {
        Dst.OmitHashesFromUploadResponse = true;
        Dst.CorruptUpload = bytes => [.. bytes, 0];
        var file = Src.AddFile(null, "a.bin", [1, 2, 3]);

        await _f.RunAsync(_f.CreateJob([file]));

        Assert.Equal(ItemStatus.Failed, Assert.Single(_f.Observer.Finished).Status);
    }

    [Fact]
    public async Task ExportedDocument_IsCheckedAgainstTheDestination()
    {
        var docx = new ExportFormat("application/docx", ".docx", "Word");
        var doc = Src.AddNativeDocument(null, "Report", "google/doc", (docx, [1, 2]));
        Dst.CorruptUpload = bytes => [.. bytes, 0];

        await _f.RunAsync(_f.CreateJob([doc]));

        Assert.Equal(ItemStatus.Failed, Assert.Single(_f.Observer.Finished).Status);
    }

    [Fact]
    public async Task ConvertedFile_HasNoChecksum_AndIsCopiedUnchecked()
    {
        Dst.CanConvertToNativeFormat = true;
        Dst.CorruptUpload = bytes => [.. bytes, 0];
        var file = Src.AddFile(null, "a.docx", [1, 2, 3]);

        await _f.RunAsync(_f.CreateJob([file], options: new TransferOptions { ConvertToNativeFormat = true }));

        Assert.Equal(ItemStatus.Done, Assert.Single(_f.Observer.Finished).Status);
    }

    [Fact]
    public async Task CompareExisting_SkipsIdentical_FailsDifferent_CopiesNew_InsideMergedFolders()
    {
        var folder = Src.AddContainer(null, "Docs");
        Src.AddFile(folder, "same.txt", "same"u8.ToArray());
        Src.AddFile(folder, "changed.txt", "new"u8.ToArray());
        Src.AddFile(folder, "new.txt", "n"u8.ToArray());
        var existing = Dst.AddContainer(null, "Docs");
        Dst.AddFile(existing, "same.txt", "same"u8.ToArray());
        var changed = Dst.AddFile(existing, "changed.txt", "old"u8.ToArray());

        var job = _f.CreateJob([folder], options: Compare);
        await _f.RunAsync(job);

        var results = _f.Observer.Finished.ToDictionary(f => f.Name);
        Assert.Equal(ItemStatus.Skipped, results["same.txt"].Status);
        Assert.Contains("identical", results["same.txt"].Message, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Failed, results["changed.txt"].Status);
        Assert.Equal(ItemStatus.Done, results["new.txt"].Status);
        var failed = Assert.Single(_f.Store.LoadItems(job.Id, ItemStatus.Failed));
        Assert.Equal((FailureKind.DiffersFromExisting, changed), (failed.Failure, failed.Target));
        Assert.Equal("old", Encoding.UTF8.GetString(Dst.GetContent(changed)));
    }

    [Fact]
    public async Task CompareExisting_WithoutCommonChecksum_Skips()
    {
        Src.ReportHashes = h => new FileHashes(Md5: h.Md5);
        Dst.ReportHashes = h => new FileHashes(QuickXor: h.QuickXor);
        var file = Src.AddFile(null, "a.txt", "new"u8.ToArray());
        Dst.AddFile(null, "a.txt", "old"u8.ToArray());

        await _f.RunAsync(_f.CreateJob([file], options: Compare));

        var result = Assert.Single(_f.Observer.Finished);
        Assert.Equal(ItemStatus.Skipped, result.Status);
        Assert.Contains("no checksum", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareExisting_OnlyAppliesWhenSkipping()
    {
        var file = Src.AddFile(null, "a.txt", "new"u8.ToArray());
        var existing = Dst.AddFile(null, "a.txt", "old"u8.ToArray());

        await _f.RunAsync(_f.CreateJob([file], options: Compare with { Conflicts = ConflictPolicy.Overwrite }));

        Assert.Equal(ItemStatus.Done, Assert.Single(_f.Observer.Finished).Status);
        Assert.Equal("new", Encoding.UTF8.GetString(Dst.GetContent(existing)));
    }

    [Fact]
    public async Task DamagedCopy_CopiedAgain_OverwritesThatCopy()
    {
        // Keep both put the damaged copy under a new name; copying again must replace it, not the original "a.txt".
        var original = Dst.AddFile(null, "a.txt", "old"u8.ToArray());
        var file = Src.AddFile(null, "a.txt", "new"u8.ToArray());
        Dst.CorruptUpload = bytes => [.. bytes, 0];
        var job = _f.CreateJob([file], options: new TransferOptions { Conflicts = ConflictPolicy.KeepBoth });
        await _f.RunAsync(job);
        Assert.Single(_f.Store.LoadItems(job.Id, ItemStatus.Failed));

        Dst.CorruptUpload = null;
        Assert.Equal(1, _f.Store.ResetMismatched(job.Id, FailureKind.ChecksumMismatch, ConflictPolicy.Overwrite));
        _f.ReopenStore();
        await _f.RunAsync(job);

        Assert.Empty(_f.Store.LoadItems(job.Id, ItemStatus.Failed));
        var files = await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct);
        Assert.Equal(["a (1).txt", "a.txt"], files.Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.Equal("new", Encoding.UTF8.GetString(Dst.GetContent(files.Single(n => n.Name == "a (1).txt"))));
        Assert.Equal("old", Encoding.UTF8.GetString(Dst.GetContent(original)));
    }

    [Fact]
    public async Task DamagedCopy_DeletedMeanwhile_IsCopiedFresh()
    {
        var file = Src.AddFile(null, "a.txt", "new"u8.ToArray());
        Dst.CorruptUpload = bytes => [.. bytes, 0];
        var job = _f.CreateJob([file]);
        await _f.RunAsync(job);
        Dst.Remove(Assert.Single(_f.Store.LoadItems(job.Id, ItemStatus.Failed)).Target!);

        Dst.CorruptUpload = null;
        _f.Store.ResetMismatched(job.Id, FailureKind.ChecksumMismatch, ConflictPolicy.Overwrite);
        await _f.RunAsync(job);

        var copy = Assert.Single(await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct));
        Assert.Equal(("a.txt", "new"), (copy.Name, Encoding.UTF8.GetString(Dst.GetContent(copy))));
    }

    [Fact]
    public async Task DamagedCopy_StillDamaged_FailsAgain()
    {
        var file = Src.AddFile(null, "a.txt", "new"u8.ToArray());
        Dst.CorruptUpload = bytes => [.. bytes, 0];
        var job = _f.CreateJob([file]);
        await _f.RunAsync(job);

        _f.Store.ResetMismatched(job.Id, FailureKind.ChecksumMismatch, ConflictPolicy.Overwrite);
        await _f.RunAsync(job);

        Assert.Equal(FailureKind.ChecksumMismatch, Assert.Single(_f.Store.LoadItems(job.Id, ItemStatus.Failed)).Failure);
        Assert.Single(await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(ConflictPolicy.Overwrite, new[] { "a.txt" }, "new")]
    [InlineData(ConflictPolicy.KeepBoth, new[] { "a (1).txt", "a.txt" }, "old")]
    public async Task DifferentExistingFile_ResolvedWithTheChosenPolicy(ConflictPolicy resolution, string[] expected, string contentOfOriginalName)
    {
        var file = Src.AddFile(null, "a.txt", "new"u8.ToArray());
        var existing = Dst.AddFile(null, "a.txt", "old"u8.ToArray());
        var job = _f.CreateJob([file], options: Compare);
        await _f.RunAsync(job);

        _f.Store.ResetMismatched(job.Id, FailureKind.DiffersFromExisting, resolution);
        await _f.RunAsync(job);

        Assert.Empty(_f.Store.LoadItems(job.Id, ItemStatus.Failed));
        Assert.Equal(expected, (await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.Equal(contentOfOriginalName, Encoding.UTF8.GetString(Dst.GetContent(existing)));
    }

    [Fact]
    public async Task RetryFailed_LeavesMismatchesAlone_AndSkipMismatchedSettlesThem()
    {
        var file = Src.AddFile(null, "a.txt", "new"u8.ToArray());
        Dst.AddFile(null, "a.txt", "old"u8.ToArray());
        var job = _f.CreateJob([file], options: Compare);
        await _f.RunAsync(job);

        Assert.Equal(0, _f.Store.ResetFailed(job.Id));
        Assert.Equal(1, _f.Store.SkipMismatched(job.Id, FailureKind.DiffersFromExisting, "kept"));

        var counts = _f.Store.GetCounts(job.Id);
        Assert.Equal((0, 1), (counts.Failed, counts.Skipped));
        Assert.Equal("kept", Assert.Single(_f.Store.LoadItems(job.Id, ItemStatus.Skipped)).Error);
    }

    [Fact]
    public async Task HashingStream_HashesEachByteOnce_WhenAReaderSeeksBack()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        using var hashes = new FileHashAccumulator();
        await using var stream = new HashingStream(new MemoryStream(data), hashes);
        var buffer = new byte[300];

        await stream.ReadExactlyAsync(buffer, Ct);
        stream.Position = 100; // A chunk is re-sent after a failed request.
        while (await stream.ReadAsync(buffer, Ct) > 0)
        {
        }

        Assert.Equal(FileHashAccumulator.Compute(data), stream.GetHashes(data.Length));
    }

    [Fact]
    public async Task HashingStream_SkippedOrUnreadContent_GivesNoHashes()
    {
        var data = new byte[1000];
        using (var hashes = new FileHashAccumulator())
        await using (var skipped = new HashingStream(new MemoryStream(data), hashes))
        {
            skipped.Position = 10;
            await skipped.CopyToAsync(Stream.Null, Ct);
            Assert.Null(skipped.GetHashes(data.Length));
        }

        using var partialHashes = new FileHashAccumulator();
        await using var partial = new HashingStream(new MemoryStream(data), partialHashes);
        await partial.ReadExactlyAsync(new byte[10], Ct);
        Assert.Null(partial.GetHashes(data.Length));
    }
}
