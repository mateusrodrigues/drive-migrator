namespace DriveMigrator.Core.Calendar;

public sealed record CalendarImportOptions(AttendeeHandling AttendeeHandling)
{
    public static CalendarImportOptions Default { get; } = new(AttendeeHandling.EmbedInDescription);
}

public enum AttendeeHandling
{
    /// <summary>Create the event without attendees and list them in the description, so nobody is notified.</summary>
    EmbedInDescription,

    /// <summary>Keep real attendees. Some providers (Microsoft Graph) will send them invitations.</summary>
    KeepAttendees,
}
