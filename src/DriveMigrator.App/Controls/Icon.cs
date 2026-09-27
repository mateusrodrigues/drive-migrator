using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DriveMigrator.App.Controls;

/// <summary>The design system's glyphs: what kind of thing a row is, a status, or an action.</summary>
public enum IconKind
{
    None,
    ChevronRight,
    ChevronDown,
    Check,
    Dash,
    Cloud,
    Envelope,
    Calendar,
    Person,
    Folder,
    File,
    DocLines,
    Refresh,
    Eye,
    EyeOff,
    Close,
    Info,
    Warning,
    Danger,
    Success,
    Pause,
    Play,
    Trash,
    External,
    ArrowRight,
    ArrowLeft,
}

/// <summary>
/// A 16×16 outline glyph stroked in the inherited foreground (1.4px, round caps and joins), scaled to the control's
/// size. Only the dots inside the info, warning and danger glyphs are filled. Never a provider's own logo.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<IconKind> KindProperty =
        AvaloniaProperty.Register<Icon, IconKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    private const double GridSize = 16;

    private static readonly Dictionary<IconKind, (Geometry Stroke, Geometry? Fill)> Glyphs = new()
    {
        [IconKind.ChevronRight] = Glyph("M6,3 L11,8 L6,13"),
        [IconKind.ChevronDown] = Glyph("M3,6 L8,11 L13,6"),
        [IconKind.Check] = Glyph("M3.5,8.3 L6.5,11.3 L12.5,4.7"),
        [IconKind.Dash] = Glyph("M4,8 L12,8"),
        [IconKind.Cloud] = Glyph("M4,12 H11 A2.5,2.5 0 0 0 11,7 A3.5,3.5 0 0 0 4.2,6 A2.5,2.5 0 0 0 4,12 Z"),
        [IconKind.Envelope] = Glyph("M3.5,4 H12.5 A1.5,1.5 0 0 1 14,5.5 V10.5 A1.5,1.5 0 0 1 12.5,12 H3.5 A1.5,1.5 0 0 1 2,10.5 V5.5 A1.5,1.5 0 0 1 3.5,4 Z M2.5,4.8 L8,9 L13.5,4.8"),
        [IconKind.Calendar] = Glyph("M4,3.5 H12 A1.5,1.5 0 0 1 13.5,5 V12 A1.5,1.5 0 0 1 12,13.5 H4 A1.5,1.5 0 0 1 2.5,12 V5 A1.5,1.5 0 0 1 4,3.5 Z M2.5,6.6 H13.5 M5.2,2.2 V4.4 M10.8,2.2 V4.4"),
        [IconKind.Person] = Glyph("M10.4,5.4 A2.4,2.4 0 1 1 5.6,5.4 A2.4,2.4 0 1 1 10.4,5.4 Z M3.3,13.3 A4.7,4.7 0 0 1 12.7,13.3"),
        [IconKind.Folder] = Glyph("M2.5,5 H6.5 L7.7,6.6 H13.5 A1,1 0 0 1 14.5,7.6 V13 A1,1 0 0 1 13.5,14 H3.5 A1,1 0 0 1 2.5,13 V6 A1,1 0 0 1 3.5,5 Z"),
        [IconKind.File] = Glyph(FileOutline),
        [IconKind.DocLines] = Glyph(FileOutline + " M5.6,8.2 H10.4 M5.6,10 H10.4 M5.6,11.8 H9"),
        // Differs from the design's near-closed arc, which reads as a plain circle at 16px: a 270° arc with the
        // arrowhead clear of it at the open end.
        [IconKind.Refresh] = Glyph("M13.5,8 A5.5,5.5 0 1 1 8,2.5 C9.5,2.5 10.95,3.1 12,4.15 L13.5,5.5 M13.5,2.5 V5.5 H10.5"),
        [IconKind.Eye] = Glyph(EyeOutline),
        [IconKind.EyeOff] = Glyph(EyeOutline + " M2.5,2.5 L13.5,13.5"),
        [IconKind.Close] = Glyph("M4,4 L12,12 M12,4 L4,12"),
        [IconKind.Info] = Glyph(Circle + " M8,7.2 V11.2", Dot(8, 5.3)),
        [IconKind.Warning] = Glyph("M8,2.6 L14.3,13 A1,1 0 0 1 13.4,14.5 H2.6 A1,1 0 0 1 1.7,13 Z M8,6.3 V9.6", Dot(8, 11.6)),
        [IconKind.Danger] = Glyph(Circle + " M8,5 V9", Dot(8, 11.2)),
        [IconKind.Success] = Glyph(Circle + " M5.2,8.3 L7.2,10.3 L11,6.2"),
        [IconKind.Pause] = Glyph("M5,4 H7.1 V12 H5 Z M8.9,4 H11 V12 H8.9 Z"),
        [IconKind.Play] = Glyph("M5.5,4 L12,8 L5.5,12 Z"),
        [IconKind.Trash] = Glyph("M4,5 H12 M6.3,5 V3.8 A1,1 0 0 1 7.3,2.8 H8.7 A1,1 0 0 1 9.7,3.8 V5 M5.2,5 L5.8,13 A1,1 0 0 0 6.8,13.9 H9.2 A1,1 0 0 0 10.2,13 L10.8,5"),
        [IconKind.External] = Glyph("M11,8.5 V12.5 A1,1 0 0 1 10,13.5 H3.5 A1,1 0 0 1 2.5,12.5 V6 A1,1 0 0 1 3.5,5 H7.5 M9,3 H13 V7 M6.5,9.5 L13,3"),
        [IconKind.ArrowRight] = Glyph("M3,8 H12 M8.5,4 L12,8 L8.5,12"),
        [IconKind.ArrowLeft] = Glyph("M13,8 H4 M7.5,4 L4,8 L7.5,12"),
    };

    private const string FileOutline = "M4.5,2.5 H9 L12,5.5 V13 A1,1 0 0 1 11,14 H4.5 A1,1 0 0 1 3.5,13 V3.5 A1,1 0 0 1 4.5,2.5 Z M9,2.5 V5.5 H12";

    private const string EyeOutline = "M1.5,8 C3.2,4.8 6,3.3 8,3.3 C10,3.3 12.8,4.8 14.5,8 C12.8,11.2 10,12.7 8,12.7 C6,12.7 3.2,11.2 1.5,8 Z M10,8 A2,2 0 1 1 6,8 A2,2 0 1 1 10,8 Z";

    private const string Circle = "M14,8 A6,6 0 1 1 2,8 A6,6 0 1 1 14,8 Z";

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty);
    }

    public IconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Foreground is not { } brush || !Glyphs.TryGetValue(Kind, out var glyph))
        {
            return;
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / GridSize;
        var offset = new Vector((Bounds.Width - (GridSize * scale)) / 2, (Bounds.Height - (GridSize * scale)) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            context.DrawGeometry(null, new Pen(brush, 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), glyph.Stroke);
            if (glyph.Fill is { } fill)
            {
                context.DrawGeometry(brush, null, fill);
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(GridSize, GridSize);

    private static (Geometry, Geometry?) Glyph(string stroke, string? fill = null)
        => (StreamGeometry.Parse(stroke), fill is null ? null : StreamGeometry.Parse(fill));

    private static string Dot(double x, double y)
        => FormattableString.Invariant($"M{x + 0.9},{y} A0.9,0.9 0 1 1 {x - 0.9},{y} A0.9,0.9 0 1 1 {x + 0.9},{y} Z");
}
