using System.Net;
using System.Text.Json;
using System.Web;
using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using DriveMigrator.Providers.Google.Drive;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;

namespace DriveMigrator.Providers.Tests;

public class GoogleDriveTransferTests
{
    private const string Api = "https://www.googleapis.com/drive/v3/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateContainer_PostsFolderUnderParent()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "id": "n1", "name": "New", "mimeType": "application/vnd.google-apps.folder" }"""));

        var created = await Create(handler).CreateContainerAsync(new MigrationNode("p1", "Parent", NodeKind.Folder), "New", Ct);

        Assert.Equal(("n1", NodeKind.Folder), (created.Id, created.Kind));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("New", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("application/vnd.google-apps.folder", body.RootElement.GetProperty("mimeType").GetString());
        Assert.Equal("p1", body.RootElement.GetProperty("parents")[0].GetString());
    }

    [Fact]
    public async Task OpenRead_RegularFileDownloadsMedia()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Bytes([4, 5]));
        var file = new MigrationNode("f1", "a.bin", NodeKind.File) { Size = 2, MimeType = "application/x-test" };

        await using var content = await Create(handler).OpenReadAsync(file, null, Ct);

        Assert.Equal([4, 5], await ReadAllAsync(content));
        Assert.Equal(Api + "files/f1?alt=media&supportsAllDrives=true", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task OpenRead_NativeDocumentExportsInChosenFormat()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Bytes([9]));
        var formats = GoogleDriveCapability.NativeExportFormats["application/vnd.google-apps.document"];
        var doc = new MigrationNode("d1", "Report", NodeKind.File) { MimeType = "application/vnd.google-apps.document", ExportFormats = formats };

        await using var content = await Create(handler).OpenReadAsync(doc, formats[0], Ct);

        Assert.Equal([9], await ReadAllAsync(content));
        Assert.Equal(formats[0].MimeType, content.MimeType);
        var request = Assert.Single(handler.Requests);
        Assert.StartsWith(Api + "files/d1/export?", request.Url, StringComparison.Ordinal);
        Assert.Equal(formats[0].MimeType, HttpUtility.ParseQueryString(request.RequestUri.Query)["mimeType"]);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).OpenReadAsync(doc, null, Ct));
    }

    [Fact]
    public async Task OpenRead_LargeDocumentFallsBackToExportLinks()
    {
        var pdf = new ExportFormat("application/pdf", ".pdf", "PDF");
        var handler = new FakeHttpHandler(r => r.Url switch
        {
            var u when u.Contains("/export?", StringComparison.Ordinal) => FakeHttpHandler.Json(
                """{ "error": { "code": 403, "message": "This file is too large to be exported.", "errors": [ { "reason": "exportSizeLimitExceeded" } ] } }""",
                HttpStatusCode.Forbidden),
            var u when u.Contains("fields=exportLinks", StringComparison.Ordinal) => FakeHttpHandler.Json(
                """{ "exportLinks": { "application/pdf": "https://docs.example/export/big.pdf" } }"""),
            "https://docs.example/export/big.pdf" => FakeHttpHandler.Bytes([1, 2]),
            var u => throw new InvalidOperationException(u),
        });
        var doc = new MigrationNode("big", "Big", NodeKind.File) { MimeType = "application/vnd.google-apps.document", ExportFormats = [pdf] };

        await using var content = await Create(handler).OpenReadAsync(doc, pdf, Ct);

        Assert.Equal([1, 2], await ReadAllAsync(content));
    }

    [Fact]
    public async Task OpenRead_ErrorsCarryGoogleMessage()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "error": { "message": "File not found: f9." } }""", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Create(handler).OpenReadAsync(new MigrationNode("f9", "gone.txt", NodeKind.File), null, Ct));

        Assert.Contains("File not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_ResumableCreateWithConversionAndTimestamp()
    {
        var handler = new FakeHttpHandler(r =>
        {
            if (r.Method == HttpMethod.Post)
            {
                var start = new HttpResponseMessage(HttpStatusCode.OK);
                start.Headers.Location = new Uri("https://upload.example/resume1");
                return start;
            }

            return FakeHttpHandler.Json("""{ "id": "u1", "name": "notes.docx", "mimeType": "application/vnd.google-apps.document" }""");
        });
        var docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        var content = new DriveFileContent(new MemoryStream([1, 2, 3]), "notes.docx", docx)
        {
            Length = 3,
            ModifiedAt = new DateTimeOffset(2023, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };
        var progress = new List<long>();

        var uploaded = await Create(handler).UploadAsync(
            new MigrationNode("p1", "Parent", NodeKind.Folder), content, new DriveUploadOptions { ConvertToNativeFormat = true }, new SyncProgress(progress), Ct);

        Assert.Equal("u1", uploaded.Id);
        Assert.True(uploaded.RequiresExport);

        var start = handler.Requests[0];
        Assert.Contains("uploadType=resumable", start.Url, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(start.Body);
        Assert.Equal("notes.docx", metadata.RootElement.GetProperty("name").GetString());
        Assert.Equal("application/vnd.google-apps.document", metadata.RootElement.GetProperty("mimeType").GetString());
        Assert.Equal("p1", metadata.RootElement.GetProperty("parents")[0].GetString());
        Assert.StartsWith("2023-01-02T03:04:05", metadata.RootElement.GetProperty("modifiedTime").GetString(), StringComparison.Ordinal);

        var put = handler.Requests[1];
        Assert.Equal("https://upload.example/resume1", put.Url);
        Assert.Equal([1, 2, 3], put.Body!);
        Assert.Equal(3, progress[^1]);
    }

    [Fact]
    public async Task Upload_WithoutConversionKeepsOriginalType()
    {
        var handler = new FakeHttpHandler(r =>
        {
            if (r.Method == HttpMethod.Post)
            {
                var start = new HttpResponseMessage(HttpStatusCode.OK);
                start.Headers.Location = new Uri("https://upload.example/resume2");
                return start;
            }

            return FakeHttpHandler.Json("""{ "id": "u2", "name": "a.docx", "mimeType": "application/x" }""");
        });
        var content = new DriveFileContent(new MemoryStream([1]), "a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document") { Length = 1 };

        await Create(handler).UploadAsync(null, content, DriveUploadOptions.Default, cancellationToken: Ct);

        using var metadata = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.False(metadata.RootElement.TryGetProperty("mimeType", out _));
        Assert.Equal("root", metadata.RootElement.GetProperty("parents")[0].GetString());
    }

    private static async Task<byte[]> ReadAllAsync(DriveFileContent content)
    {
        using var copy = new MemoryStream();
        await content.Content.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private static GoogleDriveCapability Create(FakeHttpHandler handler)
        => new(new DriveService(new BaseClientService.Initializer
        {
            HttpClientFactory = new FakeClientFactory(handler),
            ApplicationName = "tests",

            // The real client gzips request bodies; turn that off so tests can read them.
            GZipEnabled = false,
        }));

    private sealed class FakeClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private sealed class SyncProgress(List<long> reports) : IProgress<long>
    {
        public void Report(long value) => reports.Add(value);
    }
}
