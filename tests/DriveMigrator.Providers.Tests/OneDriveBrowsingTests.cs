using System.Net;
using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using DriveMigrator.Providers.Microsoft.Drive;
using DriveMigrator.Providers.Microsoft.Graph;

namespace DriveMigrator.Providers.Tests;

public class OneDriveBrowsingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListsRootAndFollowsNextLink()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://graph.microsoft.com/v1.0/me/drive/root/children?$skiptoken=page2" => FakeHttpHandler.Json("""
                { "value": [ { "id": "N1", "name": "Notebook", "package": { "type": "oneNote" }, "parentReference": { "driveId": "d1" } } ] }
                """),
            var u when u.StartsWith("https://graph.microsoft.com/v1.0/me/drive/root/children?", StringComparison.Ordinal) => FakeHttpHandler.Json("""
                {
                  "value": [
                    { "id": "F1", "name": "Documents", "folder": { "childCount": 3 }, "parentReference": { "driveId": "d1", "id": "root" } },
                    { "id": "A1", "name": "photo.jpg", "size": 1234, "lastModifiedDateTime": "2026-01-02T03:04:05Z",
                      "file": { "mimeType": "image/jpeg" }, "parentReference": { "driveId": "d1" } }
                  ],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/drive/root/children?$skiptoken=page2"
                }
                """),
            var u => throw new InvalidOperationException($"Unexpected request {u}"),
        });
        var drive = CreateCapability(handler);

        var nodes = await drive.GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Collection(
            nodes,
            n => Assert.Equal(("drives/d1/items/F1", "Documents", NodeKind.Folder, (long?)null), (n.Id, n.Name, n.Kind, n.Size)),
            n =>
            {
                Assert.Equal(("drives/d1/items/A1", "photo.jpg", NodeKind.File, (long?)1234), (n.Id, n.Name, n.Kind, n.Size));
                Assert.Equal("image/jpeg", n.MimeType);
                Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), n.ModifiedAt);
            });
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer token", r.Authorization));
    }

    [Fact]
    public async Task RepeatedNextLink_FailsInsteadOfLoopingForever()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            { "value": [], "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/drive/root/children?$skiptoken=same" }
            """));
        var drive = CreateCapability(handler);

        await Assert.ThrowsAsync<GraphException>(async () => await drive.GetChildrenAsync(null, Ct).ToListAsync(Ct));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ChildrenOfFolderUseTheNodePath()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "value": [] }"""));
        var drive = CreateCapability(handler);
        var folder = new MigrationNode("drives/d1/items/F1", "Documents", NodeKind.Folder);

        await drive.GetChildrenAsync(folder, Ct).ToListAsync(Ct);

        Assert.StartsWith("https://graph.microsoft.com/v1.0/drives/d1/items/F1/children?", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteItem_PointsAtTheSharedDrive()
    {
        var item = new DriveItem("local", "Shared folder", null, null, null, null, null,
            new RemoteItemFacet("R1", null, new FolderFacet(2), null, new ItemReference("otherDrive", null)),
            new ItemReference("d1", null));

        var node = OneDriveCapability.ToNode(item)!;

        Assert.Equal(("drives/otherDrive/items/R1", NodeKind.Folder), (node.Id, node.Kind));
    }

    [Fact]
    public async Task Listing_MapsWhicheverHashesTheAccountReports()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {
              "value": [
                { "id": "P1", "name": "personal.bin", "parentReference": { "driveId": "d1" },
                  "file": { "mimeType": "application/octet-stream", "hashes": {
                    "sha1Hash": "A9993E364706816ABA3E25717850C26C9CD0D89D",
                    "sha256Hash": "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                    "crc32Hash": "C2412435", "quickXorHash": "YgMAAAAAAAAAAAAAAwAAAAAAAAA=" } } },
                { "id": "B1", "name": "business.bin", "parentReference": { "driveId": "d1" },
                  "file": { "mimeType": "application/octet-stream", "hashes": { "quickXorHash": "YgMAAAAAAAAAAAAAAwAAAAAAAAA=" } } },
                { "id": "N1", "name": "new.bin", "parentReference": { "driveId": "d1" }, "file": { "mimeType": "application/octet-stream" } },
                { "id": "F1", "name": "Folder", "parentReference": { "driveId": "d1" }, "folder": { "childCount": 0 } }
              ]
            }
            """));

        var nodes = await CreateCapability(handler).GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Equal(
            new FileHashes(
                Sha1: "a9993e364706816aba3e25717850c26c9cd0d89d",
                Sha256: "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                QuickXor: "YgMAAAAAAAAAAAAAAwAAAAAAAAA="),
            nodes[0].Hashes);
        Assert.Equal(new FileHashes(QuickXor: "YgMAAAAAAAAAAAAAAwAAAAAAAAA="), nodes[1].Hashes);
        Assert.Null(nodes[2].Hashes);
        Assert.Null(nodes[3].Hashes);
    }

    [Fact]
    public void RemoteItem_TakesTheSharedFilesHashes()
    {
        var item = new DriveItem("local", "shared.bin", null, null, null, null, null,
            new RemoteItemFacet("R1", 3, null, new FileFacet("application/octet-stream", new HashesFacet("YgMAAAAAAAAAAAAAAwAAAAAAAAA=", null, null)), new ItemReference("otherDrive", null)),
            new ItemReference("d1", null));

        Assert.Equal(new FileHashes(QuickXor: "YgMAAAAAAAAAAAAAAwAAAAAAAAA="), OneDriveCapability.ToNode(item)!.Hashes);
    }

    [Fact]
    public async Task Throttling_RetriesAfterRetryAfter()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            if (calls++ == 0)
            {
                var throttled = FakeHttpHandler.Json("""{ "error": { "code": "activityLimitReached" } }""", HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return throttled;
            }

            return FakeHttpHandler.Json("""{ "value": [] }""");
        });
        var delays = new List<TimeSpan>();
        var drive = CreateCapability(handler, delays);

        await drive.GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PersistentThrottling_GivesUp()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
        var delays = new List<TimeSpan>();
        var drive = CreateCapability(handler, delays);

        await Assert.ThrowsAsync<GraphException>(async () => await drive.GetChildrenAsync(null, Ct).ToListAsync(Ct));

        Assert.Equal(GraphClient.MaxRetries, delays.Count);
        Assert.Equal(GraphClient.MaxRetries + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task Errors_CarryGraphCodeAndMessage()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{ "error": { "code": "itemNotFound", "message": "The resource could not be found." } }""", HttpStatusCode.NotFound));
        var drive = CreateCapability(handler);

        var ex = await Assert.ThrowsAsync<GraphException>(async () => await drive.GetChildrenAsync(null, Ct).ToListAsync(Ct));

        Assert.Equal((HttpStatusCode.NotFound, "itemNotFound"), (ex.StatusCode, ex.Code));
        Assert.Contains("could not be found", ex.Message, StringComparison.Ordinal);
    }

    private static OneDriveCapability CreateCapability(FakeHttpHandler handler, List<TimeSpan>? delays = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(GraphClient.BaseUrl) };
        var graph = new GraphClient(http, _ => Task.FromResult("token"), (delay, _) =>
        {
            delays?.Add(delay);
            return Task.CompletedTask;
        });
        return new OneDriveCapability(graph);
    }
}
