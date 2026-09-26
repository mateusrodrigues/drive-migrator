using Avalonia.Headless.XUnit;
using DriveMigrator.App.ViewModels;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Testing;

namespace DriveMigrator.App.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly FakeCloudProvider _google = new("google", "Google", CapabilityKind.Drive);
    private readonly FakeCloudProvider _contactsOnly = new("contacts", "Contacts Cloud", CapabilityKind.Contacts);
    private readonly InMemoryAccountStore _store = new();
    private readonly AccountManager _accounts;
    private readonly FakeDialogService _dialogs = new();
    private readonly MainWindowViewModel _vm;
    private readonly FakeAccountSession _source;
    private readonly FakeAccountSession _target;

    public MainWindowViewModelTests()
    {
        var registry = new ProviderRegistry([_google, _contactsOnly]);
        _accounts = new AccountManager(registry, _store);
        _vm = new MainWindowViewModel(_accounts, registry, _dialogs);

        _source = _google.AddAccount("me@gmail.com");
        var photos = _source.Drive.AddContainer(null, "Photos");
        _source.Drive.AddFile(photos, "cat.jpg", [1]);
        _source.Drive.AddFile(null, "notes.txt", [1]);

        _target = _google.AddAccount("work@example.com");
        var archive = _target.Drive.AddContainer(null, "Archive");
        _target.Drive.AddFile(archive, "old.txt", [1]);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _accounts.Dispose();
    }

    [AvaloniaFact]
    public async Task Initialize_PutsFirstTwoAccountsSideBySide()
    {
        await LoadAsync(_source, _target);

        Assert.Equal(_source.Account, _vm.Left.SelectedAccount!.Info);
        Assert.Equal(_target.Account, _vm.Right.SelectedAccount!.Info);
        Assert.Equal("Drive", Assert.Single(_vm.Left.Roots).Name);
    }

    [AvaloniaFact]
    public async Task NothingChecked_ExplainsWhatToDo()
    {
        await LoadAsync(_source, _target);

        Assert.Null(MainWindowViewModel.BuildRequest(_vm.Left, _vm.Right, out var error));
        Assert.Contains("Check the items", error, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CheckedItems_GoIntoSelectedDestinationFolder()
    {
        await LoadAsync(_source, _target);
        var sourceRoot = await ExpandAsync(_vm.Left.Roots[0]);
        sourceRoot.Children.Single(c => c.Name == "Photos").IsChecked = true;
        var targetRoot = await ExpandAsync(_vm.Right.Roots[0]);
        var archive = await ExpandAsync(targetRoot.Children[0]);

        // Selecting a file in the destination means "its folder".
        _vm.Right.SelectedNode = archive.Children[0];
        var request = MainWindowViewModel.BuildRequest(_vm.Left, _vm.Right, out var error);

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("Photos", Assert.Single(request.Items).Node!.Name);
        Assert.Equal(archive.Node, request.GetTarget(CapabilityKind.Drive)!.Container);
        Assert.Equal("Copies land in: Drive / Archive", _vm.Right.DestinationSummary);
        Assert.Equal(
            "From me@gmail.com · Google to work@example.com · Google\n\n• Drive: 1 folder → Drive / Archive",
            MainWindowViewModel.Describe(_vm.Left, _vm.Right, request).ReplaceLineEndings("\n"));
    }

    [AvaloniaFact]
    public async Task NoDestinationSelected_TargetsRoot()
    {
        await LoadAsync(_source, _target);
        _vm.Left.Roots[0].IsChecked = true;

        var request = MainWindowViewModel.BuildRequest(_vm.Left, _vm.Right, out _);

        Assert.Null(Assert.Single(request!.Items).Node);
        Assert.Null(request.GetTarget(CapabilityKind.Drive)!.Container);
        Assert.Contains("• Drive: everything → Drive", MainWindowViewModel.Describe(_vm.Left, _vm.Right, request), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task SameAccount_CannotCopyFolderIntoItself()
    {
        await LoadAsync(_source);
        _vm.Right.SelectedAccount = _vm.Right.Accounts[0];
        var left = await ExpandAsync(_vm.Left.Roots[0]);
        left.Children.Single(c => c.Name == "Photos").IsChecked = true;
        var right = await ExpandAsync(_vm.Right.Roots[0]);
        var photos = await ExpandAsync(right.Children.Single(c => c.Name == "Photos"));
        _vm.Right.SelectedNode = photos;

        // Same account in both panes: each pane has its own tree, yet the destination is inside the selection.
        Assert.Null(MainWindowViewModel.BuildRequest(_vm.Left, _vm.Right, out var error));
        Assert.Contains("into themselves", error, StringComparison.Ordinal);

        // A sibling folder of the same account is fine.
        _vm.Right.SelectedNode = right;
        Assert.NotNull(MainWindowViewModel.BuildRequest(_vm.Left, _vm.Right, out error));
        Assert.Null(error);
    }

    [AvaloniaFact]
    public async Task DestinationWithoutMatchingCapability_IsRejected()
    {
        var contacts = _contactsOnly.AddAccount("c@example.com");
        await LoadAsync(_source, contacts);
        _vm.Left.Roots[0].IsChecked = true;

        Assert.Null(MainWindowViewModel.BuildRequest(_vm.Left, _vm.Right, out var error));
        Assert.Contains("has no drive", error, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CopyCommand_OnlyPreviews()
    {
        await LoadAsync(_source, _target);
        _vm.Left.Roots[0].IsChecked = true;
        var targetCountBefore = _target.Drive.Count;

        await _vm.CopyLeftToRightCommand.ExecuteAsync(null);

        var (title, message) = Assert.Single(_dialogs.Messages);
        Assert.Equal("Transfer preview", title);
        Assert.Contains("Nothing has been copied", message, StringComparison.Ordinal);
        Assert.Equal(targetCountBefore, _target.Drive.Count);
    }

    [AvaloniaFact]
    public async Task AccountRefresh_KeepsTreeUnlessSessionChanged()
    {
        await LoadAsync(_source, _target);
        var tree = _vm.Left.Roots[0];

        await _accounts.AddAccountAsync("google", TestContext.Current.CancellationToken);
        Assert.Same(tree, _vm.Left.Roots[0]);
        Assert.Equal(3, _vm.Left.Accounts.Count);

        await _accounts.RemoveAsync(_source.Account, TestContext.Current.CancellationToken);
        Assert.Null(_vm.Left.SelectedAccount);
        Assert.Empty(_vm.Left.Roots);
    }

    private async Task LoadAsync(params FakeAccountSession[] sessions)
    {
        _store.Accounts = [.. sessions.Select(s => s.Account)];
        await _vm.InitializeAsync();
    }

    private static async Task<NodeViewModel> ExpandAsync(NodeViewModel node)
    {
        await node.LoadChildrenAsync();
        return node;
    }
}
