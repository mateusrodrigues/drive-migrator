using System.Runtime.CompilerServices;
using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using Google.Apis.Drive.v3;
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace DriveMigrator.Providers.Google.Drive;

/// <summary>Google Drive ("My Drive"). Node ids are Drive file ids; the root is "root".</summary>
internal sealed class GoogleDriveCapability(DriveService drive) : IDriveCapability
{
    internal const string FolderMimeType = "application/vnd.google-apps.folder";
    private const string GoogleAppsPrefix = "application/vnd.google-apps.";

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

    public bool SupportsNestedContainers => true;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = drive.Files.List();
        request.Q = $"'{parent?.Id ?? "root"}' in parents and trashed = false";
        request.Fields = "nextPageToken, files(id, name, mimeType, size, modifiedTime)";
        request.PageSize = 1000;
        request.Spaces = "drive";
        request.SupportsAllDrives = true;
        request.IncludeItemsFromAllDrives = true;

        do
        {
            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var file in page.Files ?? [])
            {
                if (ToNode(file) is { } node)
                {
                    yield return node;
                }
            }

            if (page.NextPageToken is not null && page.NextPageToken == request.PageToken)
            {
                throw new InvalidOperationException("Google Drive returned the same page token twice.");
            }

            request.PageToken = page.NextPageToken;
        }
        while (request.PageToken is not null);
    }

    public Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
        => throw TransfersNotAvailable();

    public Task<DriveFileContent> OpenReadAsync(MigrationNode file, ExportFormat? exportAs, CancellationToken cancellationToken = default)
        => throw TransfersNotAvailable();

    public Task<MigrationNode> UploadAsync(
        MigrationNode? parent,
        DriveFileContent content,
        DriveUploadOptions options,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
        => throw TransfersNotAvailable();

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

    // Reading and writing file content arrive with the transfer engine.
    private static NotSupportedException TransfersNotAvailable() => new("Copying Google Drive content is not available yet.");
}
