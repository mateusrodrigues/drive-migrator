namespace DriveMigrator.Core.Drive;

/// <summary>A file's bytes plus the metadata that travels with it. Owns and disposes <see cref="Content"/>.</summary>
public sealed class DriveFileContent(Stream content, string name, string mimeType) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public string Name { get; } = name;

    public string MimeType { get; } = mimeType;

    /// <summary>Length in bytes when known up front (required by some chunked upload APIs).</summary>
    public long? Length { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ModifiedAt { get; init; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
