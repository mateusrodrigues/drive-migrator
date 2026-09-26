using DriveMigrator.Core;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Testing;

namespace DriveMigrator.Engine.Tests;

public sealed class MailTransferTests : IDisposable
{
    private readonly EngineFixture _f = new();

    private FakeMailCapability Src => _f.Source.Mail;

    private FakeMailCapability Dst => _f.Destination.Mail;

    private static CancellationToken Ct => EngineFixture.Ct;

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task WellKnownFoldersMapToTheirCounterparts_OthersAreCreated()
    {
        var sent = Src.AddSpecialFolder(MailFolderRole.Sent, "SENT");
        Src.AddMessage(sent, "Hello", "a"u8.ToArray(), messageId: "<1@x>");
        var work = Src.AddContainer(null, "Work");
        var project = Src.AddContainer(work, "Project");
        Src.AddMessage(project, "Plan", "b"u8.ToArray(), messageId: "<2@x>");
        var dstSent = Dst.AddSpecialFolder(MailFolderRole.Sent, "Sent Items");

        await _f.RunAsync(_f.CreateJob([sent, work], kind: CapabilityKind.Mail));

        var sentMessages = await Dst.GetChildrenAsync(dstSent, Ct).ToListAsync(Ct);
        Assert.Equal("<1@x>", Dst.GetMessageId(Assert.Single(sentMessages)));
        var roots = await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct);
        Assert.Equal(["Sent Items", "Work"], roots.Select(r => r.Name));
        var dstProject = Assert.Single(await Dst.GetChildrenAsync(roots[1], Ct).ToListAsync(Ct));
        Assert.Equal("Project", dstProject.Name);
        Assert.Single(await Dst.GetChildrenAsync(dstProject, Ct).ToListAsync(Ct));
    }

    [Fact]
    public async Task RoleMappingOnlyAppliesAtTheTopLevel()
    {
        var inbox = Src.AddSpecialFolder(MailFolderRole.Inbox, "INBOX");
        Src.AddMessage(inbox, "Hi", [1]);
        Dst.AddSpecialFolder(MailFolderRole.Inbox, "Inbox");
        var archive = Dst.AddContainer(null, "Old account");

        await _f.RunAsync(_f.CreateJob([inbox], archive, kind: CapabilityKind.Mail));

        var created = Assert.Single(await Dst.GetChildrenAsync(archive, Ct).ToListAsync(Ct));
        Assert.Equal("INBOX", created.Name);
        Assert.Single(await Dst.GetChildrenAsync(created, Ct).ToListAsync(Ct));
    }

    [Fact]
    public async Task ReadAndFlaggedStateAreKept()
    {
        var folder = Src.AddContainer(null, "F");
        Src.AddMessage(folder, "read+flagged", [1], isRead: true, isFlagged: true);
        Src.AddMessage(folder, "unread", [2]);

        await _f.RunAsync(_f.CreateJob([folder], kind: CapabilityKind.Mail));

        var copied = await Dst.GetChildrenAsync((await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Single(), Ct).ToListAsync(Ct);
        var bytes = copied.ToDictionary(m => Dst.GetMime(m)[0]);
        Assert.True(Dst.IsRead(bytes[1]) && Dst.IsFlagged(bytes[1]));
        Assert.False(Dst.IsRead(bytes[2]) || Dst.IsFlagged(bytes[2]));
    }

    [Fact]
    public async Task RerunningSkipsMessagesAlreadyThere_UnlessTurnedOff()
    {
        var folder = Src.AddContainer(null, "F");
        Src.AddMessage(folder, "one", [1], messageId: "<1@x>");
        Src.AddMessage(folder, "no id", [2]);

        await _f.RunAsync(_f.CreateJob([folder], kind: CapabilityKind.Mail));
        await _f.RunAsync(_f.CreateJob([folder], kind: CapabilityKind.Mail));

        var dstFolder = (await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Single();
        // The message with an id was skipped the second time; the one without can't be recognised.
        Assert.Equal(3, (await Dst.GetChildrenAsync(dstFolder, Ct).ToListAsync(Ct)).Count);
        Assert.Contains(_f.Observer.Finished, f => f.Status == ItemStatus.Skipped && f.Name == "one");

        await _f.RunAsync(_f.CreateJob([folder], options: new TransferOptions { SkipExistingMessages = false }, kind: CapabilityKind.Mail));
        Assert.Equal(5, (await Dst.GetChildrenAsync(dstFolder, Ct).ToListAsync(Ct)).Count);
    }

    [Fact]
    public async Task LooseMessagesGoToTheInbox()
    {
        var folder = Src.AddContainer(null, "F");
        var message = Src.AddMessage(folder, "one", [1]);
        var inbox = Dst.AddSpecialFolder(MailFolderRole.Inbox, "Inbox");

        await _f.RunAsync(_f.CreateJob([message], kind: CapabilityKind.Mail));

        Assert.Single(await Dst.GetChildrenAsync(inbox, Ct).ToListAsync(Ct));
    }

    [Fact]
    public async Task LooseMessagesWithoutAnInbox_FailClearly()
    {
        var message = Src.AddMessage(Src.AddContainer(null, "F"), "one", [1]);
        var job = _f.CreateJob([message], kind: CapabilityKind.Mail);

        await _f.RunAsync(job);

        Assert.Contains("no Inbox", Assert.Single(_f.Store.LoadItems(job.Id, ItemStatus.Failed)).Error, StringComparison.Ordinal);
    }
}
