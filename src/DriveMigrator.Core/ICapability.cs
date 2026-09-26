namespace DriveMigrator.Core;

/// <summary>
/// One data category of a signed-in account. Every capability exposes the same browsing
/// contract so the UI can render any of them as a tree; typed read/write operations live on
/// the derived interfaces (<see cref="Drive.IDriveCapability"/>, <see cref="Mail.IMailCapability"/>...).
/// </summary>
public interface ICapability
{
    CapabilityKind Kind { get; }

    /// <summary>Whether containers can be created inside other containers (false for calendars).</summary>
    bool SupportsNestedContainers { get; }

    /// <summary>Lists the direct children of <paramref name="parent"/>, or the root items when it is null.</summary>
    IAsyncEnumerable<MigrationNode> GetChildrenAsync(MigrationNode? parent, CancellationToken cancellationToken = default);

    /// <summary>Creates a container (folder, mail folder, calendar, contact folder) under <paramref name="parent"/>.</summary>
    Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a direct child by name. The default implementation scans <see cref="GetChildrenAsync"/>
    /// case-insensitively; providers with a server-side lookup should override it.
    /// </summary>
    async Task<MigrationNode?> FindChildAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        await foreach (var child in GetChildrenAsync(parent, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }
}
