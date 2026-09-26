using System.Globalization;
using System.Runtime.CompilerServices;
using DriveMigrator.Core;
using DriveMigrator.Core.Calendar;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using GoogleEvent = Google.Apis.Calendar.v3.Data.Event;

namespace DriveMigrator.Providers.Google.Calendar;

/// <summary>
/// Google Calendar. Node ids are calendar ids and "{calendarId}|{eventId}" for events. Listing a calendar reads
/// all its events once: modified and deleted occurrences of recurring events are folded into their series (as
/// exceptions and EXDATEs) instead of being listed on their own. Events are added with events.import, which keeps
/// attendees without notifying them.
/// </summary>
internal sealed class GoogleCalendarCapability : ICalendarCapability
{
    private const char Separator = '|';
    private const string ListFields = "nextPageToken,items(id,status,summary,start,end,recurringEventId,originalStartTime,eventType,recurrence)";

    private static readonly HashSet<string> SkippedTypes = new(StringComparer.OrdinalIgnoreCase) { "workingLocation", "birthday" };

    private readonly CalendarService _calendar;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<CalendarScan>> _scans = [];
    private Task<List<CalendarListEntry>>? _calendars;

    public GoogleCalendarCapability(CalendarService calendar)
    {
        _calendar = calendar;
        GoogleBackOff.Install(calendar);
    }

    public CapabilityKind Kind => CapabilityKind.Calendar;

    public string DisplayName => "Google Calendar";

    public bool SupportsNestedContainers => false;

    public bool ImportNotifiesAttendees => false;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (parent is null)
        {
            foreach (var entry in await GetCalendarsAsync(refresh: true, cancellationToken).ConfigureAwait(false))
            {
                yield return CalendarNode(entry);
            }

            yield break;
        }

        var scan = await ScanAsync(parent.Id, refresh: true, cancellationToken).ConfigureAwait(false);
        foreach (var item in scan.Events)
        {
            var start = ToTime(item.Start, scan.TimeZone);
            var text = start.DateTime.ToString(item.Start?.Date is null ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
            yield return new MigrationNode($"{parent.Id}{Separator}{item.Id}", string.IsNullOrWhiteSpace(item.Summary) ? "(no title)" : item.Summary, NodeKind.CalendarEvent)
            {
                Detail = item.Recurrence is { Count: > 0 } ? $"repeats from {text}" : text,
            };
        }
    }

    public async Task<MigrationNode?> GetSpecialContainerAsync(ContainerRole role, CancellationToken cancellationToken = default)
    {
        if (role != ContainerRole.DefaultCalendar)
        {
            return null;
        }

        var calendars = await GetCalendarsAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        return calendars.FirstOrDefault(c => c.Primary == true) is { } primary ? CalendarNode(primary) : null;
    }

    public async Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        var calendars = await GetCalendarsAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        var created = await _calendar.Calendars.Insert(new global::Google.Apis.Calendar.v3.Data.Calendar
        {
            Summary = name,
            TimeZone = calendars.FirstOrDefault(c => c.Primary == true)?.TimeZone,
        }).ExecuteAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Google returned nothing when creating the calendar '{name}'.");

        lock (_gate)
        {
            calendars.Add(new CalendarListEntry { Id = created.Id, Summary = created.Summary, TimeZone = created.TimeZone });
        }

