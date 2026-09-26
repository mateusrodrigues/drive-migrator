using DriveMigrator.Core;
using DriveMigrator.Core.Contacts;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Testing;

namespace DriveMigrator.Engine.Tests;

public sealed class ContactsTransferTests : IDisposable
{
    private readonly EngineFixture _f = new();

    private FakeContactsCapability Src => _f.Source.Contacts;

    private FakeContactsCapability Dst => _f.Destination.Contacts;

    private static CancellationToken Ct => EngineFixture.Ct;

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task DefaultListMapsToDefault_GroupsBecomeFolders()
    {
        var all = Src.AddSpecialFolder(ContainerRole.DefaultContacts, "All contacts");
        Src.AddContact(all, Person("Ada Lovelace", "ada@x"));
        var family = Src.AddContainer(null, "Family");
        Src.AddContact(family, Person("Grace Hopper", "grace@x"));
        var dstDefault = Dst.AddSpecialFolder(ContainerRole.DefaultContacts, "Contacts");

        await _f.RunAsync(_f.CreateJob([all, family], kind: CapabilityKind.Contacts));

        Assert.Equal(["Ada Lovelace"], (await Dst.GetChildrenAsync(dstDefault, Ct).ToListAsync(Ct)).Select(c => c.Name));
        var roots = await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct);
        Assert.Equal(["Contacts", "Family"], roots.Select(r => r.Name));
        var grace = Assert.Single(await Dst.GetChildrenAsync(roots[1], Ct).ToListAsync(Ct));
        Assert.Equal("grace@x", (await Dst.ReadContactAsync(grace, Ct)).Emails[0].Value);
    }

    [Fact]
    public async Task LooseContactsGoToTheDefaultList_AndFieldsSurvive()
    {
        var original = Person("Ada Lovelace", "ada@x") with
        {
            GivenName = "Ada",
            FamilyName = "Lovelace",
            Company = "Analytical Engines",
            Phones = [new LabeledValue("+44 20 0000", "mobile")],
            Birthday = new DateOnly(1815, 12, 10),
            Notes = "First programmer",
        };
        var contact = Src.AddContact(Src.AddContainer(null, "F"), original);

        await _f.RunAsync(_f.CreateJob([contact], kind: CapabilityKind.Contacts));

        var copied = Assert.Single(await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct));
        Assert.Equal(original, await Dst.ReadContactAsync(copied, Ct));
    }

    [Fact]
    public async Task ExistingPeopleAreSkipped_UnlessTurnedOff()
    {
        var folder = Src.AddContainer(null, "Friends");
        Src.AddContact(folder, Person("Ada", "ADA@x"));
        Src.AddContact(folder, Person("No Email", null));
        var dstFolder = Dst.AddContainer(null, "Friends");
        Dst.AddContact(dstFolder, Person("Ada L.", "ada@x"));
        Dst.AddContact(dstFolder, Person("no email", null));

        await _f.RunAsync(_f.CreateJob([folder], kind: CapabilityKind.Contacts));
        Assert.Equal(2, (await Dst.GetChildrenAsync(dstFolder, Ct).ToListAsync(Ct)).Count);
        Assert.Equal(2, _f.Observer.Finished.Count(f => f.Status == ItemStatus.Skipped));

        await _f.RunAsync(_f.CreateJob([folder], options: new TransferOptions { SkipDuplicates = false }, kind: CapabilityKind.Contacts));
        Assert.Equal(4, (await Dst.GetChildrenAsync(dstFolder, Ct).ToListAsync(Ct)).Count);
    }

    private static Contact Person(string name, string? email)
        => new() { DisplayName = name, Emails = email is null ? [] : [new LabeledValue(email, "home")] };
}
