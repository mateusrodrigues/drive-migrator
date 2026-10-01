using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.Engine;

/// <summary>
/// Copies one file between drives: export choice, name conflicts, streaming upload, and checking the copy against the
/// checksums both services report.
/// </summary>
internal static class DriveCopier
{
    /// <param name="resolution">Overrides the job's conflict policy for this file (a mismatched file copied again).</param>
    /// <param name="replaceTarget">With <see cref="ConflictPolicy.Overwrite"/>, the destination file to overwrite.</param>
    public static async Task<ItemOutcome> CopyAsync(
        IDriveCapability source,
        IDriveCapability destination,
        TargetIndex index,
        MigrationNode file,
        MigrationNode? targetParent,
        TransferOptions options,
        ConflictPolicy? resolution,
        MigrationNode? replaceTarget,
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

        var conflicts = resolution ?? options.Conflicts;
        if (conflicts != ConflictPolicy.Overwrite)
        {
            replaceTarget = null;
        }

        // Overwriting a recorded file: claim its name, which may differ from this one (e.g. a "name (1).ext" copy).
        name = replaceTarget?.Name ?? destination.ToValidName(name);
        var claim = await index.ClaimAsync(targetParent, name, cancellationToken).ConfigureAwait(false);
        MigrationNode? replace = null;
        if (claim.Existing is { } existing)
        {
            if (existing.IsContainer || conflicts == ConflictPolicy.KeepBoth)
            {
                claim = await index.ClaimUniqueAsync(targetParent, name, cancellationToken).ConfigureAwait(false);
            }
            else if (replaceTarget is not null && existing.Id != replaceTarget.Id)
            {
                return ItemOutcome.Skipped("Not copied again: a different file has taken this name in the destination.", existing);
            }
            else if (conflicts == ConflictPolicy.Skip)
            {
                return SkipExisting(file, existing, options.CompareExisting && resolution is null);
            }
            else
            {
                replace = existing;
            }
        }

        MigrationNode uploaded;
        long length;
        FileHashes? sent;
        bool converted;
        try
        {
            var content = await source.OpenReadAsync(file, export, cancellationToken).ConfigureAwait(false);
            await using (content.ConfigureAwait(false))
            {
                var upload = await WithKnownLengthAsync(content, claim.Name, cancellationToken).ConfigureAwait(false);
                await using (upload.ConfigureAwait(false))
                {
                    using var hashes = options.VerifyHashes ? new FileHashAccumulator() : null;
                    var body = hashes is null ? upload : WithContent(upload, new HashingStream(upload.Content, hashes));
                    var uploadOptions = new DriveUploadOptions
                    {
                        Replace = replace,
                        ConvertToNativeFormat = options.ConvertToNativeFormat && destination.CanConvertToNativeFormat,
                    };
                    uploaded = await destination.UploadAsync(targetParent, body, uploadOptions, progress, cancellationToken).ConfigureAwait(false);
                    length = upload.Length ?? uploaded.Size ?? 0;
                    sent = (body.Content as HashingStream)?.GetHashes(length);
                    converted = uploadOptions.ConvertToNativeFormat;

                    if (replace is null)
                    {
                        claim.Complete(uploaded);
                    }
                    else
                    {
                        TargetIndex.Update(claim, uploaded);
                    }
                }
            }
        }
        catch
        {
            claim.Release();
            throw;
        }

        if (sent is not null)
        {
            await VerifyAsync(destination, targetParent, export is null ? file.Hashes : null, sent, uploaded, converted, cancellationToken).ConfigureAwait(false);
        }

        return ItemOutcome.Done(uploaded, length);
    }

    /// <summary>A same-named file is already in the destination and the policy is to leave it alone.</summary>
    private static ItemOutcome SkipExisting(MigrationNode file, MigrationNode existing, bool compare)
    {
        if (!compare)
        {
            return ItemOutcome.Skipped("Skipped: a file with this name already exists.", existing);
        }

        var comparison = FileHashes.Compare(file.Hashes, existing.Hashes);
        return comparison.Outcome switch
        {
            HashOutcome.Match => ItemOutcome.Skipped("Skipped: an identical file is already in the destination.", existing),
            HashOutcome.Mismatch => throw new ChecksumMismatchException(
                FailureKind.DiffersFromExisting,
                existing,
                $"The file with this name in the destination is different ({comparison.Algorithm} differs).",
                comparison),
            _ => ItemOutcome.Skipped("Skipped: a file with this name already exists (there is no checksum to compare them by).", existing),
        };
    }

    /// <summary>
    /// Compares what was sent with the source's checksums (a damaged download, or a file that changed meanwhile) and
    /// with the destination's (a damaged upload). Sides without a common algorithm are not compared.
    /// </summary>
    private static async Task VerifyAsync(
        IDriveCapability destination,
        MigrationNode? targetParent,
        FileHashes? sourceHashes,
        FileHashes sent,
        MigrationNode uploaded,
        bool converted,
        CancellationToken cancellationToken)
    {
        var download = FileHashes.Compare(sourceHashes, sent);
        if (download.Outcome == HashOutcome.Mismatch)
        {
            throw new ChecksumMismatchException(
                FailureKind.ChecksumMismatch,
                uploaded,
                $"The copy doesn't match the original ({download.Algorithm} differs): it was damaged while downloading, or the file changed during the copy.",
                download);
        }

        var reported = uploaded.Hashes;
        if (reported is null && !converted)
        {
            // Some services (OneDrive) compute checksums a moment after the upload; look once more.
            var found = await destination.FindChildAsync(targetParent, uploaded.Name, cancellationToken).ConfigureAwait(false);
            reported = found is not null && found.Id == uploaded.Id ? found.Hashes : null;
        }

        var upload = FileHashes.Compare(sent, reported);
        if (upload.Outcome == HashOutcome.Mismatch)
        {
            throw new ChecksumMismatchException(
                FailureKind.ChecksumMismatch,
                uploaded,
                $"The copy doesn't match the original ({upload.Algorithm} differs): it was damaged while uploading.",
                upload);
        }
    }

    private static DriveFileContent WithContent(DriveFileContent content, Stream stream) => new(stream, content.Name, content.MimeType)
    {
        Length = content.Length,
        CreatedAt = content.CreatedAt,
        ModifiedAt = content.ModifiedAt,
    };

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