        return new MigrationNode(created.Id, created.Summary ?? name, NodeKind.Calendar);
    }

    public async Task<bool> ContainsEventAsync(MigrationNode calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (calendarEvent.ICalUid is not { Length: > 0 } uid)
        {
            return false;
        }

        var request = _calendar.Events.List(calendar.Id);
        request.ICalUID = uid;
        request.ShowDeleted = false;
        request.MaxResults = 1;
        request.Fields = "items(id)";
        var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return page?.Items is { Count: > 0 };
    }

    public async Task<CalendarEvent> ReadEventAsync(MigrationNode calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var (calendarId, eventId) = Split(calendarEvent.Id);
        var scan = await ScanAsync(calendarId, refresh: false, cancellationToken).ConfigureAwait(false);
        var item = await _calendar.Events.Get(calendarId, eventId).ExecuteAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Google returned nothing for event {eventId}.");

        var result = ToEvent(item, scan.TimeZone);
        if (!scan.Exceptions.TryGetValue(eventId, out var exceptions))
        {
            return result;
        }

        var recurrence = result.Recurrence.ToList();
        var modified = new List<CalendarEvent>();
        foreach (var exception in exceptions)
        {
            var original = ToTime(exception.OriginalStartTime, scan.TimeZone);
            recurrence.Add(Recurrence.ExDate(original, result.IsAllDay));
            if (exception.Status != "cancelled")
            {
                // Listing returns only a few fields; read the whole modified occurrence.
                var full = await _calendar.Events.Get(calendarId, exception.Id).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (full is not null)
                {
                    modified.Add(ToEvent(full, scan.TimeZone) with { Recurrence = [], OriginalStart = original });
                }
            }
        }

        return result with { Recurrence = recurrence, Exceptions = modified };
    }

    public async Task<MigrationNode> ImportEventAsync(
        MigrationNode calendar,
        CalendarEvent calendarEvent,
        CalendarImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var uid = calendarEvent.ICalUid ?? $"{Guid.NewGuid():N}@drivemigrator";
        var imported = await ImportOneAsync(calendar.Id, calendarEvent, uid, cancellationToken).ConfigureAwait(false);

        // Modified occurrences become standalone events (their original dates are excluded from the series).
        foreach (var exception in calendarEvent.Exceptions)
        {
            var exceptionUid = exception.OriginalStart is { } original
                ? $"{uid}-{original.DateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}"
                : $"{Guid.NewGuid():N}@drivemigrator";
            await ImportOneAsync(calendar.Id, exception with { Recurrence = [], Exceptions = [] }, exceptionUid, cancellationToken).ConfigureAwait(false);
        }

        return new MigrationNode($"{calendar.Id}{Separator}{imported.Id}", calendarEvent.Title, NodeKind.CalendarEvent);
    }

    internal static GoogleEvent ToGoogle(CalendarEvent calendarEvent, string uid)
    {
        var item = new GoogleEvent
        {
            ICalUID = uid,
            Summary = calendarEvent.Title,
            Description = calendarEvent.Description,
            Location = calendarEvent.Location,
            Start = ToGoogleTime(calendarEvent.Start, calendarEvent.IsAllDay),
            End = ToGoogleTime(calendarEvent.End, calendarEvent.IsAllDay),
            Recurrence = calendarEvent.Recurrence.Count > 0 ? [.. calendarEvent.Recurrence] : null,
            Transparency = calendarEvent.ShowAs == EventAvailability.Free ? "transparent" : "opaque",
            Visibility = calendarEvent.IsPrivate ? "private" : null,
            Reminders = calendarEvent.ReminderMinutesBefore.Count > 0
                ? new GoogleEvent.RemindersData
                {
                    UseDefault = false,
                    Overrides = [.. calendarEvent.ReminderMinutesBefore.Select(m => new EventReminder { Method = "popup", Minutes = m })],
                }
                : new GoogleEvent.RemindersData { UseDefault = true },
        };

        if (calendarEvent.Organizer is { } organizer)
        {
            item.Organizer = new GoogleEvent.OrganizerData { Email = organizer.Email, DisplayName = organizer.Name };
        }

        if (calendarEvent.Attendees.Count > 0)
        {
            item.Attendees = [.. calendarEvent.Attendees.Select(a => new EventAttendee
            {
                Email = a.Email,
                DisplayName = a.Name,
                Optional = a.IsOptional ? true : null,
                ResponseStatus = a.Response switch
                {
                    AttendeeResponse.Accepted => "accepted",
                    AttendeeResponse.Tentative => "tentative",
                    AttendeeResponse.Declined => "declined",
                    _ => "needsAction",
                },
            })];
        }

        return item;
    }

    internal static CalendarEvent ToEvent(GoogleEvent item, string calendarTimeZone)
    {
        var allDay = item.Start?.Date is not null;
        var start = ToTime(item.Start, calendarTimeZone);
        return new CalendarEvent
        {
            Title = string.IsNullOrWhiteSpace(item.Summary) ? "(no title)" : item.Summary,
            Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description,
            Location = string.IsNullOrWhiteSpace(item.Location) ? null : item.Location,
            Start = start,
            End = item.End is null ? start : ToTime(item.End, calendarTimeZone),
            IsAllDay = allDay,
            Recurrence = [.. item.Recurrence ?? []],
            Organizer = item.Organizer?.Email is { } organizer ? new EventParticipant(organizer, item.Organizer.DisplayName) : null,
            Attendees = [.. (item.Attendees ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.Email) && a.Resource != true)
                .Select(a => new EventParticipant(a.Email, a.DisplayName)
                {
                    IsOptional = a.Optional == true,
                    Response = a.ResponseStatus switch
                    {
                        "accepted" => AttendeeResponse.Accepted,
                        "tentative" => AttendeeResponse.Tentative,
                        "declined" => AttendeeResponse.Declined,
                        _ => AttendeeResponse.None,
                    },
                })],
            ShowAs = item.EventType == "outOfOffice" ? EventAvailability.OutOfOffice
                : item.Transparency == "transparent" ? EventAvailability.Free
                : EventAvailability.Busy,
            IsPrivate = item.Visibility is "private" or "confidential",
            ReminderMinutesBefore = item.Reminders?.UseDefault == false
                ? [.. (item.Reminders.Overrides ?? []).Where(r => r.Minutes is not null).Select(r => r.Minutes!.Value)]
                : [],
            ICalUid = item.ICalUID,
        };
    }

    /// <summary>Wall-clock time in the event's zone (or the calendar's, when the event has none).</summary>
    internal static EventTime ToTime(EventDateTime? value, string calendarTimeZone)
    {
        var timeZone = value?.TimeZone ?? calendarTimeZone;
        if (value?.Date is { } date)
        {
            return new EventTime(DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture), timeZone);
        }

        if (value?.DateTimeRaw is { } raw && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
        {
            return EventTime.FromUtc(instant.UtcDateTime, timeZone);
        }

        return new EventTime(DateTime.UtcNow, timeZone);
    }

    private static EventDateTime ToGoogleTime(EventTime time, bool allDay)
        => allDay
            ? new EventDateTime { Date = time.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : new EventDateTime { DateTimeRaw = time.DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), TimeZone = time.TimeZone };

    private static (string CalendarId, string EventId) Split(string nodeId)
    {
        var separator = nodeId.LastIndexOf(Separator);
        return separator < 0 ? throw new ArgumentException($"'{nodeId}' is not a Google Calendar event id.", nameof(nodeId)) : (nodeId[..separator], nodeId[(separator + 1)..]);
    }

    private static MigrationNode CalendarNode(CalendarListEntry entry)
        => new(entry.Id, entry.SummaryOverride ?? entry.Summary ?? entry.Id, NodeKind.Calendar) { Role = entry.Primary == true ? ContainerRole.DefaultCalendar : null };

    private async Task<GoogleEvent> ImportOneAsync(string calendarId, CalendarEvent calendarEvent, string uid, CancellationToken cancellationToken)
        => await _calendar.Events.Import(ToGoogle(calendarEvent, uid), calendarId).ExecuteAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Google returned nothing when importing '{calendarEvent.Title}'.");

    private Task<CalendarScan> ScanAsync(string calendarId, bool refresh, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (refresh || !_scans.TryGetValue(calendarId, out var scan) || scan.IsFaulted || scan.IsCanceled)
            {
                scan = LoadScanAsync(calendarId, cancellationToken);
                _scans[calendarId] = scan;
            }

            return scan;
        }
    }

    /// <summary>Reads every event once, separating series and single events from occurrence exceptions.</summary>
    private async Task<CalendarScan> LoadScanAsync(string calendarId, CancellationToken cancellationToken)
    {
        var calendars = await GetCalendarsAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        var timeZone = calendars.FirstOrDefault(c => c.Id == calendarId)?.TimeZone ?? TimeZones.Utc;
        var events = new List<GoogleEvent>();
        var exceptions = new Dictionary<string, List<GoogleEvent>>();

        var request = _calendar.Events.List(calendarId);
        request.SingleEvents = false;
        request.ShowDeleted = true;
        request.MaxResults = 2500;
        request.Fields = ListFields;
        do
        {
            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in page?.Items ?? [])
            {
                if (item.RecurringEventId is { } master)
                {
                    // A moved/edited or deleted occurrence of a series.
                    if (!exceptions.TryGetValue(master, out var list))
                    {
                        exceptions[master] = list = [];
                    }

                    list.Add(item);
                }
                else if (item.Status != "cancelled" && !SkippedTypes.Contains(item.EventType ?? string.Empty))
                {
                    events.Add(item);
                }
            }

            request.PageToken = page?.NextPageToken;
        }
        while (request.PageToken is not null);

        return new CalendarScan(timeZone, events, exceptions);
    }

    private Task<List<CalendarListEntry>> GetCalendarsAsync(bool refresh, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (refresh || _calendars is null || _calendars.IsFaulted || _calendars.IsCanceled)
            {
                _calendars = LoadCalendarsAsync(cancellationToken);
            }

            return _calendars;
        }
    }

    private async Task<List<CalendarListEntry>> LoadCalendarsAsync(CancellationToken cancellationToken)
    {
        var result = new List<CalendarListEntry>();
        var request = _calendar.CalendarList.List();
        do
        {
            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            result.AddRange(page?.Items ?? []);
            request.PageToken = page?.NextPageToken;
        }
        while (request.PageToken is not null);

        // Primary first, then by name.
        return [.. result.OrderByDescending(c => c.Primary == true).ThenBy(c => c.SummaryOverride ?? c.Summary, StringComparer.CurrentCultureIgnoreCase)];
    }

    private sealed record CalendarScan(string TimeZone, List<GoogleEvent> Events, Dictionary<string, List<GoogleEvent>> Exceptions);
}
