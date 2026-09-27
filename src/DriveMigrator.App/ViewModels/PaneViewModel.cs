using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.App.ViewModels;

/// <summary>
/// One side of the main window: an account picker and that account's tree. The same pane acts as the source
/// (checked items) or the destination (the highlighted node) depending on which arrow is clicked.
/// </summary>
public sealed partial class PaneViewModel(string name) : ViewModelBase, IDisposable
{
    private CancellationTokenSource _treeLifetime = new();

    /// <summary>"left" or "right", used in messages.</summary>
    public string Name { get; } = name;

    public ObservableCollection<AccountOptionViewModel> Accounts { get; } = [];

    public ObservableCollection<NodeViewModel> Roots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccount), nameof(Session))]
    public partial AccountOptionViewModel? SelectedAccount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationSummary))]
    public partial NodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    public partial string SelectionSummary { get; private set; } = "Nothing selected";

    public bool HasAccount => SelectedAccount is not null;

    public bool HasAnyAccounts => Accounts.Count > 0;

    /// <summary>The account picker's text while nothing is chosen.</summary>
    public string AccountPlaceholder => HasAnyAccounts ? "Choose an account" : "No accounts connected";

    public IAccountSession? Session => SelectedAccount?.Account.Session;

    /// <summary>Where items copied into this pane will go, as shown under the tree.</summary>
    public string DestinationSummary => DestinationNode(SelectedNode) is { } node
        ? $"Copies land in: {node.Path}"
        : "Copies land in the top level. Select a folder to choose another destination.";

    /// <summary>
    /// Updates the account list in place so the selected option (and its tree) survives unless that account is gone
    /// or has a new session (e.g. after re-authorizing), in which case the same account is re-selected.
    /// </summary>
    public void SetAccounts(IReadOnlyList<AccountOptionViewModel> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var previous = SelectedAccount;

        for (var i = Accounts.Count - 1; i >= 0; i--)
        {
            if (!accounts.Any(a => a.IsSameAs(Accounts[i])))
            {
                Accounts.RemoveAt(i);
            }
        }

        for (var i = 0; i < accounts.Count; i++)
        {
            if (!Accounts.Any(a => a.IsSameAs(accounts[i])))
            {
                Accounts.Insert(Math.Min(i, Accounts.Count), accounts[i]);
            }
        }

        OnPropertyChanged(nameof(HasAnyAccounts));
        OnPropertyChanged(nameof(AccountPlaceholder));
        if (previous is not null && !Accounts.Contains(previous))
        {
            SelectedAccount = Accounts.FirstOrDefault(a => a.Info == previous.Info);
        }
    }

    /// <summary>The checked items, each standing for its whole subtree.</summary>
    public IReadOnlyList<NodeViewModel> GetCheckedNodes() => [.. Roots.SelectMany(r => r.GetCheckedRoots())];

    public static IReadOnlyList<TransferItem> ToTransferItems(IEnumerable<NodeViewModel> nodes)
        => [.. nodes.Select(n => new TransferItem(n.Capability.Kind, n.Node))];

    /// <summary>
    /// The container that items of <paramref name="kind"/> should be copied into: the highlighted node if it belongs
    /// to that capability (its parent if it is not a container), otherwise the capability's root.
    /// </summary>
    public NodeViewModel? ResolveDestination(CapabilityKind kind)
    {
        var node = DestinationNode(SelectedNode);
        return node is not null && node.Capability.Kind == kind ? node : null;
    }

    public void Dispose()
    {
        _treeLifetime.Cancel();
        _treeLifetime.Dispose();
    }

    private static NodeViewModel? DestinationNode(NodeViewModel? selected)
    {
        var node = selected;
        while (node is not null && !node.IsContainer)
        {
            node = node.Parent;
        }

        return node;
    }

    partial void OnSelectedAccountChanged(AccountOptionViewModel? value) => RebuildTree();

    /// <summary>Reloads the tree from the service, e.g. to see what a transfer copied. Checks are cleared.</summary>
    [RelayCommand]
    private void Refresh() => RebuildTree();

    private void RebuildTree()
    {
        var value = SelectedAccount;
        _treeLifetime.Cancel();
        _treeLifetime.Dispose();
        _treeLifetime = new CancellationTokenSource();

        SelectedNode = null;
        Roots.Clear();
        foreach (var capability in value?.Account.Session?.Capabilities ?? [])
        {
            Roots.Add(NodeViewModel.CreateRoot(capability, UpdateSelectionSummary, _treeLifetime.Token));
        }

        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        var selected = GetCheckedNodes();
        SelectionSummary = selected.Count switch
        {
            0 => "Nothing selected",
            _ when selected.Any(n => n.IsCapabilityRoot) => "Selected: " + string.Join(", ", selected.Select(Describe)),
            1 => $"Selected: {selected[0].Name}",
            _ => $"Selected: {selected.Count} items",
        };

        static string Describe(NodeViewModel node) => node.IsCapabilityRoot ? $"all of {node.Name}" : node.Name;
    }
}

public sealed class AccountOptionViewModel(ConnectedAccount account, string providerName)
{
    public ConnectedAccount Account { get; } = account;

    public AccountInfo Info => Account.Info;

    public string Label => $"{Account.Info.DisplayName} · {providerName}";

    public string? Email => Account.Info.Email;

    internal bool IsSameAs(AccountOptionViewModel other) => Info == other.Info && Account.Session == other.Account.Session;
}
