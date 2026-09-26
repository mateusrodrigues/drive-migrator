using DriveMigrator.Core;
using DriveMigrator.Core.Calendar;

namespace DriveMigrator.Testing;

public sealed class FakeCalendarCapability() : InMemoryCapability(CapabilityKind.Calendar, NodeKind.Calendar, supportsNestedContainers: false), ICalendarCapability
{
    public MigrationNode AddEvent(MigrationNode calendar, CalendarEvent calendarEvent)
        => Add(calendar, EventNode(calendarEvent), new StoredEvent(calendarEvent, ImportOptions: null));

    /// <summary>The options an event was imported with, or null if it was seeded directly.</summary>
    public CalendarImportOptions? GetImportOptions(MigrationNode calendarEvent) => GetPayload<StoredEvent>(calendarEvent).ImportOptions;

    public Task<CalendarEvent> ReadEventAsync(MigrationNode calendarEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetPayload<StoredEvent>(calendarEvent).Event);
    }

    public Task<MigrationNode> ImportEventAsync(
        MigrationNode calendar,
        CalendarEvent calendarEvent,
        CalendarImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Add(calendar, EventNode(calendarEvent), new StoredEvent(calendarEvent, options)));
    }

    private static MigrationNode EventNode(CalendarEvent calendarEvent)
        => new(NewId(), calendarEvent.Title, NodeKind.CalendarEvent);

    private sealed record StoredEvent(CalendarEvent Event, CalendarImportOptions? ImportOptions);
}
