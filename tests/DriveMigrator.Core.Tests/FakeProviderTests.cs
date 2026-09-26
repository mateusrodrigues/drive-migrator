using System.Text;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Core.Contacts;
using DriveMigrator.Core.Drive;
using DriveMigrator.Core.Mail;
using DriveMigrator.Testing;

namespace DriveMigrator.Core.Tests;

public class FakeProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignIn_CreatesIsolatedAccountsThatCanBeRestored()
    {
        var provider = new FakeCloudProvider();

        var first = await provider.SignInAsync(Ct);
        var second = await provider.SignInAsync(Ct);

        Assert.NotEqual(first.Account.AccountId, second.Account.AccountId);
        Assert.Same(first, await provider.RestoreSessionAsync(first.Account, Ct));

        await provider.SignOutAsync(first.Account, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RestoreSessionAsync(first.Account, Ct));
    }

    [Fact]
    public void Session_ExposesOnlySupportedCapabilities()
    {
        var provider = new FakeCloudProvider("drive-only", "Drive only", CapabilityKind.Drive);

        var session = provider.AddAccount();

        Assert.Equal([CapabilityKind.Drive], session.Capabilities.Select(c => c.Kind));
        Assert.NotNull(session.GetCapability<IDriveCapability>());
        Assert.Null(session.GetCapability<IMailCapability>());
        Assert.Null(session.GetCapability(CapabilityKind.Calendar));
    }

    [Fact]
    public async Task Drive_BrowseAndFindChildren()
    {
        var drive = new FakeCloudProvider().AddAccount().Drive;
        var docs = drive.AddContainer(null, "Documents");
        drive.AddContainer(docs, "Taxes");
        var file = drive.AddFile(docs, "notes.txt", "hi"u8.ToArray(), "text/plain");

        var roots = await drive.GetChildrenAsync(null, Ct).ToListAsync(Ct);
        var children = await drive.GetChildrenAsync(docs, Ct).ToListAsync(Ct);

        Assert.Equal(["Documents"], roots.Select(n => n.Name));
        Assert.Equal(["Taxes", "notes.txt"], children.Select(n => n.Name));
        Assert.True(children[0].IsContainer);
        Assert.Equal(2, file.Size);

        // Exercises the default interface implementation.
        Assert.Equal(file, await ((ICapability)drive).FindChildAsync(docs, "NOTES.TXT", Ct));
        Assert.Null(await ((ICapability)drive).FindChildAsync(docs, "missing", Ct));
    }

    [Fact]
    public async Task Drive_GetChildrenOfFile_Throws()
    {
        var drive = new FakeCloudProvider().AddAccount().Drive;
        var file = drive.AddFile(null, "a.bin", [1]);

        await Assert.ThrowsAsync<ArgumentException>(async () => await drive.GetChildrenAsync(file, Ct).ToListAsync(Ct));
    }

    [Fact]
    public async Task Drive_CopyFileBetweenAccounts_PreservesBytesAndReportsProgress()
    {
        var provider = new FakeCloudProvider();
        var source = provider.AddAccount().Drive;
        var target = provider.AddAccount().Drive;
        var payload = Encoding.UTF8.GetBytes(new string('x', 200_000));
        var file = source.AddFile(null, "big.txt", payload, "text/plain");
        var progress = new List<long>();

        var targetFolder = await target.CreateContainerAsync(null, "Imported", Ct);
        await using var content = await source.OpenReadAsync(file, exportAs: null, Ct);
        var uploaded = await target.UploadAsync(targetFolder, content, DriveUploadOptions.Default, new SyncProgress(progress), Ct);

        Assert.Equal("big.txt", uploaded.Name);
        Assert.Equal(payload, target.GetContent(uploaded));
        Assert.Equal(payload.Length, progress[^1]);
        Assert.Equal(targetFolder, target.GetParent(uploaded));
    }

    [Fact]
    public async Task Drive_UploadWithReplace_OverwritesInPlace()
    {
        var drive = new FakeCloudProvider().AddAccount().Drive;
        var existing = drive.AddFile(null, "a.txt", "old"u8.ToArray());

        await using var content = new DriveFileContent(new MemoryStream("newer"u8.ToArray()), "a.txt", "text/plain");
        var replaced = await drive.UploadAsync(null, content, new DriveUploadOptions { Replace = existing, ConvertToNativeFormat = true }, cancellationToken: Ct);

        Assert.Equal(existing.Id, replaced.Id);
        Assert.Equal("newer"u8.ToArray(), drive.GetContent(replaced));
        Assert.True(drive.WasConvertedToNative(replaced));
        Assert.Equal(1, drive.Count);
    }

    [Fact]
    public async Task Drive_NativeDocument_MustBeExported()
    {
        var drive = new FakeCloudProvider().AddAccount().Drive;
        var docx = new ExportFormat("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx", "Word");
        var pdf = new ExportFormat("application/pdf", ".pdf", "PDF");
        var doc = drive.AddNativeDocument(null, "Report", "application/vnd.google-apps.document", (docx, [1, 2]), (pdf, [3]));

        Assert.True(doc.RequiresExport);
        await Assert.ThrowsAsync<ArgumentException>(() => drive.OpenReadAsync(doc, exportAs: null, Ct));

        await using var exported = await drive.OpenReadAsync(doc, pdf, Ct);
        Assert.Equal("Report.pdf", exported.Name);
        Assert.Equal("application/pdf", exported.MimeType);
        Assert.Equal(1, exported.Length);
    }

    [Fact]
    public async Task Mail_RoundTripsMimeAndFlags()
    {
        var provider = new FakeCloudProvider();
        var source = provider.AddAccount().Mail;
        var target = provider.AddAccount().Mail;
        var inbox = source.AddContainer(null, "Inbox");
        var mime = "Subject: hi\r\n\r\nbody"u8.ToArray();
        var message = source.AddMessage(inbox, "hi", mime, isRead: true, isFlagged: true);

        await using var content = await source.ReadMessageAsync(message, Ct);
        Assert.Equal(["Inbox"], content.Labels);

        var archive = await target.CreateContainerAsync(null, "Archive", Ct);
        var imported = await target.ImportMessageAsync(archive, content, Ct);

        Assert.Equal(mime, target.GetMime(imported));
        await using var reread = await target.ReadMessageAsync(imported, Ct);
        Assert.True(reread.IsRead);
        Assert.True(reread.IsFlagged);
    }

    [Fact]
    public async Task Calendar_DoesNotNestAndRecordsImportOptions()
    {
        var calendar = new FakeCloudProvider().AddAccount().Calendar;
        var work = await calendar.CreateContainerAsync(null, "Work", Ct);

        Assert.False(calendar.SupportsNestedContainers);
        await Assert.ThrowsAsync<NotSupportedException>(() => calendar.CreateContainerAsync(work, "Nested", Ct));

        var evt = new CalendarEvent
        {
            Title = "Standup",
            Start = new EventTime(new DateTime(2026, 10, 1, 9, 0, 0), "Europe/Lisbon"),
            End = new EventTime(new DateTime(2026, 10, 1, 9, 15, 0), "Europe/Lisbon"),
            Recurrence = ["RRULE:FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR"],
            Attendees = [new EventParticipant("a@example.com", "A") { Response = AttendeeResponse.Accepted }],
        };
        var options = new CalendarImportOptions(AttendeeHandling.KeepAttendees);
        var imported = await calendar.ImportEventAsync(work, evt, options, Ct);

        Assert.Equal("Standup", imported.Name);
        Assert.Equal(evt, await calendar.ReadEventAsync(imported, Ct));
        Assert.Equal(options, calendar.GetImportOptions(imported));
    }

    [Fact]
    public async Task Contacts_ImportIntoDefaultList()
    {
        var contacts = new FakeCloudProvider().AddAccount().Contacts;
        var contact = new Contact
        {
            DisplayName = "Ada Lovelace",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            Emails = [new LabeledValue("ada@example.com", "work")],
        };

        var imported = await contacts.ImportContactAsync(folder: null, contact, Ct);

        Assert.Equal(NodeKind.Contact, imported.Kind);
        Assert.Null(contacts.GetParent(imported));
        Assert.Equal(contact, await contacts.ReadContactAsync(imported, Ct));
    }

    /// <summary><see cref="Progress{T}"/> posts asynchronously; tests need synchronous reports.</summary>
    private sealed class SyncProgress(List<long> reports) : IProgress<long>
    {
        public void Report(long value) => reports.Add(value);
    }
}
