using DriveMigrator.Core;

namespace DriveMigrator.Engine;

/// <summary>
/// Caches the names in each destination container (one listing per container instead of one lookup per item) and
/// hands out names atomically, so parallel workers never create two items with the same name.
/// </summary>
internal sealed class TargetIndex(ICapability capability)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<Dictionary<string, Task<MigrationNode?>>>> _listings = [];

    /// <summary>
    /// Returns the existing item called <paramref name="name"/> in <paramref name="parent"/>, waiting if another worker
    /// is creating it right now, or reserves the name for the caller, who must then call
    /// <see cref="NameClaim.Complete"/> or <see cref="NameClaim.Release"/>.
    /// </summary>
    public async Task<NameClaim> ClaimAsync(MigrationNode? parent, string name, CancellationToken cancellationToken)
    {
        var listing = await GetListingAsync(parent, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            Task<MigrationNode?> pending;
            lock (listing)
            {
                if (!listing.TryGetValue(name, out pending!))
                {
                    var reservation = new TaskCompletionSource<MigrationNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    listing[name] = reservation.Task;
                    return new NameClaim(null, name, listing, reservation);
                }
            }

            if (await pending.WaitAsync(cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return new NameClaim(existing, name, listing, null);
            }

            // The other worker failed and released the name; try again.
        }
    }

    /// <summary>Reserves the first free name of the form "name (1).ext", "name (2).ext"...</summary>
    public async Task<NameClaim> ClaimUniqueAsync(MigrationNode? parent, string name, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
        if (stem == name)
        {
            extension = string.Empty;
        }

        for (var i = 1; ; i++)
        {
            var claim = await ClaimAsync(parent, $"{stem} ({i}){extension}", cancellationToken).ConfigureAwait(false);
            if (claim.Existing is null)
            {
                return claim;
            }
        }
    }

    /// <summary>A container this job just created is known to be empty; skip listing it.</summary>
    public void RegisterEmptyContainer(MigrationNode container)
    {
        lock (_gate)
        {
            _listings.TryAdd(container.Id, Task.FromResult(new Dictionary<string, Task<MigrationNode?>>(StringComparer.OrdinalIgnoreCase)));
        }
    }

    /// <summary>Records that <paramref name="replaced"/> now has new metadata after an overwrite.</summary>
    public static void Update(NameClaim claim, MigrationNode replaced)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (claim.Listing)
        {
            claim.Listing[claim.Name] = Task.FromResult<MigrationNode?>(replaced);
        }
    }

    private Task<Dictionary<string, Task<MigrationNode?>>> GetListingAsync(MigrationNode? parent, CancellationToken cancellationToken)
    {
        var key = parent?.Id ?? string.Empty;
        lock (_gate)
        {
            if (!_listings.TryGetValue(key, out var listing) || listing.IsFaulted || listing.IsCanceled)
            {
                listing = LoadListingAsync(parent, cancellationToken);
                _listings[key] = listing;
            }

            return listing;
        }
    }

    private async Task<Dictionary<string, Task<MigrationNode?>>> LoadListingAsync(MigrationNode? parent, CancellationToken cancellationToken)
    {
        // Case-insensitive: OneDrive treats names that way, and it avoids near-duplicates elsewhere.
        var listing = new Dictionary<string, Task<MigrationNode?>>(StringComparer.OrdinalIgnoreCase);
        await foreach (var child in capability.GetChildrenAsync(parent, cancellationToken).ConfigureAwait(false))
        {
            listing.TryAdd(child.Name, Task.FromResult<MigrationNode?>(child));
        }

        return listing;
    }
}

/// <summary>Result of <see cref="TargetIndex.ClaimAsync"/>: an existing item, or a reservation the caller must settle.</summary>
internal sealed class NameClaim
{
    private readonly TaskCompletionSource<MigrationNode?>? _reservation;

    internal NameClaim(MigrationNode? existing, string name, Dictionary<string, Task<MigrationNode?>> listing, TaskCompletionSource<MigrationNode?>? reservation)
    {
        Existing = existing;
        Name = name;
        Listing = listing;
        _reservation = reservation;
    }

    public MigrationNode? Existing { get; }

    public string Name { get; }

    internal Dictionary<string, Task<MigrationNode?>> Listing { get; }

    /// <summary>The reserved name now belongs to <paramref name="created"/>.</summary>
    public void Complete(MigrationNode created)
    {
        lock (Listing)
        {
            Listing[Name] = Task.FromResult<MigrationNode?>(created);
        }

        _reservation?.TrySetResult(created);
    }

    /// <summary>Gives the name back (creation failed) so others can use it.</summary>
    public void Release()
    {
        if (_reservation is null)
        {
            return;
        }

        lock (Listing)
        {
            if (Listing.TryGetValue(Name, out var current) && current == _reservation.Task)
            {
                Listing.Remove(Name);
            }
        }

        _reservation.TrySetResult(null);
    }
}
