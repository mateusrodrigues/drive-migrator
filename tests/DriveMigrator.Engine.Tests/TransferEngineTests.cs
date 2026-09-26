using System.Text;
using DriveMigrator.Core;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Testing;

namespace DriveMigrator.Engine.Tests;

public sealed class TransferEngineTests : IDisposable
{
    private readonly EngineFixture _f = new();

    private FakeDriveCapability Src => _f.Source.Drive;

    private FakeDriveCapability Dst => _f.Destination.Drive;

    private static CancellationToken Ct => EngineFixture.Ct;

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task CopiesSelectedTreeIntoDestinationFolder()
    {
        var docs = Src.AddContainer(null, "Docs");
        Src.AddFile(docs, "a.txt", "aaa"u8.ToArray());
        var sub = Src.AddContainer(docs, "Sub");
        Src.AddFile(sub, "b.txt", "bb"u8.ToArray());
        Src.AddContainer(docs, "Empty");
        var top = Src.AddFile(null, "c.txt", "c"u8.ToArray());
        Src.AddFile(null, "not-selected.txt", [1]);
        var imported = Dst.AddContainer(null, "Imported");

        var job = _f.CreateJob([docs, top], imported);
        await _f.RunAsync(job);

        Assert.Equal(
            ["Imported/", "Imported/Docs/", "Imported/Docs/Empty/", "Imported/Docs/Sub/", "Imported/Docs/Sub/b.txt", "Imported/Docs/a.txt", "Imported/c.txt"],
            await EngineFixture.TreeAsync(Dst));
        var counts = _f.Store.GetCounts(job.Id);
        Assert.Equal((0, 6, 0, 0, 6L), (counts.Pending, counts.Done, counts.Skipped, counts.Failed, counts.Bytes));
        Assert.Equal(6, _f.Observer.Bytes);
    }

    [Fact]
    public async Task WholeCapability_CopiesEveryRootItemToDestinationRoot()
    {
        Src.AddFile(Src.AddContainer(null, "A"), "x.txt", [1]);
        Src.AddFile(null, "y.txt", [2]);

        await _f.RunAsync(_f.CreateJob([null]));

        Assert.Equal(["A/", "A/x.txt", "y.txt"], await EngineFixture.TreeAsync(Dst));
    }

