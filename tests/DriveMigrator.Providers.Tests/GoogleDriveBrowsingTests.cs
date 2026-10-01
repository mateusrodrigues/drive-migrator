using System.Web;
using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using DriveMigrator.Providers.Google.Drive;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;

namespace DriveMigrator.Providers.Tests;

public class GoogleDriveBrowsingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListsRootAcrossPagesAndMapsTypes()
    {
        var handler = new FakeHttpHandler(request =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            return query["pageToken"] is null
                ? FakeHttpHandler.Json("""
                    {
                      "nextPageToken": "p2",
                      "files": [
                        { "id": "f1", "name": "Taxes", "mimeType": "application/vnd.google-apps.folder" },
                        { "id": "d1", "name": "Budget", "mimeType": "application/vnd.google-apps.spreadsheet", "modifiedTime": "2026-02-03T04:05:06.000Z" }
                      ]
                    }
                    """)
                : FakeHttpHandler.Json("""
                    {
                      "files": [
                        { "id": "b1", "name": "scan.pdf", "mimeType": "application/pdf", "size": "2048" },
                        { "id": "x1", "name": "Survey", "mimeType": "application/vnd.google-apps.form" }
                      ]
                    }
                    """);
        });
        var drive = CreateCapability(handler);

        var nodes = await drive.GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Collection(
            nodes,
            n => Assert.Equal(("f1", "Taxes", NodeKind.Folder, false), (n.Id, n.Name, n.Kind, n.RequiresExport)),
            n =>
            {
                Assert.Equal(("d1", NodeKind.File, true), (n.Id, n.Kind, n.RequiresExport));
                Assert.Equal([".xlsx", ".ods", ".pdf"], n.ExportFormats.Select(f => f.FileExtension));
                Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), n.ModifiedAt);
            },
            n => Assert.Equal(("b1", (long?)2048, false), (n.Id, n.Size, n.RequiresExport)));

        Assert.Equal(2, handler.Requests.Count);
        var first = HttpUtility.ParseQueryString(handler.Requests[0].RequestUri!.Query);
        Assert.Equal("'root' in parents and trashed = false", first["q"]);
        Assert.Equal("p2", HttpUtility.ParseQueryString(handler.Requests[1].RequestUri!.Query)["pageToken"]);
    }

    [Fact]
    public async Task Listing_RequestsAndMapsChecksums()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {
              "files": [
                { "id": "b1", "name": "scan.pdf", "mimeType": "application/pdf", "size": "3",
                  "md5Checksum": "900150983CD24FB0D6963F7D28E17F72", "sha1Checksum": "a9993e364706816aba3e25717850c26c9cd0d89d",
                  "sha256Checksum": "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" },
                { "id": "d1", "name": "Budget", "mimeType": "application/vnd.google-apps.spreadsheet" }
              ]
            }
            """));

        var nodes = await CreateCapability(handler).GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Equal(
            new FileHashes("900150983cd24fb0d6963f7d28e17f72", "a9993e364706816aba3e25717850c26c9cd0d89d", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
            nodes[0].Hashes);
        Assert.Null(nodes[1].Hashes);
        var fields = HttpUtility.ParseQueryString(Assert.Single(handler.Requests).RequestUri!.Query)["fields"]!;
        Assert.Contains("md5Checksum", fields, StringComparison.Ordinal);
        Assert.Contains("sha1Checksum", fields, StringComparison.Ordinal);
        Assert.Contains("sha256Checksum", fields, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChildrenOfFolderQueryByFolderId()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "files": [] }"""));
        var drive = CreateCapability(handler);

        await drive.GetChildrenAsync(new MigrationNode("abc123", "Taxes", NodeKind.Folder), Ct).ToListAsync(Ct);

        Assert.Equal("'abc123' in parents and trashed = false", HttpUtility.ParseQueryString(Assert.Single(handler.Requests).RequestUri!.Query)["q"]);
    }

    [Fact]
    public async Task EmptyFolder_WithEmptyResponseBody()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(string.Empty) });

        Assert.Empty(await CreateCapability(handler).GetChildrenAsync(null, Ct).ToListAsync(Ct));
    }

    private static GoogleDriveCapability CreateCapability(FakeHttpHandler handler)
        => new(new DriveService(new BaseClientService.Initializer
        {
            HttpClientFactory = new FakeClientFactory(handler),
            ApplicationName = "tests",
        }));

    private sealed class FakeClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }
}
