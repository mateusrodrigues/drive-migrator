using DriveMigrator.App.Controls;
using DriveMigrator.Core;

namespace DriveMigrator.App.ViewModels;

/// <summary>
/// The neutral glyph for each kind of tree row. A native document gets the ruled-lines glyph because it is the one
/// file that behaves differently: it is converted on copy.
/// </summary>
internal static class NodeIcons
{
    public static IconKind ForCapability(CapabilityKind kind) => kind switch
    {
        CapabilityKind.Mail => IconKind.Envelope,
        CapabilityKind.Calendar => IconKind.Calendar,
        CapabilityKind.Contacts => IconKind.Person,
        _ => IconKind.Cloud,
    };

    public static IconKind ForNode(MigrationNode node) => node.Kind switch
    {
        NodeKind.Folder or NodeKind.MailFolder or NodeKind.ContactFolder or NodeKind.Calendar => IconKind.Folder,
        NodeKind.CalendarEvent => IconKind.Calendar,
        NodeKind.MailMessage => IconKind.Envelope,
        NodeKind.Contact => IconKind.Person,
        _ when node.RequiresExport => IconKind.DocLines,
        _ => IconKind.File,
    };
}
