using DriveMigrator.Core;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.Engine.Tests;

public sealed class TransferStoreTests : IDisposable
{
    private readonly EngineFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void JobsAndOptionsSurviveReopening()
    {
        var pdf = new ExportFormat("application/pdf", ".pdf", "PDF");
        var folder = new MigrationNode("t1", "Target", NodeKind.Folder);
        var options = new TransferOptions
        {
            Conflicts = ConflictPolicy.KeepBoth,
            ConvertToNativeFormat = true,
            NativeExports = new Dictionary<string, ExportFormat?> { ["doc"] = pdf, ["form"] = null },
            Calendar = new CalendarImportOptions(AttendeeHandling.KeepAttendees),
        };
        var source = new MigrationNode("s1", "Report", NodeKind.File) { ExportFormats = [pdf], Size = 5 };
        var job = _f.Store.CreateJob("Google Drive → OneDrive", new AccountRef("google", "a"), new AccountRef("microsoft", "b"), options,
            [new TransferTarget(CapabilityKind.Drive, folder)], [new NewItem(CapabilityKind.Drive, source, folder, "Report")]);

        _f.ReopenStore();
        var loaded = Assert.Single(_f.Store.LoadJobs());

        Assert.Equal((job.Id, "Google Drive → OneDrive", JobStatus.Running), (loaded.Id, loaded.Title, loaded.Status));
        Assert.Equal(ConflictPolicy.KeepBoth, loaded.Options.Conflicts);
        Assert.True(loaded.Options.ConvertToNativeFormat);
        Assert.Equal(pdf, loaded.Options.NativeExports["doc"]);
        Assert.Null(loaded.Options.NativeExports["form"]);
        Assert.Equal(AttendeeHandling.KeepAttendees, loaded.Options.Calendar.AttendeeHandling);
        Assert.Equal(folder, Assert.Single(loaded.Targets).Container);

        var item = Assert.Single(_f.Store.LoadPendingItems(job.Id));
        Assert.Equal(("Report", "s1", ".pdf", "t1"), (item.Name, item.Source!.Id, item.Source.ExportFormats[0].FileExtension, item.TargetParent!.Id));
    }

    [Fact]
    public void OlderDatabaseWithoutNewColumns_IsUpgraded()
    {
        var path = Path.Combine(Path.GetDirectoryName(_f.StorePath)!, "old.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY AUTOINCREMENT, job_id INTEGER NOT NULL, parent_id INTEGER, kind INTEGER NOT NULL, name TEXT NOT NULL, depth INTEGER NOT NULL, source TEXT, target_parent TEXT, target TEXT, status INTEGER NOT NULL, error TEXT, bytes INTEGER NOT NULL DEFAULT 0);";
            create.ExecuteNonQuery();
        }

        using var store = TransferStore.Open(path);
        var job = store.CreateJob("t", new AccountRef("a", "1"), new AccountRef("b", "2"), TransferOptions.Default, [], [new NewItem(CapabilityKind.Drive, null, null, "Drive")]);
        var item = Assert.Single(store.LoadPendingItems(job.Id));
        store.MarkFinished(item.Id, ItemStatus.Failed, error: "e", details: "full");

        var failed = Assert.Single(store.LoadItems(job.Id, ItemStatus.Failed));
        Assert.Equal(("full", FailureKind.Error, (ConflictPolicy?)null), (failed.Details, failed.Failure, failed.Resolution));
    }

    [Fact]
    public void DeleteJob_RemovesItems()
    {
        var job = _f.CreateJob([null]);

        _f.Store.DeleteJob(job.Id);

        Assert.Empty(_f.Store.LoadJobs());
        Assert.Empty(_f.Store.LoadPendingItems(job.Id));
    }
}
