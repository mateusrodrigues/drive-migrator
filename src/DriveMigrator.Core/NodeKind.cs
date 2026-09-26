namespace DriveMigrator.Core;

public enum NodeKind
{
    Folder,
    File,
    MailFolder,
    MailMessage,
    Calendar,
    CalendarEvent,
    ContactFolder,
    Contact,
}

public static class NodeKindExtensions
{
    public static bool IsContainer(this NodeKind kind) => kind switch
    {
        NodeKind.Folder or NodeKind.MailFolder or NodeKind.Calendar or NodeKind.ContactFolder => true,
        _ => false,
    };
}
