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

    /// <summary>
    /// RFC 5545 recurrence lines (RRULE, EXDATE, RDATE), as Google expresses them. EXDATE values are wall-clock times
    /// in the event's time zone ("EXDATE;TZID=Europe/Lisbon:20260105T090000") or dates for all-day events
    /// ("EXDATE;VALUE=DATE:20260105"). Occurrences that were modified are excluded here and listed in <see cref="Exceptions"/>.
    /// </summary>
    public IReadOnlyList<string> Recurrence { get; init; } = [];

    /// <summary>
    /// For a recurring event: occurrences that differ from the series (moved, retitled...). They are copied as
    /// standalone events; their original dates are already excluded via EXDATE in <see cref="Recurrence"/>.
    /// </summary>
    public IReadOnlyList<CalendarEvent> Exceptions { get; init; } = [];

    /// <summary>For an entry of <see cref="Exceptions"/>: when this occurrence was originally scheduled.</summary>
    public EventTime? OriginalStart { get; init; }

    public EventParticipant? Organizer { get; init; }

    public IReadOnlyList<EventParticipant> Attendees { get; init; } = [];

    public EventAvailability ShowAs { get; init; } = EventAvailability.Busy;

    public bool IsPrivate { get; init; }

    public IReadOnlyList<int> ReminderMinutesBefore { get; init; } = [];

    /// <summary>The iCalendar UID, used to recognise the same event across services.</summary>
    public string? ICalUid { get; init; }
}

/// <summary>A wall-clock time in an IANA time zone (e.g. "Europe/Lisbon").</summary>
public sealed record EventTime(DateTime DateTime, string TimeZone)
{
    /// <summary>The same instant in UTC.</summary>
    public DateTime ToUtc() => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(DateTime, DateTimeKind.Unspecified), TimeZones.Find(TimeZone));

    /// <summary>The wall-clock time in <paramref name="timeZone"/> of a UTC instant.</summary>
    public static EventTime FromUtc(DateTime utc, string timeZone)
        => new(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZones.Find(timeZone)), timeZone);
}

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
