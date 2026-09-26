using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;

namespace DriveMigrator.App.ViewModels;

/// <summary>
/// One row of a pane's tree: a capability root ("OneDrive"), a container or an item. Children load lazily on
/// first expansion. The tri-state check box selects whole subtrees: checking a folder selects everything under
/// it, even what has not been loaded yet.
/// </summary>
public sealed partial class NodeViewModel : ViewModelBase
{
    private readonly Action _selectionChanged;
    private readonly CancellationToken _lifetime;
    /// <summary>
    /// Most items loaded into one tree level. Mail folders can hold tens of thousands of messages and showing each
    /// costs a request on some services, so they show the newest page only; checking the folder still copies all.
    /// </summary>
    internal const int MailBrowseLimit = 200;

    internal const int BrowseLimit = 5000;

    private bool? _isChecked = false;
    private bool _loaded;
    private bool _truncated;
    private Task? _loading;

    private NodeViewModel(ICapability capability, MigrationNode? node, NodeViewModel? parent, Action selectionChanged, CancellationToken lifetime)
    {
        Capability = capability;
        Node = node;
        Parent = parent;
        _selectionChanged = selectionChanged;
        _lifetime = lifetime;
        Children = IsContainer ? [Placeholder("Loading…")] : [];
    }

    // Placeholder rows ("Loading…", errors) give containers an expander before their children are known.
    private NodeViewModel(string message)
    {
        Capability = null!;
        IsPlaceholder = true;
        PlaceholderText = message;
        _selectionChanged = static () => { };
        Children = [];
    }

    public ICapability Capability { get; }

    /// <summary>The item, or null for the capability root.</summary>
    public MigrationNode? Node { get; }

    public NodeViewModel? Parent { get; }

    public bool IsPlaceholder { get; }

    private string? PlaceholderText { get; }

    public bool IsCapabilityRoot => !IsPlaceholder && Node is null;

    public bool IsContainer => !IsPlaceholder && (Node is null || Node.IsContainer);

    public string Name => PlaceholderText ?? Node?.Name ?? Capability.DisplayName;

    public Geometry? Icon => IsPlaceholder ? null : Node is null ? NodeIcons.ForCapability(Capability.Kind) : NodeIcons.ForNode(Node);

    /// <summary>Secondary text: file size, or a note that a native document will be exported.</summary>
    public string? Detail => Node switch
    {
        { Detail: { } detail } => detail,
        { RequiresExport: true } => "native document",
        { IsContainer: false, Size: { } size } => FormatSize(size),
        _ => null,
    };

    [ObservableProperty]
    public partial ObservableCollection<NodeViewModel> Children { get; private set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>
    /// True, false, or null when only some children are checked. Setting null (which a two-state check box can do
    /// when clicked while indeterminate) counts as checking.
    /// </summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value ?? true, updateChildren: true, updateParent: true);
    }

    public static NodeViewModel CreateRoot(ICapability capability, Action selectionChanged, CancellationToken lifetime)
        => new(capability, node: null, parent: null, selectionChanged, lifetime);

    /// <summary>
    /// The minimal set of checked nodes: a fully checked node stands for its whole subtree, so its descendants are
    /// not listed separately.
    /// </summary>
    public IEnumerable<NodeViewModel> GetCheckedRoots()
    {
        if (IsPlaceholder || _isChecked == false)
        {
            yield break;
        }

        if (_isChecked == true)
        {
            yield return this;
            yield break;
        }

        foreach (var child in Children)
        {
            foreach (var selected in child.GetCheckedRoots())
            {
                yield return selected;
            }
        }
    }

    /// <summary>
    /// Whether this node is <paramref name="item"/> or lies below it. Compares item ids, so it also works across
    /// the two panes' separate trees of the same account.
    /// </summary>
    public bool IsSelfOrDescendantOf(MigrationNode item)
    {
        ArgumentNullException.ThrowIfNull(item);
        for (var node = this; node is not null; node = node.Parent)
        {
            if (node.Node?.Id == item.Id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>"OneDrive / Documents / Taxes".</summary>
    public string Path => Parent is null ? Name : $"{Parent.Path} / {Name}";

    /// <summary>Loads children once; concurrent callers share the load in progress. Failed loads can be retried.</summary>
    public Task LoadChildrenAsync()
    {
        if (_loaded || !IsContainer)
        {
            return Task.CompletedTask;
        }

        if (_loading is not null)
        {
            return _loading;
        }

        // A load that fails synchronously has already completed (and cleared _loading); don't cache it.
        var load = LoadCoreAsync();
        if (!load.IsCompleted)
        {
            _loading = load;
        }

        return load;
    }

    private async Task LoadCoreAsync()
    {
        IsLoading = true;
        try
        {
            var limit = Capability.Kind == CapabilityKind.Mail ? MailBrowseLimit : BrowseLimit;
            var loaded = new List<NodeViewModel>();
            var items = 0;
            _truncated = false;
            await foreach (var node in Capability.BrowseChildrenAsync(Node, limit + 1, _lifetime))
            {
                if (!node.IsContainer && ++items > limit)
                {
                    _truncated = true;
                    break;
                }

                loaded.Add(new NodeViewModel(Capability, node, this, _selectionChanged, _lifetime));
            }

            // Mail keeps the service's order (Inbox first, newest messages first). Elsewhere: folders first, then
            // names in the user's collation order (this is for display, not identity).
            if (Capability.Kind != CapabilityKind.Mail)
            {
#pragma warning disable CA1309
                loaded.Sort(static (a, b) => a.IsContainer != b.IsContainer
                    ? a.IsContainer ? -1 : 1
                    : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
#pragma warning restore CA1309
            }

            // Children appearing under a checked or unchecked folder take its state.
            foreach (var child in loaded)
            {
                child._isChecked = _isChecked ?? false;
            }

            if (_truncated)
            {
                loaded.Add(Placeholder($"Showing the first {limit} items. Check \"{Name}\" itself to copy everything in it."));
            }

            Children = [.. loaded];
            _loaded = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The pane switched accounts; this tree is gone.
        }
        catch (ReauthenticationRequiredException ex)
        {
            Children = [Placeholder($"{ex.Message} Re-authorize the account in Settings.")];
        }
#pragma warning disable CA1031 // Any provider failure is shown in the tree; collapsing and expanding retries.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Children = [Placeholder($"Couldn't load: {ex.Message}")];
        }
        finally
        {
            IsLoading = false;
            _loading = null;
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = LoadChildrenAsync();
        }
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (IsPlaceholder || _isChecked == value)
        {
            return;
        }

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        if (updateChildren && value is { } state)
        {
            foreach (var child in Children)
            {
                child.SetChecked(state, updateChildren: true, updateParent: false);
            }
        }

        if (updateParent && Parent is not null)
        {
            Parent.UpdateFromChildren();
        }
        else if (updateParent)
        {
            _selectionChanged();
        }
    }

    private void UpdateFromChildren()
    {
        var states = Children.Where(c => !c.IsPlaceholder).Select(c => c._isChecked).Distinct().ToList();
        if (states.Count == 0)
        {
            _selectionChanged();
            return;
        }

        // When only part of the children is shown, checking all of those still isn't the whole container.
        var state = states.Count == 1 && !(_truncated && states[0] == true) ? states[0] : null;
        if (state == _isChecked)
        {
            _selectionChanged();
            return;
        }

        SetChecked(state, updateChildren: false, updateParent: true);
    }

    private static NodeViewModel Placeholder(string message) => new(message);

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
