namespace DriveMigrator.Core.Drive;

public interface IDriveCapability : ICapability
{
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
