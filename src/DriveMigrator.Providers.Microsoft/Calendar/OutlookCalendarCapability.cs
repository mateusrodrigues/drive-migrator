using System.Globalization;
using System.Runtime.CompilerServices;
using DriveMigrator.Core;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Providers.Microsoft.Graph;
using DriveMigrator.Providers.Microsoft.Mail;

namespace DriveMigrator.Providers.Microsoft.Calendar;

/// <summary>
/// Outlook calendars through Microsoft Graph. Calendar ids are "me/calendars/{id}", event ids "me/events/{id}".
/// Graph can't store another service's iCalendar UID, so each copy is tagged with it in a custom extended property,
/// which is how re-runs recognise events that were already copied.
/// </summary>
internal sealed class OutlookCalendarCapability(GraphClient graph) : ICalendarCapability
{
    /// <summary>Extended property holding the source event's iCalendar UID on copies made by this app.</summary>
    internal const string SourceUidProperty = "String {7d1c3f06-3c56-4e5e-9b8f-0c2a9f3e4d71} Name DriveMigrator.SourceUid";

    /// <summary>Times come back in UTC (converted using the event's original zone) and bodies as plain text.</summary>
    internal const string Prefer = "outlook.timezone=\"UTC\", outlook.body-content-type=\"text\"";

    private const string EventFields = "id,subject,body,start,end,isAllDay,location,attendees,organizer,showAs,sensitivity,isReminderOn,reminderMinutesBeforeStart,recurrence,iCalUId,type,originalStartTimeZone,originalStart";

    private readonly Lock _gate = new();
    private Task<List<GraphCalendar>>? _calendars;

    public CapabilityKind Kind => CapabilityKind.Calendar;

    public string DisplayName => "Outlook Calendar";

    public bool SupportsNestedContainers => false;

    public bool ImportNotifiesAttendees => true;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (parent is null)
        {
            foreach (var calendar in await GetCalendarsAsync(refresh: true, cancellationToken).ConfigureAwait(false))
            {
                yield return CalendarNode(calendar);
            }

            yield break;
        }

        var url = $"{parent.Id}/events?$select=id,subject,start,isAllDay,type,originalStartTimeZone&$orderby=start/dateTime desc&$top=100";
        await foreach (var graphEvent in graph.GetPagedAsync<GraphEvent>(url, cancellationToken, Prefer).ConfigureAwait(false))
        {
            var start = StartOf(graphEvent);
            yield return new MigrationNode($"me/events/{graphEvent.Id}", string.IsNullOrWhiteSpace(graphEvent.Subject) ? "(no title)" : graphEvent.Subject, NodeKind.CalendarEvent)
            {
                Detail = FormatStart(start, graphEvent.IsAllDay == true, graphEvent.Type == "seriesMaster"),
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
        return calendars.FirstOrDefault(c => c.IsDefaultCalendar == true) is { } calendar ? CalendarNode(calendar) : null;
    }

    public async Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        var created = await graph.SendJsonAsync<GraphCalendar>(HttpMethod.Post, "me/calendars", new Dictionary<string, object> { ["name"] = name }, cancellationToken).ConfigureAwait(false);
        return CalendarNode(created);
    }

    public async Task<bool> ContainsEventAsync(MigrationNode calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (calendarEvent.ICalUid is not { Length: > 0 } uid)
        {
            return false;
        }

        var filter = Uri.EscapeDataString($"singleValueExtendedProperties/Any(ep: ep/id eq '{SourceUidProperty}' and ep/value eq '{uid.Replace("'", "''", StringComparison.Ordinal)}')");
        var page = await graph.GetAsync<GraphPage<CreatedItem>>($"{calendar.Id}/events?$filter={filter}&$select=id&$top=1", cancellationToken).ConfigureAwait(false);
        return page.Value.Count > 0;
    }

