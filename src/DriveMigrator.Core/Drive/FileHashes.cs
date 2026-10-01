namespace DriveMigrator.Core.Drive;

/// <summary>
/// Content checksums a service reports for a file (or that were computed locally). Hex values are lowercase;
/// <see cref="QuickXor"/> is standard base64, as OneDrive reports it. Not every service offers every algorithm.
/// </summary>
public sealed record FileHashes(string? Md5 = null, string? Sha1 = null, string? Sha256 = null, string? QuickXor = null)
{
    public bool IsEmpty => Md5 is null && Sha1 is null && Sha256 is null && QuickXor is null;

    /// <summary>
    /// Compares every algorithm both sides have, strongest first. Equal on all of them is a match; different on any
    /// is a mismatch; nothing in common (or a missing side) says nothing about the content.
    /// </summary>
    public static HashComparison Compare(FileHashes? expected, FileHashes? actual)
    {
        if (expected is null || actual is null)
        {
            return HashComparison.NoCommonHash;
        }

        string? matched = null;
        foreach (var (algorithm, left, right, comparison) in new (string, string?, string?, StringComparison)[]
        {
            ("SHA-256", expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase),
            ("SHA-1", expected.Sha1, actual.Sha1, StringComparison.OrdinalIgnoreCase),
            ("QuickXorHash", expected.QuickXor, actual.QuickXor, StringComparison.Ordinal),
            ("MD5", expected.Md5, actual.Md5, StringComparison.OrdinalIgnoreCase),
        })
        {
            if (left is null || right is null)
            {
                continue;
            }

            if (!string.Equals(left, right, comparison))
            {
                return new HashComparison(HashOutcome.Mismatch, algorithm, left, right);
            }

            matched ??= algorithm;
        }

        return matched is null ? HashComparison.NoCommonHash : new HashComparison(HashOutcome.Match, matched, null, null);
    }
}

public enum HashOutcome
{
    /// <summary>The two sides share no algorithm, so nothing can be said.</summary>
    NoCommonHash,

    Match,

    Mismatch,
}

/// <summary>Result of <see cref="FileHashes.Compare"/>. For a mismatch, the algorithm and both values that differ.</summary>
public sealed record HashComparison(HashOutcome Outcome, string? Algorithm, string? Expected, string? Actual)
{
    public static HashComparison NoCommonHash { get; } = new(HashOutcome.NoCommonHash, null, null, null);
}
