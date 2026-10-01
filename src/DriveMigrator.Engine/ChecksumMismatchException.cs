using DriveMigrator.Core;
using DriveMigrator.Core.Drive;

namespace DriveMigrator.Engine;

/// <summary>
/// A file's content doesn't match: the copy is damaged, or the destination already has a different file with that
/// name. Recorded as a failure of <see cref="Kind"/> with <see cref="Destination"/> as the item's target, so the
/// user can choose to overwrite it.
/// </summary>
internal sealed class ChecksumMismatchException(FailureKind kind, MigrationNode destination, string message, HashComparison comparison)
    : Exception(message)
{
    public FailureKind Kind { get; } = kind;

    /// <summary>The destination file that doesn't match.</summary>
    public MigrationNode Destination { get; } = destination;

    /// <summary>The full values, for the error details.</summary>
    public string Details { get; } = $"{message}{Environment.NewLine}{comparison.Algorithm} expected {comparison.Expected}{Environment.NewLine}{comparison.Algorithm} found    {comparison.Actual}";
}
