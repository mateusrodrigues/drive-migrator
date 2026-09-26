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
    public void DeleteJob_RemovesItems()
    {
        var job = _f.CreateJob([null]);

        _f.Store.DeleteJob(job.Id);

        Assert.Empty(_f.Store.LoadJobs());
        Assert.Empty(_f.Store.LoadPendingItems(job.Id));
    }
}
