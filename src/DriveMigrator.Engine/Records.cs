using DriveMigrator.Core;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.Engine;

public enum JobStatus
{
    Running,

    /// <summary>Stopped by the user or interrupted (e.g. the app closed); can be resumed.</summary>
    Paused,

    Completed,

    CompletedWithErrors,
}

public enum ItemStatus
{
    Pending,
    InProgress,
    Done,
    Skipped,
    Failed,
}

/// <summary>Why a failed item failed. Persisted, so only append.</summary>
public enum FailureKind
{
    /// <summary>An ordinary error (network, permissions, unsupported item...). "Retry failed" tries it again.</summary>
    Error,

    /// <summary>
    /// The copy doesn't match the original's checksum: damaged on the way. The copy stays in the destination
    /// (<see cref="ItemRecord.Target"/>) and is overwritten when copied again.
    /// </summary>
    ChecksumMismatch,

    /// <summary>
    /// A file with the same name was already in the destination (<see cref="ItemRecord.Target"/>) and its content is
    /// different. The user decides what to do with it.
    /// </summary>
    DiffersFromExisting,
}

/// <summary>Identifies an account across app restarts.</summary>
public sealed record AccountRef(string ProviderId, string AccountId);

public sealed record JobRecord(
    long Id,
    DateTimeOffset CreatedAt,
    string Title,
    AccountRef Source,
    AccountRef Destination,
    TransferOptions Options,
    IReadOnlyList<TransferTarget> Targets,
    JobStatus Status);

/// <summary>
/// One unit of work. <see cref="Source"/> null means "every root item of the capability". Items are only created
/// once their destination parent exists, so <see cref="TargetParent"/> is always known (null = destination root).
/// <see cref="Error"/> is the message shown to the user; <see cref="Details"/> holds the full exception (type and
/// stack trace) for bug reports. <see cref="Failure"/> says what kind of failure it is. <see cref="Resolution"/>, set
/// when the user chose how to copy a mismatched file again, overrides the job's conflict policy for this item;
/// with <see cref="ConflictPolicy.Overwrite"/>, <see cref="Replace"/> is the destination file to overwrite.
/// </summary>
public sealed record ItemRecord(
    long Id,
    long JobId,
    long? ParentId,
    CapabilityKind Kind,
    string Name,
    int Depth,
    MigrationNode? Source,
    MigrationNode? TargetParent,
    ItemStatus Status,
    MigrationNode? Target = null,
    string? Error = null,
    long Bytes = 0,
    string? Details = null,
    FailureKind Failure = FailureKind.Error,
    ConflictPolicy? Resolution = null,
    MigrationNode? Replace = null);

/// <summary>A child discovered while processing a container.</summary>
public sealed record NewItem(CapabilityKind Kind, MigrationNode? Source, MigrationNode? TargetParent, string Name);

public sealed record JobCounts(int Pending, int Done, int Skipped, int Failed, long Bytes)
{
    public int Total => Pending + Done + Skipped + Failed;

    public int Finished => Done + Skipped + Failed;
}
