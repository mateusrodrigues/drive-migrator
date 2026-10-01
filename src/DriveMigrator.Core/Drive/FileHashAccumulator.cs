using System.Security.Cryptography;

namespace DriveMigrator.Core.Drive;

/// <summary>
/// Computes every algorithm in <see cref="FileHashes"/> over content fed in pieces, so a copy can be compared with
/// whatever the source and destination report without reading the file twice.
/// </summary>
public sealed class FileHashAccumulator : IDisposable
{
#pragma warning disable CA5350, CA5351 // MD5 and SHA-1 only check integrity against what the services report; nothing relies on them for security.
    private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350, CA5351
    private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly QuickXorHash _quickXor = new();

    public long Length { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        _md5.AppendData(data);
        _sha1.AppendData(data);
        _sha256.AppendData(data);
        _quickXor.Append(data);
        Length += data.Length;
    }

    /// <summary>The hashes of everything appended. Call once.</summary>
    public FileHashes Complete() => new(
        Convert.ToHexStringLower(_md5.GetHashAndReset()),
        Convert.ToHexStringLower(_sha1.GetHashAndReset()),
        Convert.ToHexStringLower(_sha256.GetHashAndReset()),
        Convert.ToBase64String(_quickXor.GetHash()));

    public static FileHashes Compute(ReadOnlySpan<byte> data)
    {
        using var accumulator = new FileHashAccumulator();
        accumulator.Append(data);
        return accumulator.Complete();
    }

    public void Dispose()
    {
        _md5.Dispose();
        _sha1.Dispose();
        _sha256.Dispose();
    }
}
