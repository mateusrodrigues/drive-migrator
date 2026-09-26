namespace DriveMigrator.Core;

/// <summary>
/// One data category of a signed-in account. Every capability exposes the same browsing
/// contract so the UI can render any of them as a tree; typed read/write operations live on
/// the derived interfaces (<see cref="Drive.IDriveCapability"/>, <see cref="Mail.IMailCapability"/>...).
/// </summary>
public interface ICapability
{
    CapabilityKind Kind { get; }

    /// <summary>Service-specific name shown as the top-level tree node, e.g. "OneDrive" or "Google Drive".</summary>
    string DisplayName { get; }

    /// <summary>Whether containers can be created inside other containers (false for calendars).</summary>
    bool SupportsNestedContainers { get; }

    /// <summary>Lists the direct children of <paramref name="parent"/>, or the root items when it is null.</summary>
    IAsyncEnumerable<MigrationNode> GetChildrenAsync(MigrationNode? parent, CancellationToken cancellationToken = default);

    /// <summary>Creates a container (folder, mail folder, calendar, contact folder) under <paramref name="parent"/>.</summary>
    Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists children for display in the tree: at most <paramref name="maxItems"/> of them, possibly with richer
    /// names and details than <see cref="GetChildrenAsync"/> (which the transfer engine uses and which must list
    /// everything as cheaply as possible). Containers come first.
    /// </summary>
    async IAsyncEnumerable<MigrationNode> BrowseChildrenAsync(
        MigrationNode? parent,
        int maxItems,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var count = 0;
        await foreach (var child in GetChildrenAsync(parent, cancellationToken).ConfigureAwait(false))
        {
            if (count++ == maxItems)
            {
                yield break;
            }

            yield return child;
        }
    }

    /// <summary>The account's well-known container for <paramref name="role"/>, or null if it has none.</summary>
    Task<MigrationNode?> GetSpecialContainerAsync(ContainerRole role, CancellationToken cancellationToken = default)
        => Task.FromResult<MigrationNode?>(null);

    /// <summary>
    /// Adjusts a name to what this service accepts (e.g. OneDrive forbids characters like ':' and '?' that Google
    /// allows). The engine uses the adjusted name both to detect existing items and to create new ones.
    /// </summary>
    string ToValidName(string name) => name;

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
