using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using DriveMigrator.Core;
using DriveMigrator.Core.Mail;
using DriveMigrator.Providers.Google.Mail;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;

namespace DriveMigrator.Providers.Tests;

public class GmailTests
{
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me/";

    private const string Labels = """
        { "labels": [
            { "id": "INBOX", "name": "INBOX", "type": "system" },
            { "id": "UNREAD", "name": "UNREAD", "type": "system" },
            { "id": "L1", "name": "Work", "type": "user" },
            { "id": "L2", "name": "Work/Project", "type": "user" },
            { "id": "L3", "name": "Travel/2024", "type": "user" } ] }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Root_ShowsSystemFoldersAndTopLevelLabels()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(Labels));

        var nodes = await Create(handler).GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Equal(["Inbox", "Sent", "Spam", "Trash", "All Mail", "Travel/2024", "Work"], nodes.Select(n => n.Name));
        Assert.Equal(ContainerRole.Archive, nodes.Single(n => n.Id == GmailCapability.AllMail).Role);
    }

    [Fact]
    public async Task Label_ListsSubLabelsThenAllMessagesAcrossPages()
    {
        var handler = new FakeHttpHandler(r => r.Url switch
        {
            var u when u.StartsWith(Api + "labels", StringComparison.Ordinal) => FakeHttpHandler.Json(Labels),
            var u when u.Contains("pageToken=p2", StringComparison.Ordinal) => FakeHttpHandler.Json("""{ "messages": [ { "id": "m3" } ] }"""),
            _ => FakeHttpHandler.Json("""{ "messages": [ { "id": "m1" }, { "id": "m2" } ], "nextPageToken": "p2" }"""),
        });

        var nodes = await Create(handler).GetChildrenAsync(new MigrationNode("L1", "Work", NodeKind.MailFolder), Ct).ToListAsync(Ct);

        Assert.Equal(["Project", "Message m1", "Message m2", "Message m3"], nodes.Select(n => n.Name));
        var list = handler.Requests.First(r => r.Url.StartsWith(Api + "messages?", StringComparison.Ordinal));
        Assert.Equal("L1", HttpUtility.ParseQueryString(list.RequestUri.Query)["labelIds"]);
    }

    [Fact]
    public async Task AllMail_ListsWithoutLabelFilter()
    {
        var handler = new FakeHttpHandler(r => r.Url.StartsWith(Api + "labels", StringComparison.Ordinal)
            ? FakeHttpHandler.Json(Labels)
            : FakeHttpHandler.Json("""{ "messages": [] }"""));

        await Create(handler).GetChildrenAsync(new MigrationNode(GmailCapability.AllMail, "All Mail", NodeKind.MailFolder), Ct).ToListAsync(Ct);

        var list = handler.Requests.Single(r => r.Url.StartsWith(Api + "messages?", StringComparison.Ordinal));
        Assert.Null(HttpUtility.ParseQueryString(list.RequestUri.Query)["labelIds"]);
    }

    [Fact]
    public async Task Browse_ShowsSubjectAndSenderForAPageOfMessages()
    {
        var handler = new FakeHttpHandler(r => r.Url switch
        {
            var u when u.StartsWith(Api + "labels", StringComparison.Ordinal) => FakeHttpHandler.Json(Labels),
            var u when u.StartsWith(Api + "messages?", StringComparison.Ordinal) => FakeHttpHandler.Json("""{ "messages": [ { "id": "m1" }, { "id": "m2" } ], "nextPageToken": "more" }"""),
            var u when u.StartsWith(Api + "messages/m1", StringComparison.Ordinal) => FakeHttpHandler.Json("""
                { "id": "m1", "internalDate": "1700000000000", "sizeEstimate": 2048,
                  "payload": { "headers": [ { "name": "Subject", "value": "Hello" }, { "name": "From", "value": "Ada Lovelace <ada@x>" } ] } }
                """),
            var u => throw new InvalidOperationException(u),
        });

        var nodes = await Create(handler).BrowseChildrenAsync(new MigrationNode("INBOX", "Inbox", NodeKind.MailFolder), 1, Ct).ToListAsync(Ct);

        var message = Assert.Single(nodes);
        Assert.Equal(("Hello", "Ada Lovelace", (long?)2048), (message.Name, message.Detail, message.Size));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), message.ModifiedAt);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("pageToken=more", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadMessage_DecodesRawAndMapsLabelsToState()
    {
        var mime = "Message-ID: <abc@x>\r\nSubject: hi\r\n\r\nbody ÿ"u8.ToArray();
        var raw = Convert.ToBase64String(mime).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json($$"""{ "id": "m1", "raw": "{{raw}}", "labelIds": [ "INBOX", "STARRED" ], "internalDate": "1700000000000" }"""));

        await using var content = await Create(handler).ReadMessageAsync(new MigrationNode("m1", "hi", NodeKind.MailMessage), Ct);
        using var copy = new MemoryStream();
        await content.Mime.CopyToAsync(copy, Ct);

        Assert.Equal(mime, copy.ToArray());
        Assert.True(content.IsRead);
        Assert.True(content.IsFlagged);
        Assert.Equal("<abc@x>", content.InternetMessageId);
        Assert.Contains("format=raw", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_NewMessageUsesImportWithLabelsAndState()
    {
        var handler = new FakeHttpHandler(r => r switch
        {
            { Method.Method: "GET" } => FakeHttpHandler.Json("{}"), // rfc822msgid search: nothing
            { Method.Method: "POST" } => Resumable("https://upload.example/imp"),
            _ => FakeHttpHandler.Json("""{ "id": "new1" }"""),
        });
        var content = new MailMessageContent(new MemoryStream("Subject: x\r\n\r\ny"u8.ToArray())) { IsRead = false, IsFlagged = true, InternetMessageId = "<id1@x>" };

        var node = await Create(handler).ImportMessageAsync(new MigrationNode("L1", "Work", NodeKind.MailFolder), content, Ct);

        Assert.Equal("new1", node.Id);
        Assert.Equal("rfc822msgid:id1@x", HttpUtility.ParseQueryString(handler.Requests[0].RequestUri.Query)["q"]);
        var start = handler.Requests[1];
        var query = HttpUtility.ParseQueryString(start.RequestUri.Query);
        Assert.Equal(("dateHeader", "true", "false"), (query["internalDateSource"], query["neverMarkSpam"], query["processForCalendar"]));
        using var metadata = JsonDocument.Parse(start.Body);
        Assert.Equal(["L1", "UNREAD", "STARRED"], metadata.RootElement.GetProperty("labelIds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Subject: x\r\n\r\ny", handler.Requests[2].BodyText);
    }

    [Fact]
    public async Task Import_ExistingMessageJustGetsTheLabel()
    {
        var handler = new FakeHttpHandler(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json("""{ "messages": [ { "id": "old1" } ] }""")
            : FakeHttpHandler.Json("""{ "id": "old1" }"""));
        var content = new MailMessageContent(new MemoryStream([1])) { InternetMessageId = "<dup@x>" };

        var node = await Create(handler).ImportMessageAsync(new MigrationNode("L2", "Project", NodeKind.MailFolder), content, Ct);

        Assert.Equal("old1", node.Id);
        var modify = handler.Requests[1];
        Assert.Equal(Api + "messages/old1/modify", modify.Url);
        Assert.Contains("\"addLabelIds\":[\"L2\"]", modify.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainsMessage_SearchesWithinTheLabel()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}"));

        Assert.False(await Create(handler).ContainsMessageAsync(new MigrationNode("SPAM", "Spam", NodeKind.MailFolder), "<a@x>", Ct));

        var query = HttpUtility.ParseQueryString(Assert.Single(handler.Requests).RequestUri.Query);
        Assert.Equal(("SPAM", "true", "rfc822msgid:a@x"), (query["labelIds"], query["includeSpamTrash"], query["q"]));
    }

    [Fact]
    public async Task CreateContainer_NestsUnderUserLabelsOnly()
    {
        var created = 0;
        var handler = new FakeHttpHandler(r => r.Method == HttpMethod.Get
            ? FakeHttpHandler.Json(Labels)
            : FakeHttpHandler.Json($$"""{ "id": "N{{++created}}", "name": "x", "type": "user" }"""));
        var gmail = Create(handler);

        await gmail.CreateContainerAsync(new MigrationNode("L1", "Work", NodeKind.MailFolder), "Clients", Ct);
        await gmail.CreateContainerAsync(new MigrationNode("INBOX", "Inbox", NodeKind.MailFolder), "Receipts", Ct);

        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("name").GetString());
        Assert.Equal(["Work/Clients", "Receipts"], posts);
    }

    // Regression: with a field filter, Gmail answers "no results" with an empty body, which the client returns as
    // null; that crashed the duplicate check when copying a single message into Gmail.
    [Fact]
    public async Task EmptyResponses_MeanNoResults()
    {
        var handler = new FakeHttpHandler(r => r switch
        {
            { Method.Method: "GET" } when r.Url.StartsWith(Api + "labels", StringComparison.Ordinal) => Empty(),
            { Method.Method: "GET" } => Empty(),
            { Method.Method: "POST" } => Resumable("https://upload.example/imp2"),
            _ => FakeHttpHandler.Json("""{ "id": "new2" }"""),
        });
        var gmail = Create(handler);
        var inbox = new MigrationNode("INBOX", "Inbox", NodeKind.MailFolder);

        Assert.False(await gmail.ContainsMessageAsync(inbox, "<brief@x>", Ct));
        Assert.Empty(await gmail.GetChildrenAsync(inbox, Ct).ToListAsync(Ct));
        Assert.Equal(5, (await gmail.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Count);

        var content = new MailMessageContent(new MemoryStream("Subject: x\r\n\r\ny"u8.ToArray())) { InternetMessageId = "<brief@x>" };
        Assert.Equal("new2", (await gmail.ImportMessageAsync(inbox, content, Ct)).Id);
    }

    [Theory]
    [InlineData("Receipts", "Receipts")]
    [InlineData("Inbox", "Inbox (imported)")]
    [InlineData("sent", "sent (imported)")]
    [InlineData("A/B", "A-B")]
    public void ToValidName_AvoidsReservedNamesAndNesting(string name, string expected)
        => Assert.Equal(expected, Create(new FakeHttpHandler(_ => throw new InvalidOperationException())).ToValidName(name));

    [Fact]
    public void DecodeBase64Url_HandlesMissingPadding()
        => Assert.Equal("any carnal pleas"u8.ToArray(), GmailCapability.DecodeBase64Url("YW55IGNhcm5hbCBwbGVhcw"));

    private static HttpResponseMessage Empty() => new(HttpStatusCode.OK) { Content = new StringContent(string.Empty, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Resumable(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static GmailCapability Create(FakeHttpHandler handler)
        => new(new GmailService(new BaseClientService.Initializer
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
