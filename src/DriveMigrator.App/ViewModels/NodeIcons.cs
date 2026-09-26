using Avalonia.Media;
using DriveMigrator.Core;

namespace DriveMigrator.App.ViewModels;

/// <summary>Simple 24×24 outline glyphs for tree nodes.</summary>
internal static class NodeIcons
{
    public static readonly Geometry Folder = StreamGeometry.Parse(
        "M3 6.5C3 5.12 4.12 4 5.5 4h4.1c.4 0 .78.16 1.06.44L12.12 6H18.5C19.88 6 21 7.12 21 8.5v9c0 1.38-1.12 2.5-2.5 2.5h-13C4.12 20 3 18.88 3 17.5z");

    public static readonly Geometry File = StreamGeometry.Parse(
        "M6.5 2H14l6 6v12.5c0 .83-.67 1.5-1.5 1.5h-12C5.67 22 5 21.33 5 20.5v-17C5 2.67 5.67 2 6.5 2zM14 3.5V8h4.5z");

    public static readonly Geometry NativeDocument = StreamGeometry.Parse(
        "M6.5 2H14l6 6v12.5c0 .83-.67 1.5-1.5 1.5h-12C5.67 22 5 21.33 5 20.5v-17C5 2.67 5.67 2 6.5 2zM8 12v1.5h8V12zm0 3v1.5h8V15zm0 3v1.5h5V18z");

    public static readonly Geometry Cloud = StreamGeometry.Parse(
        "M6.5 19a4.5 4.5 0 0 1-.42-8.98A6 6 0 0 1 17.8 8.53 5 5 0 0 1 17.5 19z");

    public static readonly Geometry Mail = StreamGeometry.Parse(
        "M5.5 4h13A2.5 2.5 0 0 1 21 6.5v11a2.5 2.5 0 0 1-2.5 2.5h-13A2.5 2.5 0 0 1 3 17.5v-11A2.5 2.5 0 0 1 5.5 4zM5 7.2V8l7 4.4L19 8v-.8l-7 4.4z");

    public static readonly Geometry Calendar = StreamGeometry.Parse(
        "M7 2h2v2h6V2h2v2h1.5A2.5 2.5 0 0 1 21 6.5v12a2.5 2.5 0 0 1-2.5 2.5h-13A2.5 2.5 0 0 1 3 18.5v-12A2.5 2.5 0 0 1 5.5 4H7zM5 9v9.5c0 .28.22.5.5.5h13a.5.5 0 0 0 .5-.5V9z");

    public static readonly Geometry Person = StreamGeometry.Parse(
        "M12 3a4.5 4.5 0 1 1 0 9 4.5 4.5 0 0 1 0-9zM4 19.5C4 16.46 7.58 14 12 14s8 2.46 8 5.5V21H4z");

    public static Geometry ForCapability(CapabilityKind kind) => kind switch
    {
        CapabilityKind.Mail => Mail,
        CapabilityKind.Calendar => Calendar,
        CapabilityKind.Contacts => Person,
        _ => Cloud,
    };

    public static Geometry ForNode(MigrationNode node) => node.Kind switch
    {
        NodeKind.Folder or NodeKind.MailFolder or NodeKind.ContactFolder => Folder,
        NodeKind.Calendar or NodeKind.CalendarEvent => Calendar,
        NodeKind.MailMessage => Mail,
        NodeKind.Contact => Person,
        _ when node.RequiresExport => NativeDocument,
        _ => File,
    };
}
