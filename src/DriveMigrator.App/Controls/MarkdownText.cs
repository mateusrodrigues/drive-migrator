using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;

namespace DriveMigrator.App.Controls;

/// <summary>
/// A selectable block of rendered Markdown text in which link runs open in the browser when clicked. Every inline in
/// it is a <see cref="Run"/>, which is what lets a text position be mapped back to the link it falls in.
/// </summary>
internal sealed class MarkdownText : SelectableTextBlock
{
    private readonly List<(int Start, int End, string Url)> _links = [];

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public IReadOnlyList<(int Start, int End, string Url)> Links => _links;

    /// <summary>Marks <paramref name="run"/>, about to be appended to <see cref="TextBlock.Inlines"/>, as a link.</summary>
    public void AddLink(Run run, string url)
    {
        var start = Inlines!.OfType<Run>().Sum(r => r.Text?.Length ?? 0);
        _links.Add((start, start + (run.Text?.Length ?? 0), url));
    }

    /// <summary>The address of the link at a text position, if any.</summary>
    public string? LinkAt(int position)
    {
        foreach (var (start, end, url) in _links)
        {
            if (position >= start && position < end)
            {
                return url;
            }
        }

        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured is not null && e.Pointer.Captured != this)
        {
            return;
        }

        var url = LinkUnder(e);
        Cursor = url is null ? null : new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, url);
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Dragging across a link selects text; only a plain click follows it.
        if (e.InitialPressMouseButton == MouseButton.Left && SelectionStart == SelectionEnd && LinkUnder(e) is { } url
            && TopLevel.GetTopLevel(this) is { } top)
        {
            await top.Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private string? LinkUnder(PointerEventArgs e)
    {
        if (_links.Count == 0)
        {
            return null;
        }

        var hit = TextLayout.HitTestPoint(e.GetPosition(this) - new Point(Padding.Left, Padding.Top));
        return hit.IsInside ? LinkAt(hit.TextPosition) : null;
    }
}
