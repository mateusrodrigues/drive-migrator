using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace DriveMigrator.Providers.Google.Drive;

/// <summary>Google Drive ("My Drive"). Node ids are Drive file ids; the root is "root".</summary>
internal sealed class GoogleDriveCapability : IDriveCapability
{
    internal const string FolderMimeType = "application/vnd.google-apps.folder";

    /// <summary>Resumable upload chunk size; Google requires a multiple of 256 KiB.</summary>
    internal const int ChunkSize = 40 * 256 * 1024;

    private const string GoogleAppsPrefix = "application/vnd.google-apps.";
    private const string FileFields = "id, name, mimeType, size, modifiedTime";

    /// <summary>Upload MIME types Google can convert, and the native type they become.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ConvertibleTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = GoogleAppsPrefix + "document",
        ["application/msword"] = GoogleAppsPrefix + "document",
        ["application/vnd.oasis.opendocument.text"] = GoogleAppsPrefix + "document",
        ["application/rtf"] = GoogleAppsPrefix + "document",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = GoogleAppsPrefix + "spreadsheet",
        ["application/vnd.ms-excel"] = GoogleAppsPrefix + "spreadsheet",
        ["application/vnd.oasis.opendocument.spreadsheet"] = GoogleAppsPrefix + "spreadsheet",
        ["text/csv"] = GoogleAppsPrefix + "spreadsheet",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = GoogleAppsPrefix + "presentation",
        ["application/vnd.ms-powerpoint"] = GoogleAppsPrefix + "presentation",
        ["application/vnd.oasis.opendocument.presentation"] = GoogleAppsPrefix + "presentation",
    };

    private readonly DriveService _drive;

    public GoogleDriveCapability(DriveService drive)
    {
        _drive = drive;

        GoogleBackOff.Install(drive);
    }

    private static readonly ExportFormat Pdf = new("application/pdf", ".pdf", "PDF");

    /// <summary>Formats each Google-native type can be exported to, preferred format first.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<ExportFormat>> NativeExportFormats =
        new Dictionary<string, IReadOnlyList<ExportFormat>>
        {
            [GoogleAppsPrefix + "document"] =
            [
                new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx", "Word document"),
                new("application/vnd.oasis.opendocument.text", ".odt", "OpenDocument text"),
                Pdf,
            ],
            [GoogleAppsPrefix + "spreadsheet"] =
            [
                new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx", "Excel workbook"),
                new("application/vnd.oasis.opendocument.spreadsheet", ".ods", "OpenDocument spreadsheet"),
                Pdf,
            ],
            [GoogleAppsPrefix + "presentation"] =
            [
                new("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx", "PowerPoint presentation"),
                new("application/vnd.oasis.opendocument.presentation", ".odp", "OpenDocument presentation"),
                Pdf,
            ],
            [GoogleAppsPrefix + "drawing"] =
            [
                new("image/png", ".png", "PNG image"),
                new("image/svg+xml", ".svg", "SVG image"),
                Pdf,
            ],
        };

    public CapabilityKind Kind => CapabilityKind.Drive;

    public string DisplayName => "Google Drive";

    public IReadOnlyList<NativeDocumentType> NativeDocumentTypes { get; } =
    [
        new(GoogleAppsPrefix + "document", "Google Docs", NativeExportFormats[GoogleAppsPrefix + "document"]),
        new(GoogleAppsPrefix + "spreadsheet", "Google Sheets", NativeExportFormats[GoogleAppsPrefix + "spreadsheet"]),
        new(GoogleAppsPrefix + "presentation", "Google Slides", NativeExportFormats[GoogleAppsPrefix + "presentation"]),
        new(GoogleAppsPrefix + "drawing", "Google Drawings", NativeExportFormats[GoogleAppsPrefix + "drawing"]),
    ];

    public bool CanConvertToNativeFormat => true;

    public bool SupportsNestedContainers => true;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = _drive.Files.List();
        request.Q = $"'{parent?.Id ?? "root"}' in parents and trashed = false";
        request.Fields = "nextPageToken, files(id, name, mimeType, size, modifiedTime)";
        request.PageSize = 1000;
        request.Spaces = "drive";
        request.SupportsAllDrives = true;
        request.IncludeItemsFromAllDrives = true;

        do
        {
            // An empty result can come back as an empty body, which the client returns as null.
            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var file in page?.Files ?? [])
            {
                if (ToNode(file) is { } node)
                {
                    yield return node;
                }
            }

            if (page?.NextPageToken is not null && page.NextPageToken == request.PageToken)
            {
                throw new InvalidOperationException("Google Drive returned the same page token twice.");
            }

            request.PageToken = page?.NextPageToken;
        }
        while (request.PageToken is not null);
    }

    public async Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        var request = _drive.Files.Create(new GoogleFile { Name = name, MimeType = FolderMimeType, Parents = [parent?.Id ?? "root"] });
        request.Fields = FileFields;
        request.SupportsAllDrives = true;
        var created = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Google Drive returned nothing when creating the folder '{name}'.");
        return ToNode(created)!;
    }

    public async Task<DriveFileContent> OpenReadAsync(MigrationNode file, ExportFormat? exportAs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var id = Uri.EscapeDataString(file.Id);
        HttpResponseMessage response;
        if (file.RequiresExport)
        {
            if (exportAs is null || !file.ExportFormats.Contains(exportAs))
            {
                throw new ArgumentException($"'{file.Name}' is a Google document and must be exported to one of its formats.", nameof(exportAs));
            }

            response = await GetAsync($"files/{id}/export?mimeType={Uri.EscapeDataString(exportAs.MimeType)}", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Contains("exportSizeLimitExceeded", StringComparison.Ordinal))
            {
                // files.export is limited to 10 MB; the export links serve larger documents.
                response.Dispose();
                response = await GetExportLinkAsync(file, exportAs, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            if (exportAs is not null)
            {
                throw new ArgumentException($"'{file.Name}' is a regular file and cannot be exported.", nameof(exportAs));
            }

            response = await GetAsync($"files/{id}?alt=media&supportsAllDrives=true", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await EnsureSuccessAsync(response, file.Name, cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new DriveFileContent(new HttpResponseStream(stream, response), file.Name, exportAs?.MimeType ?? file.MimeType ?? "application/octet-stream")
            {
                // Exports are generated on the fly, so their size is usually unknown until read.
                Length = response.Content.Headers.ContentLength ?? (exportAs is null ? file.Size : null),
                ModifiedAt = file.ModifiedAt,
            };
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<MigrationNode> UploadAsync(
        MigrationNode? parent,
        DriveFileContent content,
        DriveUploadOptions options,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var metadata = new GoogleFile { ModifiedTimeDateTimeOffset = content.ModifiedAt };
        if (options.ConvertToNativeFormat && ConvertibleTypes.TryGetValue(content.MimeType, out var nativeType))
        {
            metadata.MimeType = nativeType;
        }

        ResumableUpload<GoogleFile, GoogleFile> upload;
        Func<GoogleFile?> responseBody;
        if (options.Replace is { } replace)
        {
            var request = _drive.Files.Update(metadata, replace.Id, content.Content, content.MimeType);
            request.Fields = FileFields;
            request.SupportsAllDrives = true;
            upload = request;
            responseBody = () => request.ResponseBody;
        }
        else
        {
            metadata.Name = content.Name;
            metadata.Parents = [parent?.Id ?? "root"];
            var request = _drive.Files.Create(metadata, content.Content, content.MimeType);
            request.Fields = FileFields;
            request.SupportsAllDrives = true;
            upload = request;
            responseBody = () => request.ResponseBody;
        }

        upload.ChunkSize = ChunkSize;
        if (progress is not null)
        {
            upload.ProgressChanged += p => progress.Report(p.BytesSent);
        }

        var result = await upload.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status != UploadStatus.Completed)
        {
            throw new IOException($"Uploading '{content.Name}' to Google Drive failed: {result.Exception?.Message ?? result.Status.ToString()}", result.Exception);
        }

        return ToNode(responseBody() ?? throw new IOException($"Google Drive returned no file for '{content.Name}'."))
            ?? throw new IOException($"Google Drive returned an unsupported file type for '{content.Name}'.");
    }

    internal static MigrationNode? ToNode(GoogleFile file)
    {
        if (file.MimeType == FolderMimeType)
        {
            return new MigrationNode(file.Id, file.Name, NodeKind.Folder) { ModifiedAt = file.ModifiedTimeDateTimeOffset };
        }

        IReadOnlyList<ExportFormat> exports = [];
        if (file.MimeType?.StartsWith(GoogleAppsPrefix, StringComparison.Ordinal) == true
            && !NativeExportFormats.TryGetValue(file.MimeType, out exports!))
        {
            // Forms, Sites, My Maps, shortcuts and similar have no exportable content. Not supported yet.
            return null;
        }

        return new MigrationNode(file.Id, file.Name, NodeKind.File)
        {
            Size = file.Size,
            ModifiedAt = file.ModifiedTimeDateTimeOffset,
            MimeType = file.MimeType,
            ExportFormats = exports,
        };
    }

    private Task<HttpResponseMessage> GetAsync(string relativeUrl, CancellationToken cancellationToken)
        => _drive.HttpClient.GetAsync(new Uri(new Uri(_drive.BaseUri), relativeUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private async Task<HttpResponseMessage> GetExportLinkAsync(MigrationNode file, ExportFormat format, CancellationToken cancellationToken)
    {
        var request = _drive.Files.Get(file.Id);
        request.Fields = "exportLinks";
        request.SupportsAllDrives = true;
        var links = (await request.ExecuteAsync(cancellationToken).ConfigureAwait(false))?.ExportLinks;
        if (links is null || !links.TryGetValue(format.MimeType, out var link))
        {
            throw new IOException($"'{file.Name}' is too large to export as {format.DisplayName}.");
        }

        return await _drive.HttpClient.GetAsync(new Uri(link), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string name, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (body.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var text))
            {
                message = text.GetString() ?? message;
            }
        }
        catch (JsonException)
        {
            // Not a JSON error body.
        }

        throw new HttpRequestException($"Google Drive could not download '{name}': {message}", null, response.StatusCode);
    }
}
