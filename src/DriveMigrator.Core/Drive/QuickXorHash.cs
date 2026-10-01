using System.Buffers.Binary;

namespace DriveMigrator.Core.Drive;

/// <summary>
/// OneDrive's QuickXorHash: every byte is XORed into a 160-bit register at a position that advances 11 bits per
/// byte (wrapping), then the total length is XORed into the last 64 bits. OneDrive for Business and SharePoint
/// report no other hash, so copies to or from them can only be checked with this one.
/// Follows Microsoft's reference implementation.
/// </summary>
public sealed class QuickXorHash
{
    private const int WidthInBits = 160;
    private const int Shift = 11;
    private const int BitsInLastCell = 32;

    private readonly ulong[] _cells = new ulong[((WidthInBits - 1) / 64) + 1];
    private long _length;
    private int _shift;

    public void Append(ReadOnlySpan<byte> data)
    {
        var cellIndex = _shift / 64;
        var cellOffset = _shift % 64;
        var iterations = Math.Min(data.Length, WidthInBits);

        // Bytes WidthInBits apart land on the same bit position, so each position is handled in one pass.
        for (var i = 0; i < iterations; i++)
        {
            var isLastCell = cellIndex == _cells.Length - 1;
            var bitsInCell = isLastCell ? BitsInLastCell : 64;

            if (cellOffset <= bitsInCell - 8)
            {
                for (var j = i; j < data.Length; j += WidthInBits)
                {
                    _cells[cellIndex] ^= (ulong)data[j] << cellOffset;
                }
            }
            else
            {
                // The byte straddles two cells.
                var next = isLastCell ? 0 : cellIndex + 1;
                byte xored = 0;
                for (var j = i; j < data.Length; j += WidthInBits)
                {
                    xored ^= data[j];
                }

                _cells[cellIndex] ^= (ulong)xored << cellOffset;
                _cells[next] ^= (ulong)xored >> (bitsInCell - cellOffset);
            }

            cellOffset += Shift;
            while (cellOffset >= bitsInCell)
            {
                cellIndex = isLastCell ? 0 : cellIndex + 1;
                cellOffset -= bitsInCell;
            }
        }

        _shift = (_shift + (Shift * (data.Length % WidthInBits))) % WidthInBits;
        _length += data.Length;
    }

    /// <summary>The 20-byte hash of everything appended so far. Does not reset.</summary>
    public byte[] GetHash()
    {
        var hash = new byte[WidthInBits / 8];
        Span<byte> cell = stackalloc byte[8];
        for (var i = 0; i < _cells.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(cell, _cells[i]);
            cell[..Math.Min(8, hash.Length - (i * 8))].CopyTo(hash.AsSpan(i * 8));
        }

        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, _length);
        for (var i = 0; i < length.Length; i++)
        {
            hash[hash.Length - length.Length + i] ^= length[i];
        }

        return hash;
    }

    public static byte[] Compute(ReadOnlySpan<byte> data)
    {
        var hash = new QuickXorHash();
        hash.Append(data);
        return hash.GetHash();
    }
}
