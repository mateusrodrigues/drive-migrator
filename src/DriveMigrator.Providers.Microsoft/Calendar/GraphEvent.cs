using DriveMigrator.Providers.Microsoft.Mail;

namespace DriveMigrator.Providers.Microsoft.Calendar;

// Subsets of Graph's calendar and event resources.
internal sealed record GraphCalendar(string Id, string Name, bool? IsDefaultCalendar, bool? CanEdit);

internal sealed record GraphEvent
{
    public required string Id { get; init; }

    public string? Subject { get; init; }

    public ItemBody? Body { get; init; }

    public DateTimeTimeZone? Start { get; init; }

    public DateTimeTimeZone? End { get; init; }

    public bool? IsAllDay { get; init; }

    public GraphLocation? Location { get; init; }

    public List<GraphAttendee>? Attendees { get; init; }

    public Recipient? Organizer { get; init; }

    public string? ShowAs { get; init; }

    public string? Sensitivity { get; init; }

    public bool? IsReminderOn { get; init; }

    public int? ReminderMinutesBeforeStart { get; init; }

    public PatternedRecurrence? Recurrence { get; init; }

    public string? ICalUId { get; init; }

    public string? Type { get; init; }

    public string? OriginalStartTimeZone { get; init; }

    public DateTimeOffset? OriginalStart { get; init; }

    public List<string>? CancelledOccurrences { get; init; }

    public List<GraphEvent>? ExceptionOccurrences { get; init; }
}

internal sealed record ItemBody(string? ContentType, string? Content);

internal sealed record DateTimeTimeZone(string DateTime, string? TimeZone);

internal sealed record GraphLocation(string? DisplayName);

internal sealed record GraphAttendee(EmailAddress? EmailAddress, string? Type, GraphResponseStatus? Status);

internal sealed record GraphResponseStatus(string? Response);

internal sealed record PatternedRecurrence(RecurrencePattern Pattern, RecurrenceRange Range);

internal sealed record RecurrencePattern(
    string Type,
    int Interval,
    int? Month,
    int? DayOfMonth,
    List<string>? DaysOfWeek,
    string? FirstDayOfWeek,
    string? Index);

internal sealed record RecurrenceRange(string Type, string? StartDate, string? EndDate, int? NumberOfOccurrences, string? RecurrenceTimeZone);
