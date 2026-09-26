using System.Text.Json;
using System.Web;
using DriveMigrator.Core;
using DriveMigrator.Core.Contacts;
using DriveMigrator.Providers.Google.Contacts;
using Google.Apis.Http;
using Google.Apis.PeopleService.v1;
using Google.Apis.PeopleService.v1.Data;
using Google.Apis.Services;

namespace DriveMigrator.Providers.Tests;

public class GoogleContactsTests
{
    private const string Api = "https://people.googleapis.com/v1/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Root_AllContactsAndUserGroups()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            { "contactGroups": [
                { "resourceName": "contactGroups/myContacts", "name": "myContacts", "groupType": "SYSTEM_CONTACT_GROUP" },
                { "resourceName": "contactGroups/f1", "name": "Family", "formattedName": "Family", "groupType": "USER_CONTACT_GROUP" } ] }
            """));

        var nodes = await Create(handler).GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Equal(["All contacts", "Family"], nodes.Select(n => n.Name));
        Assert.Equal(ContainerRole.DefaultContacts, nodes[0].Role);
        Assert.Equal("contactGroups/f1", nodes[1].Id);
    }

    [Fact]
    public async Task AllContacts_ListsConnectionsAcrossPages()
    {
        var handler = new FakeHttpHandler(r => r.Url.Contains("pageToken=p2", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""{ "connections": [ { "resourceName": "people/c2", "names": [ { "displayName": "Grace" } ] } ] }""")
            : FakeHttpHandler.Json("""{ "connections": [ { "resourceName": "people/c1", "names": [ { "displayName": "Ada" } ], "emailAddresses": [ { "value": "ada@x" } ] } ], "nextPageToken": "p2" }"""));

        var nodes = await Create(handler).GetChildrenAsync(new MigrationNode("ALL", "All contacts", NodeKind.ContactFolder), Ct).ToListAsync(Ct);

        Assert.Equal([("people/c1", "Ada", "ada@x"), ("people/c2", "Grace", null)], nodes.Select(n => (n.Id, n.Name, n.Detail)));
        Assert.StartsWith(Api + "people/me/connections?", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_ListsMembersInBatches()
    {
        var handler = new FakeHttpHandler(r => r.Url.StartsWith(Api + "contactGroups/", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""{ "resourceName": "contactGroups/f1", "memberResourceNames": [ "people/c1", "people/c2" ] }""")
            : FakeHttpHandler.Json("""{ "responses": [ { "person": { "resourceName": "people/c1", "names": [ { "displayName": "Ada" } ] } }, { "person": { "resourceName": "people/c2", "names": [ { "displayName": "Grace" } ] } } ] }"""));

        var nodes = await Create(handler).GetChildrenAsync(new MigrationNode("contactGroups/f1", "Family", NodeKind.ContactFolder), Ct).ToListAsync(Ct);

        Assert.Equal(["Ada", "Grace"], nodes.Select(n => n.Name));
        var batch = HttpUtility.ParseQueryString(handler.Requests[1].RequestUri.Query);
        Assert.Equal("people/c1,people/c2", batch["resourceNames"]);
    }

    [Fact]
    public async Task Read_MapsFields_AndBirthdayWithoutYear()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            { "resourceName": "people/c1",
              "names": [ { "displayName": "Ada Lovelace", "givenName": "Ada", "familyName": "Lovelace" } ],
              "emailAddresses": [ { "value": "ada@x", "type": "home" } ],
              "phoneNumbers": [ { "value": "+1", "type": "mobile" } ],
              "organizations": [ { "name": "Engines", "title": "Programmer" } ],
              "birthdays": [ { "date": { "month": 12, "day": 10 } } ],
              "biographies": [ { "value": "First programmer" } ] }
            """));

        var contact = await Create(handler).ReadContactAsync(new MigrationNode("people/c1", "Ada", NodeKind.Contact), Ct);

        Assert.Equal(("Ada Lovelace", "Ada", "Lovelace", "Engines", "Programmer"), (contact.DisplayName, contact.GivenName!, contact.FamilyName!, contact.Company!, contact.JobTitle!));
        Assert.Equal(("ada@x", "home"), (contact.Emails[0].Value, contact.Emails[0].Label!));
        Assert.Equal(new DateOnly(PersonMapper.NoYear, 12, 10), contact.Birthday);
        Assert.Equal("First programmer", contact.Notes);
        Assert.Contains("personFields=names", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_IntoGroupSetsMembership_AndDuplicateCheckSeesIt()
    {
        var handler = new FakeHttpHandler(r => r.Method == HttpMethod.Post
            ? FakeHttpHandler.Json("""{ "resourceName": "people/new" }""")
            : FakeHttpHandler.Json("""{ "resourceName": "contactGroups/f1" }"""));
        var contacts = Create(handler);
        var family = new MigrationNode("contactGroups/f1", "Family", NodeKind.ContactFolder);
        var ada = new Contact { DisplayName = "Ada Lovelace", GivenName = "Ada", FamilyName = "Lovelace", Emails = [new("ada@x", "home")], Birthday = new DateOnly(PersonMapper.NoYear, 12, 10) };

        Assert.False(await contacts.ContainsContactAsync(family, ada, Ct));
        var node = await contacts.ImportContactAsync(family, ada, Ct);
        Assert.True(await contacts.ContainsContactAsync(family, ada, Ct));

        Assert.Equal("people/new", node.Id);
        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.StartsWith(Api + "people:createContact", post.Url, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(post.Body);
        var root = body.RootElement;
        Assert.Equal("Ada", root.GetProperty("names")[0].GetProperty("givenName").GetString());
        Assert.Equal("contactGroups/f1", root.GetProperty("memberships")[0].GetProperty("contactGroupMembership").GetProperty("contactGroupResourceName").GetString());
        Assert.False(root.GetProperty("birthdays")[0].GetProperty("date").TryGetProperty("year", out _));
    }

    [Fact]
    public async Task Import_IntoAllContactsHasNoMembership()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "resourceName": "people/new" }"""));

        await Create(handler).ImportContactAsync(null, new Contact { DisplayName = "Solo" }, Ct);

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.False(body.RootElement.TryGetProperty("memberships", out _));
        Assert.Equal("Solo", body.RootElement.GetProperty("names")[0].GetProperty("unstructuredName").GetString());
    }

    [Fact]
    public async Task CreateContainer_FlattensNesting()
    {
        var handler = new FakeHttpHandler(r => FakeHttpHandler.Json("""{ "resourceName": "contactGroups/new", "formattedName": "Work - Clients" }"""));

        var node = await Create(handler).CreateContainerAsync(new MigrationNode("contactGroups/w", "Work", NodeKind.ContactFolder), "Clients", Ct);

        Assert.Equal(("contactGroups/new", "Work - Clients"), (node.Id, node.Name));
        Assert.Contains("\"name\":\"Work - Clients\"", handler.Requests[0].BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_RoundTrips()
    {
        var contact = new Contact
        {
            DisplayName = "Ada Lovelace",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            Nickname = "Enchantress",
            Company = "Engines",
            Department = "R&D",
            JobTitle = "Programmer",
            Emails = [new("ada@x", "home")],
            Phones = [new("+1", "mobile")],
            Addresses = [new PostalAddress { Label = "home", Street = "1 Main St", City = "London", Country = "UK" }],
            Websites = [new("https://ada.example", "homePage")],
            Birthday = new DateOnly(1815, 12, 10),
            Notes = "First programmer",
        };

        var person = PersonMapper.ToPerson(contact, null);
        person.Names[0].DisplayName = "Ada Lovelace"; // Google computes the display name.

        Assert.Equal(contact, PersonMapper.ToContact(person));
    }

    private static GoogleContactsCapability Create(FakeHttpHandler handler)
        => new(new PeopleServiceService(new BaseClientService.Initializer
        {
            HttpClientFactory = new FakeClientFactory(handler),
            ApplicationName = "tests",
            GZipEnabled = false,
        }));

    private sealed class FakeClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }
}
