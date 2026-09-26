namespace DriveMigrator.Core.Calendar;

/// <summary>A provider-neutral calendar event.</summary>
public sealed record CalendarEvent
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public string? Location { get; init; }

    public required EventTime Start { get; init; }

    public required EventTime End { get; init; }

    /// <summary>For all-day events only the date part of <see cref="Start"/>/<see cref="End"/> is meaningful; End is exclusive.</summary>
    public bool IsAllDay { get; init; }

    /// <summary>RFC 5545 recurrence lines (RRULE, EXDATE, RDATE), as Google expresses them.</summary>
    public IReadOnlyList<string> Recurrence { get; init; } = [];

    public EventParticipant? Organizer { get; init; }

    public IReadOnlyList<EventParticipant> Attendees { get; init; } = [];

    public EventAvailability ShowAs { get; init; } = EventAvailability.Busy;

    public bool IsPrivate { get; init; }

    public IReadOnlyList<int> ReminderMinutesBefore { get; init; } = [];

    /// <summary>The iCalendar UID, used to recognise the same event across services.</summary>
    public string? ICalUid { get; init; }
}

/// <summary>A wall-clock time in an IANA time zone (e.g. "Europe/Lisbon").</summary>
public sealed record EventTime(DateTime DateTime, string TimeZone);

public sealed record EventParticipant(string Email, string? Name)
{
    public AttendeeResponse Response { get; init; } = AttendeeResponse.None;

    public bool IsOptional { get; init; }
}

public enum AttendeeResponse
{
    None,
    Accepted,
    Tentative,
    Declined,
}

public enum EventAvailability
{
    Free,
    Tentative,
    Busy,
    OutOfOffice,
}
