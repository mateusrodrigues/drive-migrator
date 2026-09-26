using System.Net;
using System.Text;
using DriveMigrator.Core;
using DriveMigrator.Core.Mail;
using DriveMigrator.Providers.Google.Mail;
using DriveMigrator.Providers.Microsoft.Graph;
using DriveMigrator.Providers.Microsoft.Mail;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;

namespace DriveMigrator.Providers.Tests;

/// <summary>Copies realistic messages between the two mail providers (and through the engine) over canned HTTP.</summary>
public class MailRoundTripTests
{
    private const string Newsletter =
        "From: \"Morning Brew\" <crew@morningbrew.com>\r\n" +
        "To: ada@gmail.com\r\n" +
        "Subject: =?UTF-8?Q?Daily_Briefing_=E2=80=94_September_25=2C_2026?=\r\n" +
        "Date: Fri, 25 Sep 2026 06:00:00 +0000\r\n" +
        "Message-ID: <brief-0925@mail.example>\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/alternative; boundary=\"alt\"\r\n\r\n" +
        "--alt\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHello\r\n" +
        "--alt\r\nContent-Type: multipart/related; boundary=\"rel\"\r\n\r\n" +
        "--rel\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<img src=\"https://x/logo.png\"><p>Hi</p>\r\n" +
        "--rel\r\nContent-Type: image/png\r\nContent-Location: https://x/logo.png\r\nContent-Transfer-Encoding: base64\r\n\r\niVBORw0KGgo=\r\n" +
        "--rel--\r\n--alt--\r\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OutlookToGmail_NonSeekableStream()
    {
        var graphHandler = new FakeHttpHandler(r => r.Url.EndsWith("/$value", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ForwardOnly(Encoding.UTF8.GetBytes(Newsletter))) }
            : FakeHttpHandler.Json("""{ "id": "M1", "isRead": false, "flag": { "flagStatus": "notFlagged" }, "receivedDateTime": "2026-09-25T06:00:01Z", "internetMessageId": "<brief-0925@mail.example>" }"""));
        var outlook = new OutlookMailCapability(new GraphClient(new HttpClient(graphHandler) { BaseAddress = new Uri(GraphClient.BaseUrl) }, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask));

        var gmailHandler = new FakeHttpHandler(r => r switch
        {
            { Method.Method: "GET" } => FakeHttpHandler.Json("{}"),
            { Method.Method: "POST" } => Resumable(),
            _ => FakeHttpHandler.Json("""{ "id": "new1" }"""),
        });
        var gmail = new GmailCapability(new GmailService(new BaseClientService.Initializer { HttpClientFactory = new Factory(gmailHandler), GZipEnabled = false }));

