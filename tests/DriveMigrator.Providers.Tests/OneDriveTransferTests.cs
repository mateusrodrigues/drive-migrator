using System.Net;
using System.Text.Json;
using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using DriveMigrator.Providers.Microsoft.Drive;
using DriveMigrator.Providers.Microsoft.Graph;

namespace DriveMigrator.Providers.Tests;

public class OneDriveTransferTests
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";
    private static readonly MigrationNode Docs = new("drives/d1/items/F1", "Docs", NodeKind.Folder);
    private static readonly DateTimeOffset Modified = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("Q: why?*.txt", "Q_ why__.txt")]
    [InlineData("a/b\\c|d<e>\"f", "a_b_c_d_e__f")]
    [InlineData("  padded  ", "padded")]
    [InlineData("ends with dot.", "ends with dot_")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("~$lock.docx", "_~$lock.docx")]
    public void ToValidName_ReplacesWhatOneDriveRejects(string name, string expected)
        => Assert.Equal(expected, Create(new FakeHttpHandler(_ => throw new InvalidOperationException())).ToValidName(name));

    [Fact]
    public async Task CreateContainer_PostsFolderThatFailsOnConflict()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "id": "N1", "name": "New", "folder": {}, "parentReference": { "driveId": "d1" } }""", HttpStatusCode.Created));

        var created = await Create(handler).CreateContainerAsync(Docs, "New", Ct);

        Assert.Equal(("drives/d1/items/N1", NodeKind.Folder), (created.Id, created.Kind));
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, Graph + "drives/d1/items/F1/children"), (request.Method, request.Url));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("New", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("fail", body.RootElement.GetProperty("@microsoft.graph.conflictBehavior").GetString());
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("folder").ValueKind);
    }

    [Fact]
    public async Task OpenRead_StreamsContent()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Bytes([1, 2, 3]));
        var file = new MigrationNode("drives/d1/items/A1", "a.bin", NodeKind.File) { Size = 3, MimeType = "application/x-test" };

        await using var content = await Create(handler).OpenReadAsync(file, null, Ct);
        using var copy = new MemoryStream();
        await content.Content.CopyToAsync(copy, Ct);

        Assert.Equal([1, 2, 3], copy.ToArray());
        Assert.Equal((3L, "application/x-test"), (content.Length!.Value, content.MimeType));
        Assert.Equal(Graph + "drives/d1/items/A1/content", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task SmallUpload_PutsThenSetsTimestamps()
    {
        var handler = new FakeHttpHandler(r => r.Method == HttpMethod.Put
            ? FakeHttpHandler.Json("""{ "id": "U1", "name": "a?b.txt", "size": 3, "file": {}, "parentReference": { "driveId": "d1" } }""", HttpStatusCode.Created)
            : FakeHttpHandler.Json("""{ "id": "U1", "name": "a?b.txt", "size": 3, "file": { "hashes": { "quickXorHash": "eAAAAAAAAAAAAAAAAwAAAAAAAAA=" } }, "lastModifiedDateTime": "2024-05-06T07:08:09Z", "parentReference": { "driveId": "d1" } }"""));

        var uploaded = await Create(handler).UploadAsync(Docs, Content([7, 8, 9], "a?b.txt"), DriveUploadOptions.Default, cancellationToken: Ct);

        Assert.Equal("drives/d1/items/U1", uploaded.Id);
        Assert.Equal(Modified, uploaded.ModifiedAt);
        Assert.Equal("eAAAAAAAAAAAAAAAAwAAAAAAAAA=", uploaded.Hashes?.QuickXor);
        Assert.Collection(
            handler.Requests,
            put =>
            {
                Assert.Equal(HttpMethod.Put, put.Method);
                Assert.Equal(Graph + "drives/d1/items/F1:/a%3Fb.txt:/content?@microsoft.graph.conflictBehavior=fail", put.Url);
                Assert.Equal([7, 8, 9], put.Body);
                Assert.Equal("text/plain", put.ContentType);
            },
            patch =>
            {
                Assert.Equal((HttpMethod.Patch, Graph + "drives/d1/items/U1"), (patch.Method, patch.Url));
                Assert.Contains("\"lastModifiedDateTime\":\"2024-05-06T07:08:09.0000000Z\"", patch.BodyText, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task SmallUpload_Replace_WritesToExistingItem()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "id": "X1", "name": "a.txt", "file": {}, "parentReference": { "driveId": "d1" } }"""));
        var existing = new MigrationNode("drives/d1/items/X1", "a.txt", NodeKind.File);
        var content = new DriveFileContent(new MemoryStream([1]), "a.txt", "text/plain") { Length = 1 };

        await Create(handler).UploadAsync(Docs, content, new DriveUploadOptions { Replace = existing }, cancellationToken: Ct);

        Assert.Equal(Graph + "drives/d1/items/X1/content", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task LargeUpload_UsesSessionChunksWithoutToken_AndRetriesTransientChunkFailures()
    {
        var length = OneDriveCapability.ChunkSize + 10;
        var data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var chunkCalls = 0;
        var handler = new FakeHttpHandler(r => r switch
        {
            { Method.Method: "POST" } => FakeHttpHandler.Json("""{ "uploadUrl": "https://upload.example/session1" }"""),
            { Method.Method: "PUT" } when ++chunkCalls == 1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            { Method.Method: "PUT" } when r.ContentRange!.StartsWith("bytes 0-", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.Accepted),
            { Method.Method: "PUT" } => FakeHttpHandler.Json("""{ "id": "L1", "name": "big.bin", "size": 1, "file": {}, "parentReference": { "driveId": "d1" } }""", HttpStatusCode.Created),
            _ => throw new InvalidOperationException(r.Url),
        });
        var progress = new List<long>();

        var uploaded = await Create(handler).UploadAsync(Docs, Content(data, "big.bin"), DriveUploadOptions.Default, new SyncProgress(progress), Ct);

        Assert.Equal("drives/d1/items/L1", uploaded.Id);
        var session = handler.Requests[0];
        Assert.Equal(Graph + "drives/d1/items/F1:/big.bin:/createUploadSession", session.Url);
        Assert.Contains("\"@microsoft.graph.conflictBehavior\":\"fail\"", session.BodyText, StringComparison.Ordinal);
        Assert.Contains("\"lastModifiedDateTime\"", session.BodyText, StringComparison.Ordinal);

        var chunks = handler.Requests.Skip(1).ToList();
        Assert.All(chunks, c => Assert.Null(c.Authorization));
        Assert.All(chunks, c => Assert.Equal("https://upload.example/session1", c.Url));
        Assert.Equal(
            [$"bytes 0-{OneDriveCapability.ChunkSize - 1}/{length}", $"bytes 0-{OneDriveCapability.ChunkSize - 1}/{length}", $"bytes {OneDriveCapability.ChunkSize}-{length - 1}/{length}"],
            chunks.Select(c => c.ContentRange));
        Assert.Equal(data, chunks[1].Body!.Concat(chunks[2].Body!).ToArray());
        Assert.Equal([OneDriveCapability.ChunkSize, (long)length, length], progress);
    }

    [Fact]
    public async Task LargeUpload_FailedChunk_CancelsSession()
    {
        var length = OneDriveCapability.SimpleUploadLimit + 1;
        var handler = new FakeHttpHandler(r => r.Method.Method switch
        {
            "POST" => FakeHttpHandler.Json("""{ "uploadUrl": "https://upload.example/s2" }"""),
            "PUT" => FakeHttpHandler.Json("""{ "error": { "code": "invalidRange" } }""", HttpStatusCode.RequestedRangeNotSatisfiable),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });

        await Assert.ThrowsAsync<GraphException>(() => Create(handler).UploadAsync(Docs, Content(new byte[length], "x.bin"), DriveUploadOptions.Default, cancellationToken: Ct));

        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
        Assert.Equal("https://upload.example/s2", handler.Requests[^1].Url);
    }

    [Fact]
    public async Task UploadWithoutLength_IsRejected()
    {
        var content = new DriveFileContent(new MemoryStream([1]), "a", "text/plain");

        await Assert.ThrowsAsync<ArgumentException>(() => Create(new FakeHttpHandler(_ => throw new InvalidOperationException())).UploadAsync(null, content, DriveUploadOptions.Default, cancellationToken: Ct));
    }

    private static DriveFileContent Content(byte[] data, string name)
        => new(new MemoryStream(data), name, "text/plain") { Length = data.Length, ModifiedAt = Modified };

    private static OneDriveCapability Create(FakeHttpHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(GraphClient.BaseUrl) };
        return new OneDriveCapability(new GraphClient(http, _ => Task.FromResult("token"), (_, _) => Task.CompletedTask));
    }

    private sealed class SyncProgress(List<long> reports) : IProgress<long>
    {
        public void Report(long value) => reports.Add(value);
    }
}
