using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.Engine;

/// <summary>Copies one file between drives: export choice, name conflicts, and streaming upload.</summary>
internal static class DriveCopier
{
    public static async Task<ItemOutcome> CopyAsync(
        IDriveCapability source,
        IDriveCapability destination,
        TargetIndex index,
        MigrationNode file,
        MigrationNode? targetParent,
        TransferOptions options,
        IProgress<long> progress,
        CancellationToken cancellationToken)
    {
        ExportFormat? export = null;
        if (file.RequiresExport)
        {
            if (!options.NativeExports.TryGetValue(file.MimeType ?? string.Empty, out export))
            {
                export = file.ExportFormats[0];
            }
            else if (export is null)
            {
                return ItemOutcome.Skipped("Not copied: this document type was set to be skipped.");
            }
        }

        var name = file.Name;
        if (export is not null && !name.EndsWith(export.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            name += export.FileExtension;
        }

        name = destination.ToValidName(name);
        var claim = await index.ClaimAsync(targetParent, name, cancellationToken).ConfigureAwait(false);
        MigrationNode? replace = null;
        if (claim.Existing is { } existing)
        {
            if (existing.IsContainer || options.Conflicts == ConflictPolicy.KeepBoth)
            {
                claim = await index.ClaimUniqueAsync(targetParent, name, cancellationToken).ConfigureAwait(false);
            }
            else if (options.Conflicts == ConflictPolicy.Skip)
            {
                return ItemOutcome.Skipped("Skipped: a file with this name already exists.", existing);
            }
            else
            {
                replace = existing;
            }
        }

        try
        {
            var content = await source.OpenReadAsync(file, export, cancellationToken).ConfigureAwait(false);
            await using (content.ConfigureAwait(false))
            {
                var upload = await WithKnownLengthAsync(content, claim.Name, cancellationToken).ConfigureAwait(false);
                await using (upload.ConfigureAwait(false))
                {
                    var uploadOptions = new DriveUploadOptions
                    {
                        Replace = replace,
                        ConvertToNativeFormat = options.ConvertToNativeFormat && destination.CanConvertToNativeFormat,
                    };
                    var uploaded = await destination.UploadAsync(targetParent, upload, uploadOptions, progress, cancellationToken).ConfigureAwait(false);

                    if (replace is null)
                    {
                        claim.Complete(uploaded);
                    }
                    else
                    {
                        TargetIndex.Update(claim, uploaded);
                    }

                    return ItemOutcome.Done(uploaded, upload.Length ?? uploaded.Size ?? 0);
                }
            }
        }
        catch
        {
            claim.Release();
            throw;
        }
    }

    /// <summary>
    /// Chunked upload APIs need the total size up front. When the source can't say (e.g. exported Google Docs),
    /// buffer the content in a temporary file that is deleted when closed.
    /// </summary>
    private static async Task<DriveFileContent> WithKnownLengthAsync(DriveFileContent content, string name, CancellationToken cancellationToken)
    {
        if (content.Length is not null)
        {
            // Share the stream under the destination name; the caller disposes the original.
            return new DriveFileContent(new NonClosingStream(content.Content), name, content.MimeType)
            {
                Length = content.Length,
                CreatedAt = content.CreatedAt,
                ModifiedAt = content.ModifiedAt,
            };
        }

        var buffer = new FileStream(
            Path.Combine(Path.GetTempPath(), $"drivemigrator-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await content.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
        }
        catch
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new DriveFileContent(buffer, name, content.MimeType)
        {
            Length = buffer.Length,
            CreatedAt = content.CreatedAt,
            ModifiedAt = content.ModifiedAt,
        };
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed record ItemOutcome(ItemStatus Status, MigrationNode? Target, string? Message, long Bytes)
{
    public static ItemOutcome Done(MigrationNode target, long bytes) => new(ItemStatus.Done, target, null, bytes);

    public static ItemOutcome Skipped(string reason, MigrationNode? existing = null) => new(ItemStatus.Skipped, existing, reason, 0);
}
