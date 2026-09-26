using System.Net;
using System.Text;
using System.Text.Json;
using DriveMigrator.Core;
using DriveMigrator.Core.Mail;
using DriveMigrator.Providers.Microsoft.Graph;
using DriveMigrator.Providers.Microsoft.Mail;
using MimeKit;

namespace DriveMigrator.Providers.Tests;

public class OutlookMailTests
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Root_ListsFoldersWithRoles_HidingDraftsAndOutbox()
    {
        var handler = new FakeHttpHandler(r => Route(r, new()
        {
            ["me/mailFolders?"] = """
                { "value": [
                    { "id": "IN", "displayName": "Inbox" }, { "id": "DR", "displayName": "Drafts" },
                    { "id": "SE", "displayName": "Sent Items" }, { "id": "W", "displayName": "Work", "childFolderCount": 1 } ] }
                """,
        }));

        var nodes = await Create(handler).GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Equal(["Inbox", "Sent Items", "Work"], nodes.Select(n => n.Name));
        Assert.Equal([MailFolderRole.Inbox, MailFolderRole.Sent, (MailFolderRole?)null], nodes.Select(n => n.Role));
        Assert.Equal("me/mailFolders/IN", nodes[0].Id);
    }

    [Fact]
    public async Task Folder_ListsSubfoldersThenMessages()
    {
        var handler = new FakeHttpHandler(r => Route(r, new()
        {
            ["me/mailFolders/W/childFolders?"] = """{ "value": [ { "id": "P", "displayName": "Project" } ] }""",
            ["me/mailFolders/W/messages?"] = """
                { "value": [ { "id": "M1", "subject": "Plan", "from": { "emailAddress": { "name": "Ada", "address": "ada@x" } },
                               "receivedDateTime": "2025-01-02T03:04:05Z" },
                             { "id": "M2", "subject": "" } ] }
                """,
        }));

        var nodes = await Create(handler).GetChildrenAsync(new MigrationNode("me/mailFolders/W", "Work", NodeKind.MailFolder), Ct).ToListAsync(Ct);

        Assert.Equal(["Project", "Plan", "(no subject)"], nodes.Select(n => n.Name));
        Assert.Equal(("me/messages/M1", NodeKind.MailMessage, "Ada"), (nodes[1].Id, nodes[1].Kind, nodes[1].Detail));
    }

    [Fact]
    public async Task ContainsMessage_FiltersByInternetMessageId()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "value": [ { "id": "X" } ] }"""));

        Assert.True(await Create(handler).ContainsMessageAsync(new MigrationNode("me/mailFolders/IN", "Inbox", NodeKind.MailFolder), "<a'b@x>", Ct));

        Assert.Contains("$filter=internetMessageId%20eq%20%27%3Ca%27%27b%40x%3E%27", Assert.Single(handler.Requests).Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMessage_ReturnsMimeAndState()
    {
        var handler = new FakeHttpHandler(r => r.Url.EndsWith("/$value", StringComparison.Ordinal)
            ? FakeHttpHandler.Bytes("Subject: hi\r\n\r\nbody"u8.ToArray())
            : FakeHttpHandler.Json("""{ "id": "M1", "isRead": true, "flag": { "flagStatus": "flagged" }, "receivedDateTime": "2025-01-02T03:04:05Z", "internetMessageId": "<m1@x>" }"""));

        await using var content = await Create(handler).ReadMessageAsync(new MigrationNode("me/messages/M1", "hi", NodeKind.MailMessage), Ct);
        using var reader = new StreamReader(content.Mime);

        Assert.Equal("Subject: hi\r\n\r\nbody", await reader.ReadToEndAsync(Ct));
        Assert.True(content.IsRead && content.IsFlagged);
        Assert.Equal("<m1@x>", content.InternetMessageId);
    }

    [Fact]
    public async Task Import_CreatesReceivedMessageNotDraft_WithOriginalDatesAndAttachments()
    {
        var mime = new MimeMessage
        {
            Subject = "Quarterly report",
            MessageId = "orig@example.com",
            InReplyTo = "parent@example.com",
            Date = new DateTimeOffset(2024, 3, 4, 10, 0, 0, TimeSpan.Zero),
            Importance = MessageImportance.High,
        };
        mime.From.Add(new MailboxAddress("Ada", "ada@example.com"));
        mime.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        mime.References.Add("root@example.com");
        var builder = new BodyBuilder { HtmlBody = "<p>See <b>attached</b></p>", TextBody = "See attached" };
        builder.Attachments.Add("small.txt", "hello"u8.ToArray(), new ContentType("text", "plain"));
        var large = new byte[GraphMessageBuilder.InlineAttachmentLimit + 10];
        builder.Attachments.Add("large.bin", large, new ContentType("application", "octet-stream"));
        mime.Body = builder.ToMessageBody();

        var handler = new FakeHttpHandler(r => r.Url switch
        {
            Graph + "me/mailFolders/IN/messages" => FakeHttpHandler.Json("""{ "id": "NEW" }""", HttpStatusCode.Created),
            Graph + "me/messages/NEW/attachments/createUploadSession" => FakeHttpHandler.Json("""{ "uploadUrl": "https://outlook.example/up" }"""),
            "https://outlook.example/up" => new HttpResponseMessage(HttpStatusCode.OK),
            var u => throw new InvalidOperationException(u),
        });
        var content = new MailMessageContent(ToStream(mime))
        {
            IsRead = false,
            IsFlagged = true,
            ReceivedAt = new DateTimeOffset(2024, 3, 4, 10, 5, 0, TimeSpan.Zero),
        };

        var node = await Create(handler).ImportMessageAsync(new MigrationNode("me/mailFolders/IN", "Inbox", NodeKind.MailFolder), content, Ct);

        Assert.Equal(("me/messages/NEW", "Quarterly report"), (node.Id, node.Name));
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var root = body.RootElement;
        Assert.Equal("Quarterly report", root.GetProperty("subject").GetString());
        Assert.Equal("html", root.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Contains("<b>attached</b>", root.GetProperty("body").GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("ada@example.com", root.GetProperty("from").GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("bob@example.com", root.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("high", root.GetProperty("importance").GetString());
        Assert.Equal("flagged", root.GetProperty("flag").GetProperty("flagStatus").GetString());
        Assert.False(root.GetProperty("isRead").GetBoolean());

        var properties = root.GetProperty("singleValueExtendedProperties").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("value").GetString());
        Assert.Equal("0", properties["Integer 0x0E07"]); // no MSGFLAG_UNSENT: not a draft
        Assert.Equal("2024-03-04T10:05:00Z", properties["SystemTime 0x0E06"]);
        Assert.Equal("2024-03-04T10:00:00Z", properties["SystemTime 0x0039"]);
        Assert.Equal("<orig@example.com>", properties["String 0x1035"]);
        Assert.Equal("<parent@example.com>", properties["String 0x1042"]);
        Assert.Equal("<root@example.com>", properties["String 0x1039"]);

        var inline = Assert.Single(root.GetProperty("attachments").EnumerateArray());
        Assert.Equal(("small.txt", "aGVsbG8="), (inline.GetProperty("name").GetString(), inline.GetProperty("contentBytes").GetString()));

        // The large attachment went through an upload session, without the access token.
        Assert.Contains("\"name\":\"large.bin\"", handler.Requests[1].BodyText, StringComparison.Ordinal);
        var chunks = handler.Requests.Skip(2).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Null(c.Authorization));
        Assert.Equal(large.Length, chunks.Sum(c => c.Body!.Length));
    }

    [Fact]
    public async Task Import_ReadPlainTextMessage()
    {
        var mime = new MimeMessage { Subject = "Plain" };
        mime.From.Add(new MailboxAddress("A", "a@x"));
        mime.Body = new TextPart("plain") { Text = "just text" };
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "id": "N" }""", HttpStatusCode.Created));

        await Create(handler).ImportMessageAsync(new MigrationNode("me/mailFolders/F", "F", NodeKind.MailFolder), new MailMessageContent(ToStream(mime)) { IsRead = true }, Ct);

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("text", body.RootElement.GetProperty("body").GetProperty("contentType").GetString());
        Assert.False(body.RootElement.TryGetProperty("attachments", out _));
        Assert.Contains("\"value\":\"1\"", Encoding.UTF8.GetString(handler.Requests[0].Body!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateContainer_AtRootAndNested()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "id": "NF", "displayName": "New" }""", HttpStatusCode.Created));
        var mail = Create(handler);

        var root = await mail.CreateContainerAsync(null, "New", Ct);
        await mail.CreateContainerAsync(root, "Child", Ct);

        Assert.Equal("me/mailFolders/NF", root.Id);
        Assert.Equal([Graph + "me/mailFolders", Graph + "me/mailFolders/NF/childFolders"], handler.Requests.Select(r => r.Url));
        Assert.Contains("\"displayName\":\"Child\"", handler.Requests[1].BodyText, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Route(RecordedRequest request, Dictionary<string, string> routes)
    {
        var path = request.Url[Graph.Length..];
        foreach (var (prefix, json) in routes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return FakeHttpHandler.Json(json);
            }
        }

        // Well-known folder lookups: me/mailFolders/{name}?$select=...
        var wellKnown = new Dictionary<string, string> { ["inbox"] = "IN", ["drafts"] = "DR", ["sentitems"] = "SE", ["outbox"] = "OU" };
        var name = path.Split('?')[0].Split('/')[^1];
        return wellKnown.TryGetValue(name, out var id)
            ? FakeHttpHandler.Json($$"""{ "id": "{{id}}", "displayName": "{{name}}" }""")
            : FakeHttpHandler.Json("""{ "error": { "code": "ErrorFolderNotFound" } }""", HttpStatusCode.NotFound);
    }

    private static MemoryStream ToStream(MimeMessage message)
    {
        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return stream;
    }

    private static OutlookMailCapability Create(FakeHttpHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(GraphClient.BaseUrl) };
        return new OutlookMailCapability(new GraphClient(http, _ => Task.FromResult("token"), (_, _) => Task.CompletedTask));
    }
}
