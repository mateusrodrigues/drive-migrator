using DriveMigrator.Core.Drive;

namespace DriveMigrator.Engine;

/// <summary>
/// Hashes content as an upload reads it, so the copy can be verified without reading the file twice. Uploaders may
/// seek back and re-send a chunk after a failed request; bytes already hashed are not hashed again. If a reader
/// skips ahead, the hashes can't be trusted and <see cref="GetHashes"/> returns null. Owns neither the inner stream
/// nor <paramref name="hashes"/>, so an uploader disposing it early loses nothing.
/// </summary>
internal sealed class HashingStream(Stream inner, FileHashAccumulator hashes) : Stream
{
    private readonly FileHashAccumulator _hashes = hashes;
    private long _position;
    private bool _gap;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => CanSeek ? inner.Position : _position;
        set => inner.Position = value;
    }

    /// <summary>The hashes of the whole content, or null if it wasn't read exactly once from start to <paramref name="expectedLength"/>.</summary>
    public FileHashes? GetHashes(long expectedLength)
        => _gap || _hashes.Length != expectedLength ? null : _hashes.Complete();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var start = Position;
        var read = inner.Read(buffer);
        Observe(buffer[..read], start);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var start = Position;
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span[..read], start);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Observe(ReadOnlySpan<byte> data, long start)
    {
        _position = start + data.Length;
        var hashed = _hashes.Length;
        if (start > hashed)
        {
            _gap = true;
        }
        else if (!_gap && start + data.Length > hashed)
        {
            _hashes.Append(data[(int)(hashed - start)..]);
        }
    }
}
