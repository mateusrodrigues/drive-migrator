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
/// stack trace) for bug reports.
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
    string? Details = null);

/// <summary>A child discovered while processing a container.</summary>
public sealed record NewItem(CapabilityKind Kind, MigrationNode? Source, MigrationNode? TargetParent, string Name);

public sealed record JobCounts(int Pending, int Done, int Skipped, int Failed, long Bytes)
{
    public int Total => Pending + Done + Skipped + Failed;

    public int Finished => Done + Skipped + Failed;
}
