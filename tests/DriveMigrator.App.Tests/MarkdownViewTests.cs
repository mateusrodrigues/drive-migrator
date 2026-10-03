using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

using DriveMigrator.App.Controls;
using DriveMigrator.App.Services;
using DriveMigrator.App.Views;

namespace DriveMigrator.App.Tests;

public sealed class MarkdownViewTests
{
    private const string Sample = """
        # Title here

        Intro with **bold**, *italic*, `code` and a [link](https://example.com/page).

        ## Steps

        1. First
           - nested a
           - nested b
        2. Second

        > Careful: this is a note.

        | Name | Use |
        |---|---|
        | `A` | first |
        | `B` | second |

        ```
        line one
        line two
        ```
        """;

    [AvaloniaFact]
    public void Markdown_RendersEachBlockKind()
    {
        var view = new MarkdownView { Markdown = Sample };
        var window = new Window { Content = view, Width = 600, Height = 800 };
        window.Show();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Concat(t.Inlines!.OfType<Run>().Select(r => r.Text))).ToList();

        Assert.Contains("Title here", texts);
        Assert.Contains("Steps", texts);
        Assert.Contains("First", texts);
        Assert.Contains("nested a", texts);
        Assert.Contains("1.", texts);
        Assert.Contains("2.", texts);
        Assert.Contains("Careful: this is a note.", texts);
        Assert.Contains("line one\nline two", texts);
        Assert.Contains(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("banner"));
        Assert.Equal(6, view.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("cell")));
        Assert.Contains(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("cell") && b.Classes.Contains("head"));
        window.Close();
    }

    [AvaloniaFact]
    public void Links_AreMarkedInTheText()
    {
        var view = new MarkdownView { Markdown = Sample };
        var window = new Window { Content = view };
        window.Show();

        var paragraph = view.GetVisualDescendants().OfType<MarkdownText>().Single(t => t.Links.Count > 0);
        var (start, end, url) = Assert.Single(paragraph.Links);
        Assert.Equal("https://example.com/page", url);
        Assert.Equal("link", RunText(paragraph)[start..end]);
        Assert.Equal(url, paragraph.LinkAt(start));
        Assert.Equal(url, paragraph.LinkAt(end - 1));
        Assert.Null(paragraph.LinkAt(start - 1));
        Assert.Null(paragraph.LinkAt(end));
        window.Close();
    }

    [AvaloniaFact]
    public void LinksToOtherSchemes_AreNotFollowable()
    {
        var view = new MarkdownView { Markdown = "[run](file:///etc/passwd) and <https://example.com>" };
        var window = new Window { Content = view };
        window.Show();

        var text = Assert.Single(view.GetVisualDescendants().OfType<MarkdownText>());
        Assert.Equal("run and https://example.com", RunText(text));
        Assert.Equal("https://example.com", Assert.Single(text.Links).Url);
        Assert.True(MarkdownView.IsFollowable("https://example.com"));
        Assert.False(MarkdownView.IsFollowable("javascript:alert(1)"));
        Assert.False(MarkdownView.IsFollowable("docs/setup-google.md"));
        window.Close();
    }

    [Fact]
    public void FirstHeading_IsTheLevelOneTitle()
    {
        Assert.Equal("Setting up x things", MarkdownView.FirstHeading("intro\n\n## Not me\n\n# Setting up `x` things\n"));
        Assert.Null(MarkdownView.FirstHeading("no heading"));
    }

    [AvaloniaTheory]
    [InlineData("google", "Setting up Google credentials")]
    [InlineData("microsoft", "Setting up Microsoft credentials")]
    public void PackagedGuides_LoadFromTheDocs(string providerId, string title)
    {
        Assert.True(SetupGuides.Exists(providerId));

        var guide = SetupGuides.Load(providerId)!;

        Assert.Equal(title, guide.Title);
        Assert.Equal(File.ReadAllText(Path.Combine(RepoRoot(), "docs", $"setup-{providerId}.md")).ReplaceLineEndings(), guide.Markdown.ReplaceLineEndings());
    }

    [AvaloniaFact]
    public void UnknownGuide_IsMissing()
    {
        Assert.False(SetupGuides.Exists("nope"));
        Assert.Null(SetupGuides.Load("nope"));
    }

    [AvaloniaFact]
    public void GuideWindow_ShowsTheGuideUnderItsTitle()
    {
        var window = new GuideWindow(SetupGuides.Load("microsoft")!);
        window.Show();

        Assert.Equal("Setting up Microsoft credentials", window.Title);
        Assert.NotEmpty(window.GetVisualDescendants().OfType<MarkdownView>().Single().GetVisualDescendants().OfType<TextBlock>());
        window.Close();
    }

    private static string RunText(TextBlock text) => string.Concat(text.Inlines!.OfType<Run>().Select(r => r.Text));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DriveMigrator.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