    public async Task<CalendarEvent> ReadEventAsync(MigrationNode calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var master = await graph.GetAsync<GraphEvent>($"{calendarEvent.Id}?$select={EventFields}", cancellationToken, Prefer).ConfigureAwait(false);
        var result = ToEvent(master);
        if (master.Type != "seriesMaster" || master.Recurrence is null)
        {
            return result;
        }

        // Deleted and modified occurrences are only returned when asked for on the series master.
        var details = await graph.GetAsync<GraphEvent>(
            $"{calendarEvent.Id}?$select=id,cancelledOccurrences,exceptionOccurrences&$expand=exceptionOccurrences($select={EventFields})",
            cancellationToken,
            Prefer).ConfigureAwait(false);

        var timeZone = result.Start.TimeZone;
        var allDay = result.IsAllDay;
        var recurrence = result.Recurrence.ToList();
        foreach (var occurrenceId in details.CancelledOccurrences ?? [])
        {
            // "OID.{seriesMasterId}.2026-01-05": the date of the cancelled occurrence, at the series' start time.
            if (DateOnly.TryParseExact(occurrenceId[^10..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                recurrence.Add(Recurrence.ExDate(new EventTime(date.ToDateTime(TimeOnly.FromDateTime(result.Start.DateTime)), timeZone), allDay));
            }
        }

        var exceptions = new List<CalendarEvent>();
        foreach (var exception in details.ExceptionOccurrences ?? [])
        {
            var original = exception.OriginalStart is { } o ? EventTime.FromUtc(o.UtcDateTime, timeZone) : null;
            if (original is not null)
            {
                recurrence.Add(Recurrence.ExDate(allDay ? original with { DateTime = original.DateTime.Date } : original, allDay));
            }

            exceptions.Add(ToEvent(exception) with { Recurrence = [], OriginalStart = original });
        }

        return result with { Recurrence = recurrence, Exceptions = exceptions };
    }

    public async Task<MigrationNode> ImportEventAsync(
        MigrationNode calendar,
        CalendarEvent calendarEvent,
        CalendarImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var created = await graph.SendJsonAsync<GraphEvent>(HttpMethod.Post, $"{calendar.Id}/events", ToGraph(calendarEvent), cancellationToken).ConfigureAwait(false);
        var path = $"me/events/{created.Id}";

        if (calendarEvent.Recurrence.Count > 0)
        {
            await DeleteExcludedOccurrencesAsync(path, calendarEvent, cancellationToken).ConfigureAwait(false);
        }

        // Modified occurrences become standalone events (their original dates were excluded above).
        foreach (var exception in calendarEvent.Exceptions)
        {
            var uid = calendarEvent.ICalUid is null || exception.OriginalStart is null
                ? null
                : $"{calendarEvent.ICalUid}-{exception.OriginalStart.DateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}";
            await graph.SendJsonAsync<GraphEvent>(HttpMethod.Post, $"{calendar.Id}/events", ToGraph(exception with { Recurrence = [], Exceptions = [], ICalUid = uid }), cancellationToken).ConfigureAwait(false);
        }

        return new MigrationNode(path, calendarEvent.Title, NodeKind.CalendarEvent);
    }

    internal static Dictionary<string, object> ToGraph(CalendarEvent calendarEvent)
    {
        var allDay = calendarEvent.IsAllDay;
        var json = new Dictionary<string, object>
        {
            ["subject"] = calendarEvent.Title,
            ["body"] = new Dictionary<string, object> { ["contentType"] = "text", ["content"] = calendarEvent.Description ?? string.Empty },
            ["start"] = Time(calendarEvent.Start, allDay),
            ["end"] = Time(calendarEvent.End, allDay),
            ["isAllDay"] = allDay,
            ["showAs"] = calendarEvent.ShowAs switch
            {
                EventAvailability.Free => "free",
                EventAvailability.Tentative => "tentative",
                EventAvailability.OutOfOffice => "oof",
                _ => "busy",
            },
            ["sensitivity"] = calendarEvent.IsPrivate ? "private" : "normal",
            ["isReminderOn"] = calendarEvent.ReminderMinutesBefore.Count > 0,
        };
        if (calendarEvent.ReminderMinutesBefore.Count > 0)
        {
            json["reminderMinutesBeforeStart"] = calendarEvent.ReminderMinutesBefore.Min();
        }

        if (!string.IsNullOrWhiteSpace(calendarEvent.Location))
        {
            json["location"] = new Dictionary<string, object> { ["displayName"] = calendarEvent.Location };
        }

        if (calendarEvent.Attendees.Count > 0)
        {
            json["attendees"] = calendarEvent.Attendees.Select(a => new Dictionary<string, object>
            {
                ["emailAddress"] = new Dictionary<string, object> { ["address"] = a.Email, ["name"] = a.Name ?? a.Email },
                ["type"] = a.IsOptional ? "optional" : "required",
            }).ToList();
        }

        if (Recurrence.ParseRule(calendarEvent.Recurrence) is { } rule)
        {
            json["recurrence"] = GraphRecurrence.ToGraph(rule, calendarEvent.Start, allDay);
        }

        if (calendarEvent.ICalUid is { Length: > 0 } uid)
        {
            json["singleValueExtendedProperties"] = new[] { new Dictionary<string, object> { ["id"] = SourceUidProperty, ["value"] = uid } };
        }

        return json;
    }

    internal static CalendarEvent ToEvent(GraphEvent graphEvent)
    {
        var allDay = graphEvent.IsAllDay == true;
        var start = StartOf(graphEvent);
        var end = TimeOf(graphEvent.End, start.TimeZone, allDay) ?? start;
        return new CalendarEvent
        {
            Title = string.IsNullOrWhiteSpace(graphEvent.Subject) ? "(no title)" : graphEvent.Subject,
            Description = string.IsNullOrWhiteSpace(graphEvent.Body?.Content) ? null : graphEvent.Body.Content.Trim(),
            Location = string.IsNullOrWhiteSpace(graphEvent.Location?.DisplayName) ? null : graphEvent.Location.DisplayName,
            Start = start,
            End = end,
            IsAllDay = allDay,
            Recurrence = graphEvent.Recurrence is { } recurrence ? [GraphRecurrence.ToRule(recurrence, start, allDay)] : [],
            Organizer = graphEvent.Organizer?.EmailAddress is { Address: { } organizer } o ? new EventParticipant(organizer, o.Name) : null,
            Attendees = [.. (graphEvent.Attendees ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.EmailAddress?.Address))
                .Select(a => new EventParticipant(a.EmailAddress!.Address!, a.EmailAddress.Name)
                {
                    IsOptional = string.Equals(a.Type, "optional", StringComparison.OrdinalIgnoreCase),
                    Response = a.Status?.Response?.ToUpperInvariant() switch
                    {
                        "ACCEPTED" => AttendeeResponse.Accepted,
                        "TENTATIVELYACCEPTED" => AttendeeResponse.Tentative,
                        "DECLINED" => AttendeeResponse.Declined,
                        _ => AttendeeResponse.None,
                    },
                })],
            ShowAs = graphEvent.ShowAs?.ToUpperInvariant() switch
            {
                "FREE" or "WORKINGELSEWHERE" => EventAvailability.Free,
                "TENTATIVE" => EventAvailability.Tentative,
                "OOF" => EventAvailability.OutOfOffice,
                _ => EventAvailability.Busy,
            },
            IsPrivate = graphEvent.Sensitivity?.ToUpperInvariant() is "PRIVATE" or "CONFIDENTIAL" or "PERSONAL",
            ReminderMinutesBefore = graphEvent.IsReminderOn == true && graphEvent.ReminderMinutesBeforeStart is { } minutes ? [minutes] : [],
            ICalUid = graphEvent.ICalUId,
        };
    }

    /// <summary>
    /// The event's start as wall-clock time in its original zone. With <see cref="Prefer"/>, Graph returns UTC; the
    /// original zone gives the local time (and for all-day events, the right date).
    /// </summary>
    private static EventTime StartOf(GraphEvent graphEvent)
        => TimeOf(graphEvent.Start, TimeZones.ToIana(graphEvent.OriginalStartTimeZone), graphEvent.IsAllDay == true)
            ?? new EventTime(DateTime.UtcNow, TimeZones.Utc);

    private static EventTime? TimeOf(DateTimeTimeZone? value, string? timeZone, bool allDay)
    {
        if (value is null || !DateTime.TryParse(value.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        var utc = value.TimeZone is null or "UTC" ? parsed : new EventTime(parsed, TimeZones.ToIana(value.TimeZone) ?? TimeZones.Utc).ToUtc();
        if (timeZone is null)
        {
            // Unknown or custom zone: all-day events are midnight somewhere near UTC, so round to the nearest date.
            return allDay ? new EventTime(utc.AddHours(12).Date, TimeZones.Utc) : new EventTime(utc, TimeZones.Utc);
        }

        var local = EventTime.FromUtc(utc, timeZone);
        return allDay ? local with { DateTime = local.DateTime.AddHours(12).Date } : local;
    }

    private static Dictionary<string, object> Time(EventTime time, bool allDay) => new()
    {
        ["dateTime"] = (allDay ? time.DateTime.Date : time.DateTime).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        ["timeZone"] = TimeZones.ToWindows(time.TimeZone),
    };

    private static string FormatStart(EventTime start, bool allDay, bool recurring)
    {
        var text = start.DateTime.ToString(allDay ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return recurring ? $"repeats from {text}" : text;
    }

    private static MigrationNode CalendarNode(GraphCalendar calendar)
        => new($"me/calendars/{calendar.Id}", calendar.Name, NodeKind.Calendar) { Role = calendar.IsDefaultCalendar == true ? ContainerRole.DefaultCalendar : null };

    /// <summary>Graph can't create a series with exclusions; delete the excluded occurrences after creating it.</summary>
    private async Task DeleteExcludedOccurrencesAsync(string eventPath, CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        var timeZone = calendarEvent.Start.TimeZone;
        foreach (var excluded in Recurrence.ParseExDates(calendarEvent.Recurrence, timeZone))
        {
            var utc = calendarEvent.IsAllDay ? excluded.Date : new EventTime(excluded, timeZone).ToUtc();
            var from = Uri.EscapeDataString(utc.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            var to = Uri.EscapeDataString(utc.AddDays(2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            var instances = graph.GetPagedAsync<GraphEvent>($"{eventPath}/instances?startDateTime={from}&endDateTime={to}&$select=id,start,isAllDay,originalStartTimeZone", cancellationToken, Prefer);
            await foreach (var instance in instances.ConfigureAwait(false))
            {
                var start = TimeOf(instance.Start, timeZone, calendarEvent.IsAllDay);
                var matches = calendarEvent.IsAllDay ? start?.DateTime.Date == excluded.Date : start?.DateTime == excluded;
                if (matches)
                {
                    using var _ = await graph.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"me/events/{instance.Id}"), cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
        }
    }

    private Task<List<GraphCalendar>> GetCalendarsAsync(bool refresh, CancellationToken cancellationToken)
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

    private async Task<List<GraphCalendar>> LoadCalendarsAsync(CancellationToken cancellationToken)
        => await graph.GetPagedAsync<GraphCalendar>("me/calendars?$select=id,name,isDefaultCalendar,canEdit&$top=100", cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false);
}