    [Fact]
    public async Task ExistingFolder_IsMergedInto()
    {
        var photos = Src.AddContainer(null, "Photos");
        Src.AddFile(photos, "new.jpg", [1]);
        var existing = Dst.AddContainer(null, "photos");
        Dst.AddFile(existing, "old.jpg", [9]);

        await _f.RunAsync(_f.CreateJob([photos]));

        Assert.Equal(["photos/", "photos/new.jpg", "photos/old.jpg"], await EngineFixture.TreeAsync(Dst));
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip, new[] { "a.txt" }, "old")]
    [InlineData(ConflictPolicy.Overwrite, new[] { "a.txt" }, "new")]
    [InlineData(ConflictPolicy.KeepBoth, new[] { "a (1).txt", "a.txt" }, "old")]
    public async Task ExistingFile_FollowsConflictPolicy(ConflictPolicy policy, string[] expected, string contentOfOriginalName)
    {
        var file = Src.AddFile(null, "a.txt", "new"u8.ToArray());
        Dst.AddFile(null, "A.TXT", "old"u8.ToArray());

        await _f.RunAsync(_f.CreateJob([file], options: new TransferOptions { Conflicts = policy }));

        var names = (await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Select(n => n.Name.ToLowerInvariant()).Order(StringComparer.Ordinal);
        Assert.Equal(expected, names);
        var original = (await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Single(n => n.Name.Equals("a.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(contentOfOriginalName, Encoding.UTF8.GetString(Dst.GetContent(original)));
        if (policy == ConflictPolicy.Skip)
        {
            Assert.Contains(_f.Observer.Finished, f => f.Status == ItemStatus.Skipped && f.Message!.Contains("already exists", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task FolderNamedLikeExistingFile_GetsNewName()
    {
        var docs = Src.AddContainer(null, "Docs");
        Src.AddFile(docs, "a.txt", [1]);
        Dst.AddFile(null, "Docs", [9]);

        await _f.RunAsync(_f.CreateJob([docs]));

        Assert.Equal(["Docs", "Docs (1)/", "Docs (1)/a.txt"], await EngineFixture.TreeAsync(Dst));
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip, new[] { "a.txt" })]
    [InlineData(ConflictPolicy.KeepBoth, new[] { "a (1).txt", "a.txt" })]
    public async Task DuplicateNamesInSource_NeverCollide(ConflictPolicy policy, string[] expected)
    {
        // Google Drive allows several files with the same name in one folder.
        var folder = Src.AddContainer(null, "F");
        Src.AddFile(folder, "a.txt", [1]);
        Src.AddFile(folder, "a.txt", [2]);

        await _f.RunAsync(_f.CreateJob([folder], options: new TransferOptions { Conflicts = policy }));

        var f = (await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Single();
        Assert.Equal(expected, (await Dst.GetChildrenAsync(f, Ct).ToListAsync(Ct)).Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(_f.Observer.Finished, x => x.Status == ItemStatus.Failed);
    }

    [Fact]
    public async Task NativeDocuments_UseChosenFormatOrAreSkipped()
    {
        var docx = new ExportFormat("application/docx", ".docx", "Word");
        var pdf = new ExportFormat("application/pdf", ".pdf", "PDF");
        var xlsx = new ExportFormat("application/xlsx", ".xlsx", "Excel");
        var doc = Src.AddNativeDocument(null, "Report", "google/doc", (docx, [1]), (pdf, [2, 2]));
        var sheet = Src.AddNativeDocument(null, "Budget", "google/sheet", (xlsx, [3]));
        var slides = Src.AddNativeDocument(null, "Deck", "google/slides", (pdf, [4]));
        var options = new TransferOptions
        {
            NativeExports = new Dictionary<string, ExportFormat?> { ["google/doc"] = pdf, ["google/slides"] = null },
        };

        await _f.RunAsync(_f.CreateJob([doc, sheet, slides], options: options));

        var copied = await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct);
        Assert.Equal(["Budget.xlsx", "Report.pdf"], copied.Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.Equal([2, 2], Dst.GetContent(copied.Single(n => n.Name == "Report.pdf")));
        Assert.Contains(_f.Observer.Finished, x => x.Name == "Deck" && x.Status == ItemStatus.Skipped);
    }

    [Fact]
    public async Task NamesAreMadeValidForTheDestination()
    {
        Dst.InvalidNameCharacters = ":?";
        var folder = Src.AddContainer(null, "Q: what?");
        Src.AddFile(folder, "a:b.txt", [1]);

        await _f.RunAsync(_f.CreateJob([folder]));

        Assert.Equal(["Q_ what_/", "Q_ what_/a_b.txt"], await EngineFixture.TreeAsync(Dst));
    }

    [Fact]
    public async Task ConvertOption_OnlyAppliesWhenDestinationSupportsIt()
    {
        var a = Src.AddFile(null, "a.docx", [1]);
        var options = new TransferOptions { ConvertToNativeFormat = true };

        await _f.RunAsync(_f.CreateJob([a], options: options));
        Assert.False(Dst.WasConvertedToNative(Assert.Single(await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct))));

        Dst.CanConvertToNativeFormat = true;
        var b = Src.AddFile(null, "b.docx", [1]);
        await _f.RunAsync(_f.CreateJob([b], options: options));
        Assert.True(Dst.WasConvertedToNative((await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Single(n => n.Name == "b.docx")));
    }

    [Fact]
    public async Task FailedItems_AreRecordedAndCanBeRetried()
    {
        Src.AddFile(null, "ok.txt", [1]);
        Src.AddFile(null, "bad.txt", [2]);
        var fail = true;
        Dst.OnUpload = (name, _) => fail && name == "bad.txt" ? throw new IOException("disk on fire") : Task.CompletedTask;
        var job = _f.CreateJob([null]);

        await _f.RunAsync(job);

        var failed = Assert.Single(_f.Store.LoadItems(job.Id, ItemStatus.Failed));
        Assert.Equal(("bad.txt", "disk on fire"), (failed.Name, failed.Error));
        Assert.Equal(1, _f.Store.GetCounts(job.Id).Failed);

        fail = false;
        Assert.Equal(1, _f.Store.ResetFailed(job.Id));
        await _f.RunAsync(job);

        Assert.Equal(["bad.txt", "ok.txt"], await EngineFixture.TreeAsync(Dst));
        Assert.Equal(0, _f.Store.GetCounts(job.Id).Failed);
    }

    [Fact]
    public async Task CancelledJob_ResumesAfterRestartWithoutDuplicates()
    {
        var folder = Src.AddContainer(null, "Many");
        for (var i = 0; i < 20; i++)
        {
            Src.AddFile(folder, $"f{i:00}.txt", [(byte)i]);
        }

        using var cts = new CancellationTokenSource();
        var uploads = 0;
        Dst.OnUpload = async (_, ct) =>
        {
            if (Interlocked.Increment(ref uploads) == 8)
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
        };
        var job = _f.CreateJob([folder]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _f.RunAsync(job, cts.Token));
        var partial = _f.Store.GetCounts(job.Id);
        Assert.True(partial.Pending > 0);

        _f.ReopenStore();
        Dst.OnUpload = null;
        await _f.RunAsync(job);

        var files = await Dst.GetChildrenAsync((await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Single(), Ct).ToListAsync(Ct);
        Assert.Equal(20, files.Count);
        Assert.Equal(20, files.Select(f => f.Name).Distinct().Count());
        Assert.Equal((0, 21), (_f.Store.GetCounts(job.Id).Pending, _f.Store.GetCounts(job.Id).Done));
    }

    [Fact]
    public async Task RunningFinishedJob_DoesNothing()
    {
        var file = Src.AddFile(null, "a.txt", [1]);
        var job = _f.CreateJob([file]);
        await _f.RunAsync(job);

        await _f.RunAsync(job);

        Assert.Equal(1, Dst.UploadCount);
    }
}
