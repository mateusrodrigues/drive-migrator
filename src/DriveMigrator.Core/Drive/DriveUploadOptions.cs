namespace DriveMigrator.Core.Drive;

public sealed record DriveUploadOptions
{
    public static DriveUploadOptions Default { get; } = new();

    /// <summary>Existing item to overwrite instead of creating a new one.</summary>
    public MigrationNode? Replace { get; init; }

    /// <summary>Ask the destination to convert the file to its native format (e.g. docx to Google Docs) when it can.</summary>
    public bool ConvertToNativeFormat { get; init; }
}