        await using var content = await outlook.ReadMessageAsync(new MigrationNode("me/messages/M1", "x", NodeKind.MailMessage), Ct);
        var node = await gmail.ImportMessageAsync(new MigrationNode("INBOX", "Inbox", NodeKind.MailFolder), content, Ct);
        Assert.Equal("new1", node.Id);
    }

    [Fact]
    public async Task GmailToOutlook_Newsletter()
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(Newsletter)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var gmailHandler = new FakeHttpHandler(_ => FakeHttpHandler.Json($$"""{ "id": "g1", "raw": "{{raw}}", "labelIds": ["INBOX"], "internalDate": "1790316000000" }"""));
        var gmail = new GmailCapability(new GmailService(new BaseClientService.Initializer { HttpClientFactory = new Factory(gmailHandler), GZipEnabled = false }));
        var graphHandler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "id": "NEW" }""", HttpStatusCode.Created));
        var outlook = new OutlookMailCapability(new GraphClient(new HttpClient(graphHandler) { BaseAddress = new Uri(GraphClient.BaseUrl) }, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask));

        await using var content = await gmail.ReadMessageAsync(new MigrationNode("g1", "x", NodeKind.MailMessage), Ct);
        var node = await outlook.ImportMessageAsync(new MigrationNode("me/mailFolders/IN", "Inbox", NodeKind.MailFolder), content, Ct);
        Assert.Equal("me/messages/NEW", node.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GmailToOutlook_SingleMessageThroughEngine(bool destinationFolder)
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(Newsletter)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var gmailHandler = new FakeHttpHandler(r => r.Url.Contains("/labels", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""{ "labels": [] }""")
            : FakeHttpHandler.Json($$"""{ "id": "g1", "raw": "{{raw}}", "labelIds": ["INBOX"], "internalDate": "1790316000000" }"""));
        var gmail = new GmailCapability(new GmailService(new BaseClientService.Initializer { HttpClientFactory = new Factory(gmailHandler), GZipEnabled = false }));
        var graphHandler = new FakeHttpHandler(r => r.Url switch
        {
            var u when u.Contains("/messages?$filter", StringComparison.Ordinal) => FakeHttpHandler.Json("""{ "value": [] }"""),
            var u when u.Contains("mailFolders/inbox", StringComparison.Ordinal) => FakeHttpHandler.Json("""{ "id": "IN", "displayName": "Inbox" }"""),
            var u when u.Contains("mailFolders/", StringComparison.Ordinal) && r.Method == HttpMethod.Get => FakeHttpHandler.Json("""{ "error": { "code": "ErrorFolderNotFound" } }""", HttpStatusCode.NotFound),
            _ => FakeHttpHandler.Json("""{ "id": "NEW" }""", HttpStatusCode.Created),
        });
        var outlook = new OutlookMailCapability(new GraphClient(new HttpClient(graphHandler) { BaseAddress = new Uri(GraphClient.BaseUrl) }, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask));

        var directory = Directory.CreateTempSubdirectory("mail-roundtrip-").FullName;
        var store = Engine.TransferStore.Open(Path.Combine(directory, "t.db"));
        var source = new Session("google", gmail);
        var destination = new Session("microsoft", outlook);
        var message = new MigrationNode("g1", "Daily Briefing — September 25, 2026", NodeKind.MailMessage) { Detail = "Morning Brew", Size = 1234, ModifiedAt = DateTimeOffset.UtcNow, MimeType = "message/rfc822" };
        var target = destinationFolder ? new MigrationNode("me/mailFolders/IN", "Inbox", NodeKind.MailFolder) { Role = MailFolderRole.Inbox } : null;
        var job = store.CreateJob("t", new("google", "a"), new("microsoft", "b"), Core.Transfers.TransferOptions.Default,
            [new(CapabilityKind.Mail, target)], [new Engine.NewItem(CapabilityKind.Mail, message, target, message.Name)]);
        var observer = new Observer();

        try
        {
            await new Engine.TransferEngine(store).RunAsync(job, source, destination, observer, Ct);

            Assert.Empty(store.LoadItems(job.Id, Engine.ItemStatus.Failed).Select(i => i.Error));
            var done = Assert.Single(store.LoadItems(job.Id, Engine.ItemStatus.Done));
            Assert.Equal("me/messages/NEW", done.Target!.Id);
        }
        finally
        {
            store.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Session(string provider, ICapability capability) : Core.Accounts.IAccountSession
    {
        public Core.Accounts.AccountInfo Account { get; } = new(provider, "a", "A", "a@x");

        public IReadOnlyList<ICapability> Capabilities { get; } = [capability];
    }

    private sealed class Observer : Engine.ITransferObserver
    {
        public void ItemStarted(Engine.ItemRecord item)
        {
        }

        public void ItemFinished(Engine.ItemRecord item, Engine.ItemStatus status, string? message)
        {
        }

        public void ItemsDiscovered(int count)
        {
        }

        public void BytesTransferred(long delta)
        {
        }
    }

    private static HttpResponseMessage Resumable()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Location = new Uri("https://upload.example/x");
        return response;
    }

    private sealed class Factory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private sealed class ForwardOnly(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }
}
