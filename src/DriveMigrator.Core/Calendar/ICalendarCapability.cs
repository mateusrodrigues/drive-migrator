namespace DriveMigrator.Core.Calendar;

/// <summary>Calendars are the containers; they cannot be nested.</summary>
public interface ICalendarCapability : ICapability
{
    Task<CalendarEvent> ReadEventAsync(MigrationNode calendarEvent, CancellationToken cancellationToken = default);

    Task<MigrationNode> ImportEventAsync(
        MigrationNode calendar,
        CalendarEvent calendarEvent,
        CalendarImportOptions options,
        CancellationToken cancellationToken = default);
}
