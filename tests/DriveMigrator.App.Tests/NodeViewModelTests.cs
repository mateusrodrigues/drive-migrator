using Avalonia.Headless.XUnit;
using DriveMigrator.App.ViewModels;
using DriveMigrator.Core;
using DriveMigrator.Testing;

namespace DriveMigrator.App.Tests;

public class NodeViewModelTests
{
    private readonly FakeDriveCapability _drive = new FakeCloudProvider().AddAccount().Drive;
    private int _selectionChanges;

    [AvaloniaFact]
    public async Task Expanding_LoadsChildrenFoldersFirstThenByName()
    {
        _drive.AddFile(null, "b.txt", [1]);
        _drive.AddContainer(null, "Zeta");
        _drive.AddFile(null, "A.txt", [1, 2]);
        _drive.AddContainer(null, "alpha");
        var root = CreateRoot();

        Assert.True(Assert.Single(root.Children).IsPlaceholder);
        await root.LoadChildrenAsync();

        Assert.Equal(["alpha", "Zeta", "A.txt", "b.txt"], root.Children.Select(c => c.Name));
        Assert.Equal("2 B", root.Children[2].Detail);
        Assert.True(root.Children[0].IsContainer);
        Assert.Equal("Drive / alpha", root.Children[0].Path);
    }

    [AvaloniaFact]
    public async Task CheckingFolder_ChecksLoadedAndLaterLoadedChildren()
    {
        var docs = _drive.AddContainer(null, "Docs");
        _drive.AddFile(docs, "a.txt", [1]);
        _drive.AddFile(null, "top.txt", [1]);
        var root = CreateRoot();
        await root.LoadChildrenAsync();
        var docsNode = root.Children[0];

        docsNode.IsChecked = true;
        await docsNode.LoadChildrenAsync();

        Assert.True(Assert.Single(docsNode.Children).IsChecked);
        Assert.Null(root.IsChecked);
        Assert.Equal([docsNode], root.GetCheckedRoots());
        Assert.True(_selectionChanges > 0);
    }

    [AvaloniaFact]
    public async Task ParentStateFollowsChildren()
    {
        _drive.AddFile(null, "a.txt", [1]);
        _drive.AddFile(null, "b.txt", [1]);
        var root = CreateRoot();
        await root.LoadChildrenAsync();

        root.Children[0].IsChecked = true;
        Assert.Null(root.IsChecked);
        Assert.Equal([root.Children[0]], root.GetCheckedRoots());

        root.Children[1].IsChecked = true;
        Assert.True(root.IsChecked);
        Assert.Equal([root], root.GetCheckedRoots());

        root.Children[0].IsChecked = false;
        root.Children[1].IsChecked = false;
        Assert.False(root.IsChecked);
        Assert.Empty(root.GetCheckedRoots());
    }

    [AvaloniaFact]
    public async Task UncheckingParent_UnchecksEverything_AndNullMeansChecked()
    {
        var docs = _drive.AddContainer(null, "Docs");
        _drive.AddFile(docs, "a.txt", [1]);
        var root = CreateRoot();
        await root.LoadChildrenAsync();
        await root.Children[0].LoadChildrenAsync();
        var file = root.Children[0].Children[0];

        file.IsChecked = true;
        Assert.True(root.IsChecked);

        root.IsChecked = false;
        Assert.False(file.IsChecked);

        root.IsChecked = null;
        Assert.True(file.IsChecked);
    }

    [AvaloniaFact]
    public async Task LoadFailure_ShowsErrorAndCanRetry()
    {
        _drive.AddFile(null, "a.txt", [1]);
        _drive.OnGetChildren = _ => new HttpRequestException("network down");
        var root = CreateRoot();

        await root.LoadChildrenAsync();
        var error = Assert.Single(root.Children);
        Assert.True(error.IsPlaceholder);
        Assert.Contains("network down", error.Name, StringComparison.Ordinal);
        Assert.Empty(root.GetCheckedRoots());

        _drive.OnGetChildren = null;
        await root.LoadChildrenAsync();
        Assert.Equal("a.txt", Assert.Single(root.Children).Name);
    }

    [AvaloniaFact]
    public async Task NativeDocuments_AreMarked()
    {
        var format = new ExportFormat("application/pdf", ".pdf", "PDF");
        var doc = _drive.AddNativeDocument(null, "Report", "application/vnd.google-apps.document", (format, [1]));
        var root = CreateRoot();

        await root.LoadChildrenAsync();

        Assert.Equal("native document", Assert.Single(root.Children).Detail);
        Assert.Equal(doc, root.Children[0].Node);
    }

    private NodeViewModel CreateRoot() => NodeViewModel.CreateRoot(_drive, () => _selectionChanges++, CancellationToken.None);
}
