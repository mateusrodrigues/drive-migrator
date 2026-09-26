using DriveMigrator.Core;
using DriveMigrator.Core.Calendar;

namespace DriveMigrator.Testing;

public sealed class FakeCalendarCapability() : InMemoryCapability(CapabilityKind.Calendar, NodeKind.Calendar, supportsNestedContainers: false), ICalendarCapability
{
    /// <summary>Simulates Outlook, which invites attendees of imported events.</summary>
    public bool ImportNotifiesAttendees { get; set; }

    public MigrationNode AddEvent(MigrationNode calendar, CalendarEvent calendarEvent)
        => Add(calendar, EventNode(calendarEvent), new StoredEvent(calendarEvent, ImportOptions: null));

    /// <summary>Adds a calendar with a role, such as the default calendar.</summary>
    public MigrationNode AddSpecialCalendar(ContainerRole role, string name)
        => Add(null, new MigrationNode(NewId(), name, NodeKind.Calendar) { Role = role }, payload: null);

    /// <summary>The options an event was imported with, or null if it was seeded directly.</summary>
    public CalendarImportOptions? GetImportOptions(MigrationNode calendarEvent) => GetPayload<StoredEvent>(calendarEvent).ImportOptions;

    public async Task<MigrationNode?> GetSpecialContainerAsync(ContainerRole role, CancellationToken cancellationToken = default)
    {
        await foreach (var node in GetChildrenAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (node.Role == role)
            {
                return node;
            }
        }

        return null;
    }

    public async Task<bool> ContainsEventAsync(MigrationNode calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (calendarEvent.ICalUid is null)
        {
            return false;
        }

        await foreach (var node in GetChildrenAsync(calendar, cancellationToken).ConfigureAwait(false))
        {
            if (GetPayload<StoredEvent>(node).Event.ICalUid == calendarEvent.ICalUid)
            {
                return true;
            }
        }

        return false;
    }

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
