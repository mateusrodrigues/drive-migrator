using DriveMigrator.Core.Drive;

namespace DriveMigrator.Core.Tests;

public class FileHashesTests
{
    /// <summary>Bytes (i * 31 + 7) mod 256: the pattern the reference values below were computed over.</summary>
    private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)((i * 31) + 7))];

    // Reference values from rclone's QuickXorHash ("rclone hashsum quickxor --base64", converted to standard base64),
    // around the 160-byte register width and across several of its wraps.
    [Theory]
    [InlineData(0, "AAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData(1, "BwAAAAAAAAAAAAAAAQAAAAAAAAA=")]
    [InlineData(19, "BSu2oYg9CFEEAxz/40APuLAHTeQ=")]
    [InlineData(20, "BSu2oYg9oFEEAxz/5EAPuLAHTeQ=")]
    [InlineData(21, "BSu2oYg9oGEDAxz/5UAPuLAHTeQ=")]
    [InlineData(161, "fz+KGCbnJ+lXCrS07RVjSpWmgvs=")]
    [InlineData(1000, "X4X7cC7/cVgPZMrjju+fOUsa7aY=")]
    [InlineData(70001, "sIFS6kNptaJxg+rSNGznkn4fzvU=")]
    [InlineData(100000, "GD+KGCbnJ+lXCrS07JNiSpWmgvs=")]
    public void QuickXorHash_MatchesReference(int length, string expected)
        => Assert.Equal(expected, Convert.ToBase64String(QuickXorHash.Compute(Pattern(length))));

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(160)]
    [InlineData(161)]
    [InlineData(4096)]
    public void QuickXorHash_DoesNotDependOnHowContentIsSplit(int chunk)
    {
        var data = Pattern(70001);
        var hash = new QuickXorHash();
        for (var offset = 0; offset < data.Length; offset += chunk)
        {
            hash.Append(data.AsSpan(offset, Math.Min(chunk, data.Length - offset)));
        }

        Assert.Equal(QuickXorHash.Compute(data), hash.GetHash());
    }

    [Fact]
    public void Accumulator_ComputesEveryAlgorithm()
    {
        var hashes = FileHashAccumulator.Compute("abc"u8);

        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", hashes.Md5);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", hashes.Sha1);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hashes.Sha256);
        Assert.Equal(Convert.ToBase64String(QuickXorHash.Compute("abc"u8)), hashes.QuickXor);
    }

    [Fact]
    public void Compare_MatchesOnAlgorithmsBothSidesHave()
    {
        var local = FileHashAccumulator.Compute("abc"u8);

        var result = FileHashes.Compare(local, new FileHashes(QuickXor: local.QuickXor));

        Assert.Equal(HashOutcome.Match, result.Outcome);
        Assert.Equal("QuickXorHash", result.Algorithm);
    }

    [Fact]
    public void Compare_HexIsCaseInsensitive()
        => Assert.Equal(HashOutcome.Match, FileHashes.Compare(new FileHashes(Sha1: "abcdef"), new FileHashes(Sha1: "ABCDEF")).Outcome);

    [Fact]
    public void Compare_AnyDifferenceIsAMismatch_ReportingTheStrongest()
    {
        var result = FileHashes.Compare(
            new FileHashes(Md5: "aa", Sha256: "11"),
            new FileHashes(Md5: "bb", Sha256: "22"));

        Assert.Equal(new HashComparison(HashOutcome.Mismatch, "SHA-256", "11", "22"), result);
        Assert.Equal(HashOutcome.Mismatch, FileHashes.Compare(new FileHashes(Md5: "aa", Sha1: "11"), new FileHashes(Md5: "bb", Sha1: "11")).Outcome);
    }

    [Fact]
    public void Compare_WithoutCommonAlgorithm_SaysNothing()
    {
        Assert.Equal(HashOutcome.NoCommonHash, FileHashes.Compare(new FileHashes(Md5: "aa"), new FileHashes(QuickXor: "bb")).Outcome);
        Assert.Equal(HashOutcome.NoCommonHash, FileHashes.Compare(null, new FileHashes(Md5: "aa")).Outcome);
        Assert.Equal(HashOutcome.NoCommonHash, FileHashes.Compare(new FileHashes(Md5: "aa"), null).Outcome);
    }
}
