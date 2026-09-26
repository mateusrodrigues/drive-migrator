using DriveMigrator.Core.Accounts;

namespace DriveMigrator.Core.Transfers;

/// <summary>
/// What the user asked to copy: items picked in the source account, and for each data category the container
/// in the destination account they should land in.
/// </summary>
public sealed record TransferRequest(
    IAccountSession Source,
    IAccountSession Destination,
    IReadOnlyList<TransferItem> Items,
    IReadOnlyList<TransferTarget> Targets)
{
    public TransferTarget? GetTarget(CapabilityKind kind) => Targets.FirstOrDefault(t => t.Kind == kind);
}

/// <summary>
/// One selected item. A container means "this container and everything below it".
/// A null <see cref="Node"/> means the entire capability (every root item).
/// </summary>
public sealed record TransferItem(CapabilityKind Kind, MigrationNode? Node);

/// <summary>Where items of <see cref="Kind"/> go. A null <see cref="Container"/> means the capability's root.</summary>
public sealed record TransferTarget(CapabilityKind Kind, MigrationNode? Container);
