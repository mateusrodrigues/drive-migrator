using DriveMigrator.Core;
using DriveMigrator.Core.Drive;

namespace DriveMigrator.Testing;

public sealed class FakeDriveCapability() : InMemoryCapability(CapabilityKind.Drive, NodeKind.Folder, supportsNestedContainers: true), IDriveCapability
{
    public IReadOnlyList<NativeDocumentType> NativeDocumentTypes { get; set; } = [];

    public bool CanConvertToNativeFormat { get; set; }

    /// <summary>Characters the fake rejects in names (replaced by '_' in <see cref="ToValidName"/>), to mimic OneDrive.</summary>
    public string InvalidNameCharacters { get; set; } = string.Empty;

    /// <summary>Called before each upload with the file name; throw or block to simulate failures and slow transfers.</summary>
    public Func<string, CancellationToken, Task>? OnUpload { get; set; }

    private int _uploadCount;

    public int UploadCount => _uploadCount;

    public string ToValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Concat(name.Select(c => InvalidNameCharacters.Contains(c, StringComparison.Ordinal) ? '_' : c));
    }

    public MigrationNode AddFile(MigrationNode? parent, string name, byte[] content, string mimeType = "application/octet-stream")
        => Add(parent, FileNode(NewId(), name, mimeType, content.Length), new StoredFile(content, ConvertedToNative: false, Exports: null));

    /// <summary>Adds a provider-native document that can only be read by exporting it to one of <paramref name="exports"/>.</summary>
    public MigrationNode AddNativeDocument(MigrationNode? parent, string name, string mimeType, params (ExportFormat Format, byte[] Content)[] exports)
    {
        ArgumentOutOfRangeException.ThrowIfZero(exports.Length);
        var node = new MigrationNode(NewId(), name, NodeKind.File)
        {
            MimeType = mimeType,
            ExportFormats = [.. exports.Select(e => e.Format)],
        };
        return Add(parent, node, new StoredFile([], ConvertedToNative: false, exports.ToDictionary(e => e.Format.MimeType, e => e.Content)));
    }

    public byte[] GetContent(MigrationNode file) => GetPayload<StoredFile>(file).Content;

    public bool WasConvertedToNative(MigrationNode file) => GetPayload<StoredFile>(file).ConvertedToNative;

    public Task<DriveFileContent> OpenReadAsync(MigrationNode file, ExportFormat? exportAs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = GetPayload<StoredFile>(file);

        byte[] bytes;
        string name = file.Name;
        string mimeType = file.MimeType ?? "application/octet-stream";
        if (stored.Exports is not null)
        {
            if (exportAs is null || !stored.Exports.TryGetValue(exportAs.MimeType, out bytes!))
            {
                throw new ArgumentException($"'{file.Name}' is a native document and must be exported to one of its export formats.", nameof(exportAs));
            }

            name += exportAs.FileExtension;
            mimeType = exportAs.MimeType;
        }
        else
        {
            if (exportAs is not null)
            {
                throw new ArgumentException($"'{file.Name}' is a regular file and cannot be exported.", nameof(exportAs));
            }

            bytes = stored.Content;
        }

        // Like Google Drive, exports don't announce their size up front.
        var content = new DriveFileContent(new MemoryStream(bytes, writable: false), name, mimeType)
        {
            Length = stored.Exports is null ? bytes.Length : null,
            ModifiedAt = file.ModifiedAt,
        };
        return Task.FromResult(content);
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
        if (OnUpload is not null)
        {
            await OnUpload(content.Name, cancellationToken).ConfigureAwait(false);
        }

        if (content.Name.Any(c => InvalidNameCharacters.Contains(c, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"'{content.Name}' contains characters this drive does not allow.", nameof(content));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.Content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            progress?.Report(buffer.Length);
        }

        Interlocked.Increment(ref _uploadCount);
        var bytes = buffer.ToArray();
        var node = FileNode(NewId(), content.Name, content.MimeType, bytes.Length) with { ModifiedAt = content.ModifiedAt };
        var stored = new StoredFile(bytes, options.ConvertToNativeFormat, Exports: null);

        return options.Replace is { } existing
            ? Replace(existing, node, stored)
            : Add(parent, node, stored);
    }

    private static MigrationNode FileNode(string id, string name, string mimeType, long size)
        => new(id, name, NodeKind.File) { MimeType = mimeType, Size = size };

    private sealed record StoredFile(byte[] Content, bool ConvertedToNative, Dictionary<string, byte[]>? Exports);
}
