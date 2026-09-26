namespace DriveMigrator.Core.Calendar;

/// <summary>Calendars are the containers; they cannot be nested.</summary>
public interface ICalendarCapability : ICapability
{
    /// <summary>
    /// Whether importing an event with attendees sends them invitations (true for Outlook, false for Google's
    /// import). The options dialog only asks about attendees when it does.
    /// </summary>
    bool ImportNotifiesAttendees { get; }

    /// <summary>Whether <paramref name="calendar"/> already has a copy of this event (matched by its iCalendar UID).</summary>
    Task<bool> ContainsEventAsync(MigrationNode calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken = default);

    Task<CalendarEvent> ReadEventAsync(MigrationNode calendarEvent, CancellationToken cancellationToken = default);

    Task<MigrationNode> ImportEventAsync(
        MigrationNode calendar,
        CalendarEvent calendarEvent,
        CalendarImportOptions options,
        CancellationToken cancellationToken = default);
}
