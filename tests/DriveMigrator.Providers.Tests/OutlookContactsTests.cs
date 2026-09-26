using System.Net;
using System.Text.Json;
using DriveMigrator.Core;
using DriveMigrator.Core.Contacts;
using DriveMigrator.Providers.Microsoft.Contacts;
using DriveMigrator.Providers.Microsoft.Graph;

namespace DriveMigrator.Providers.Tests;

public class OutlookContactsTests
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tree_DefaultFolderHoldsFoldersAndContacts()
    {
        var handler = new FakeHttpHandler(r => r.Url.StartsWith(Graph + "me/contactFolders", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""{ "value": [ { "id": "F1", "displayName": "Family" } ] }""")
            : FakeHttpHandler.Json("""{ "value": [ { "id": "C1", "displayName": "Ada Lovelace", "emailAddresses": [ { "address": "ada@x" } ] } ] }"""));
        var contacts = Create(handler);

        var root = Assert.Single(await contacts.GetChildrenAsync(null, Ct).ToListAsync(Ct));
        var children = await contacts.GetChildrenAsync(root, Ct).ToListAsync(Ct);

        Assert.Equal(("me", "Contacts", ContainerRole.DefaultContacts), (root.Id, root.Name, root.Role!.Value));
        Assert.Equal(["Family", "Ada Lovelace"], children.Select(c => c.Name));
        Assert.Equal(("me/contactFolders/F1", "me/contacts/C1", "ada@x"), (children[0].Id, children[1].Id, children[1].Detail));
        Assert.StartsWith(Graph + "me/contacts?", handler.Requests[1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_FitsGraphSlotsAndKeepsTheRestInNotes()
    {
        var contact = new Contact
        {
            DisplayName = "Ada Lovelace",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            Emails = [new("a@x", "home"), new("b@x", "work"), new("c@x"), new("d@x", "other")],
            Phones = [new("1", "mobile"), new("2", "work"), new("3", "home"), new("4", "mobile"), new("5")],
            Addresses = [new PostalAddress { Label = "home", City = "London" }, new PostalAddress { Label = "home", City = "Paris" }, new PostalAddress { Label = "work", City = "Oxford" }, new PostalAddress { Label = "other", City = "Rome" }],
            Websites = [new("https://ada.example", "home"), new("https://blog.example")],
            Birthday = new DateOnly(GraphContactMapper.NoYear, 12, 10),
            Notes = "Hi",
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(GraphContactMapper.ToGraph(contact)));
        var root = json.RootElement;

        Assert.Equal(3, root.GetProperty("emailAddresses").GetArrayLength());
        Assert.Equal("1", root.GetProperty("mobilePhone").GetString());
        Assert.Equal(["2"], root.GetProperty("businessPhones").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["3", "4", "5"], root.GetProperty("homePhones").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("London", root.GetProperty("homeAddress").GetProperty("city").GetString());
        Assert.Equal("Paris", root.GetProperty("otherAddress").GetProperty("city").GetString());
        Assert.Equal("Oxford", root.GetProperty("businessAddress").GetProperty("city").GetString());
        Assert.Equal("https://ada.example", root.GetProperty("businessHomePage").GetString());
        Assert.Equal("1604-12-10T00:00:00Z", root.GetProperty("birthday").GetString());

        var notes = root.GetProperty("personalNotes").GetString()!;
        Assert.StartsWith("Hi", notes, StringComparison.Ordinal);
        Assert.Contains("Email (other): d@x", notes, StringComparison.Ordinal);
        Assert.Contains("Address (other): Rome", notes, StringComparison.Ordinal);
        Assert.Contains("Website: https://blog.example", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_ReadsGraphContact()
    {
        var graph = new GraphContact
        {
            DisplayName = "Ada Lovelace",
            GivenName = "Ada",
            Surname = "Lovelace",
            CompanyName = "Engines",
            EmailAddresses = [new("Ada", "ada@x"), new(null, "")],
            MobilePhone = "1",
            BusinessPhones = ["2"],
            HomePhones = [],
            HomeAddress = new GraphAddress("1 Main St", "London", null, "N1", "UK"),
            BusinessAddress = new GraphAddress(null, null, null, null, null),
            Birthday = "1815-12-10T11:59:00Z",
            PersonalNotes = " ",
        };

        var contact = GraphContactMapper.ToContact(graph);

        Assert.Equal(["ada@x"], contact.Emails.Select(e => e.Value));
        Assert.Equal([("1", "mobile"), ("2", "work")], contact.Phones.Select(p => (p.Value, p.Label!)));
        Assert.Equal(("London", "home"), (Assert.Single(contact.Addresses).City!, contact.Addresses[0].Label!));
        Assert.Equal(new DateOnly(1815, 12, 10), contact.Birthday);
        Assert.Null(contact.Notes);
        Assert.Equal("Engines", contact.Company);
    }

    [Fact]
    public async Task Import_PostsToFolder_AndDuplicateCheckSeesIt()
    {
        var handler = new FakeHttpHandler(r => r.Method == HttpMethod.Post
            ? FakeHttpHandler.Json("""{ "id": "NEW" }""", HttpStatusCode.Created)
            : FakeHttpHandler.Json("""{ "value": [ { "id": "C1", "displayName": "Grace", "emailAddresses": [ { "address": "GRACE@x" } ] } ] }"""));
        var contacts = Create(handler);
        var family = new MigrationNode("me/contactFolders/F1", "Family", NodeKind.ContactFolder);
        var ada = new Contact { DisplayName = "Ada", Emails = [new("ada@x")] };

        Assert.True(await contacts.ContainsContactAsync(family, new Contact { DisplayName = "G", Emails = [new("grace@x")] }, Ct));
        Assert.False(await contacts.ContainsContactAsync(family, ada, Ct));
        var node = await contacts.ImportContactAsync(family, ada, Ct);
        Assert.True(await contacts.ContainsContactAsync(family, ada, Ct));

        Assert.Equal("me/contacts/NEW", node.Id);
        Assert.Equal(Graph + "me/contactFolders/F1/contacts", handler.Requests.Single(r => r.Method == HttpMethod.Post).Url);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Import_WithoutFolderGoesToDefault()
    {
        var handler = new FakeHttpHandler(r => r.Method == HttpMethod.Post
            ? FakeHttpHandler.Json("""{ "id": "NEW" }""", HttpStatusCode.Created)
            : FakeHttpHandler.Json("""{ "value": [] }"""));

        await Create(handler).ImportContactAsync(null, new Contact { DisplayName = "Ada" }, Ct);

        Assert.Equal(Graph + "me/contacts", handler.Requests.Single(r => r.Method == HttpMethod.Post).Url);
    }

    [Fact]
    public async Task CreateContainer_InDefaultOrNested()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "id": "N", "displayName": "X" }""", HttpStatusCode.Created));
        var contacts = Create(handler);

        var top = await contacts.CreateContainerAsync(new MigrationNode("me", "Contacts", NodeKind.ContactFolder), "X", Ct);
        await contacts.CreateContainerAsync(top, "Y", Ct);

        Assert.Equal([Graph + "me/contactFolders", Graph + "me/contactFolders/N/childFolders"], handler.Requests.Select(r => r.Url));
    }

    private static OutlookContactsCapability Create(FakeHttpHandler handler)
        => new(new GraphClient(new HttpClient(handler) { BaseAddress = new Uri(GraphClient.BaseUrl) }, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask));
}
