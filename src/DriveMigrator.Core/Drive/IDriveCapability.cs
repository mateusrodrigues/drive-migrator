namespace DriveMigrator.Core.Drive;

public interface IDriveCapability : ICapability
{
    /// <summary>
    /// Provider-native document types this drive can contain (e.g. Google Docs), with the formats they can be
    /// exported to. Empty for drives that only hold ordinary files.
    /// </summary>
    IReadOnlyList<NativeDocumentType> NativeDocumentTypes => [];

    /// <summary>Whether uploads can be converted into native documents (<see cref="DriveUploadOptions.ConvertToNativeFormat"/>).</summary>
    bool CanConvertToNativeFormat => false;

    /// <summary>
    /// Opens a file for reading. <paramref name="exportAs"/> must be one of the node's
    /// <see cref="MigrationNode.ExportFormats"/> when <see cref="MigrationNode.RequiresExport"/> is true, and null otherwise.
    /// </summary>
    Task<DriveFileContent> OpenReadAsync(MigrationNode file, ExportFormat? exportAs, CancellationToken cancellationToken = default);

    /// <summary>Uploads a file into <paramref name="parent"/> (the drive root when null).</summary>
    Task<MigrationNode> UploadAsync(
        MigrationNode? parent,
        DriveFileContent content,
        DriveUploadOptions options,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}
