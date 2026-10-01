using System.Runtime.CompilerServices;
using DriveMigrator.Core;

namespace DriveMigrator.Testing;

/// <summary>
/// Shared in-memory tree for the fake capabilities. Each node may carry a payload
/// (file bytes, MIME, event, contact) that the derived capability interprets.
/// </summary>
public abstract class InMemoryCapability(CapabilityKind kind, NodeKind containerKind, bool supportsNestedContainers) : ICapability
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private long _sequence;

    public CapabilityKind Kind { get; } = kind;

    public string DisplayName { get; set; } = kind.ToString();

    /// <summary>Called before listing children; return an exception to simulate a failing request.</summary>
    public Func<MigrationNode?, Exception?>? OnGetChildren { get; set; }

    public bool SupportsNestedContainers { get; } = supportsNestedContainers;

    protected NodeKind ContainerKind { get; } = containerKind;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (OnGetChildren?.Invoke(parent) is { } failure)
        {
            throw failure;
        }

        MigrationNode[] children;
        lock (_gate)
        {
            if (parent is not null)
            {
                EnsureContainer(parent);
            }

            children = [.. _entries.Values
                .Where(e => e.ParentId == parent?.Id)
                .OrderBy(e => e.Sequence)
                .Select(e => e.Node)];
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return child;
        }
    }

    public Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AddContainer(parent, name));
    }

    /// <summary>Synchronous seeding helper for tests.</summary>
    public MigrationNode AddContainer(MigrationNode? parent, string name)
    {
        if (parent is not null && !SupportsNestedContainers)
        {
            throw new NotSupportedException($"{Kind} containers cannot be nested.");
        }

        return Add(parent, new MigrationNode(NewId(), name, ContainerKind), payload: null);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    protected static string NewId() => Guid.NewGuid().ToString("N");

    protected MigrationNode Add(MigrationNode? parent, MigrationNode node, object? payload)
    {
        lock (_gate)
        {
            if (parent is not null)
            {
                EnsureContainer(parent);
            }

            _entries.Add(node.Id, new Entry(node, parent?.Id, payload, ++_sequence));
            return node;
        }
    }

    /// <summary>Replaces an existing node's metadata and payload, keeping its id and position.</summary>
    protected MigrationNode Replace(MigrationNode existing, MigrationNode updated, object? payload)
    {
        lock (_gate)
        {
            var entry = GetEntry(existing);
            _entries[existing.Id] = entry with { Node = updated with { Id = existing.Id }, Payload = payload };
            return _entries[existing.Id].Node;
        }
    }

    /// <summary>Deletes a node (not its children), e.g. to simulate the user removing a file between runs.</summary>
    public void Remove(MigrationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            GetEntry(node);
            _entries.Remove(node.Id);
        }
    }

    protected TPayload GetPayload<TPayload>(MigrationNode node)
    {
        lock (_gate)
        {
            return GetEntry(node).Payload is TPayload payload
                ? payload
                : throw new ArgumentException($"Node '{node.Name}' is not a {typeof(TPayload).Name}.", nameof(node));
        }
    }

    /// <summary>The container a node lives in, or null for root items.</summary>
    public MigrationNode? GetParent(MigrationNode node)
    {
        lock (_gate)
        {
            var parentId = GetEntry(node).ParentId;
            return parentId is null ? null : _entries[parentId].Node;
        }
    }

    private Entry GetEntry(MigrationNode node)
        => _entries.TryGetValue(node.Id, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Node '{node.Name}' ({node.Id}) does not exist in this {Kind} store.");

    private void EnsureContainer(MigrationNode parent)
    {
        if (!GetEntry(parent).Node.IsContainer)
        {
            throw new ArgumentException($"Node '{parent.Name}' is not a container.", nameof(parent));
        }
    }

    private sealed record Entry(MigrationNode Node, string? ParentId, object? Payload, long Sequence);
}
