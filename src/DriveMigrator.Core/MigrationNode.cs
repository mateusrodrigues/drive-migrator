namespace DriveMigrator.Core;

/// <summary>
/// A provider-neutral handle to one browsable item (folder, file, message, event, contact...).
/// <see cref="Id"/> is opaque and only meaningful to the capability that produced the node.
/// Equality is identity (id and kind), not metadata.
/// </summary>
public sealed record MigrationNode(string Id, string Name, NodeKind Kind)
{
    public bool IsContainer => Kind.IsContainer();

    /// <summary>Size in bytes, when the provider knows it.</summary>
    public long? Size { get; init; }

    public DateTimeOffset? ModifiedAt { get; init; }

    public string? MimeType { get; init; }

    /// <summary>
    /// Formats this item can be exported to. Non-empty for provider-native documents
    /// (e.g. Google Docs) that have no binary representation and must be exported to be copied.
    /// </summary>
    public IReadOnlyList<ExportFormat> ExportFormats { get; init; } = [];

    public bool RequiresExport => ExportFormats.Count > 0;

    /// <summary>
    /// Nodes are equal when they identify the same item (same <see cref="Id"/> and <see cref="Kind"/>), even if
    /// metadata such as name or size differs between two reads.
    /// </summary>
    public bool Equals(MigrationNode? other)
        => other is not null && Id == other.Id && Kind == other.Kind;

    public override int GetHashCode() => HashCode.Combine(Id, Kind);
}

/// <summary>A format a provider-native document can be exported to.</summary>
public sealed record ExportFormat(string MimeType, string FileExtension, string DisplayName);
