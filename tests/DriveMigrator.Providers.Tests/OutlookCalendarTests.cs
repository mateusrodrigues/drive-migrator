using System.Net;
using System.Text.Json;
using System.Web;
using DriveMigrator.Core;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Providers.Microsoft.Calendar;
using DriveMigrator.Providers.Microsoft.Graph;

namespace DriveMigrator.Providers.Tests;

public class OutlookCalendarTests
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Root_ListsCalendarsWithDefaultRole()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "value": [ { "id": "C1", "name": "Calendar", "isDefaultCalendar": true }, { "id": "C2", "name": "Holidays" } ] }"""));

        var nodes = await Create(handler).GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Equal([("me/calendars/C1", ContainerRole.DefaultCalendar), ("me/calendars/C2", (ContainerRole?)null)], nodes.Select(n => (n.Id, n.Role)));
    }

    [Fact]
    public async Task Read_ConvertsUtcBackToTheOriginalZone_AndAllDayDates()
    {
        var handler = new FakeHttpHandler(r => r.Url.Contains("E1", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""
                { "id": "E1", "subject": "Standup", "type": "singleInstance", "isAllDay": false,
                  "start": { "dateTime": "2026-07-01T08:00:00.0000000", "timeZone": "UTC" },
                  "end": { "dateTime": "2026-07-01T08:15:00.0000000", "timeZone": "UTC" },
                  "originalStartTimeZone": "GMT Standard Time", "body": { "contentType": "text", "content": " Notes " },
                  "attendees": [ { "emailAddress": { "name": "Ada", "address": "ada@x" }, "type": "optional", "status": { "response": "accepted" } } ],
                  "organizer": { "emailAddress": { "name": "Boss", "address": "boss@x" } },
                  "showAs": "oof", "sensitivity": "private", "isReminderOn": true, "reminderMinutesBeforeStart": 15, "iCalUId": "uid1" }
                """)
            : FakeHttpHandler.Json("""
                { "id": "E2", "subject": "Holiday", "type": "singleInstance", "isAllDay": true,
                  "start": { "dateTime": "2026-12-24T23:00:00.0000000", "timeZone": "UTC" },
                  "end": { "dateTime": "2026-12-25T23:00:00.0000000", "timeZone": "UTC" },
                  "originalStartTimeZone": "W. Europe Standard Time" }
                """));
        var calendar = Create(handler);

        var standup = await calendar.ReadEventAsync(new MigrationNode("me/events/E1", "Standup", NodeKind.CalendarEvent), Ct);
        var holiday = await calendar.ReadEventAsync(new MigrationNode("me/events/E2", "Holiday", NodeKind.CalendarEvent), Ct);

        Assert.Equal(new EventTime(new DateTime(2026, 7, 1, 9, 0, 0), "Europe/London"), standup.Start);
        Assert.Equal("Notes", standup.Description);
        Assert.Equal(("ada@x", AttendeeResponse.Accepted, true), (standup.Attendees[0].Email, standup.Attendees[0].Response, standup.Attendees[0].IsOptional));
        Assert.Equal("boss@x", standup.Organizer!.Email);
        Assert.Equal((EventAvailability.OutOfOffice, true, 15, "uid1"), (standup.ShowAs, standup.IsPrivate, standup.ReminderMinutesBefore[0], standup.ICalUid!));

        Assert.True(holiday.IsAllDay);
        Assert.Equal(new DateTime(2026, 12, 25), holiday.Start.DateTime);
        Assert.Equal(new DateTime(2026, 12, 26), holiday.End.DateTime);
        Assert.Equal(OutlookCalendarCapability.Prefer, Assert.Single(handler.Requests, r => r.Url.Contains("E1", StringComparison.Ordinal)).Prefer);
    }

    [Fact]
    public async Task Read_SeriesMasterIncludesCancelledAndModifiedOccurrences()
    {
        var handler = new FakeHttpHandler(r => r.Url.Contains("expand", StringComparison.Ordinal)
            ? FakeHttpHandler.Json("""
                { "id": "S1", "cancelledOccurrences": [ "OID.S1.2026-01-12" ],
                  "exceptionOccurrences": [ { "id": "X1", "subject": "Standup (moved)", "type": "exception", "isAllDay": false,
                      "start": { "dateTime": "2026-01-19T10:00:00", "timeZone": "UTC" }, "end": { "dateTime": "2026-01-19T10:15:00", "timeZone": "UTC" },
                      "originalStartTimeZone": "GMT Standard Time", "originalStart": "2026-01-19T09:00:00Z" } ] }
                """)
            : FakeHttpHandler.Json("""
                { "id": "S1", "subject": "Standup", "type": "seriesMaster", "isAllDay": false,
                  "start": { "dateTime": "2026-01-05T09:00:00", "timeZone": "UTC" }, "end": { "dateTime": "2026-01-05T09:15:00", "timeZone": "UTC" },
                  "originalStartTimeZone": "GMT Standard Time", "iCalUId": "series1",
                  "recurrence": { "pattern": { "type": "weekly", "interval": 1, "daysOfWeek": [ "monday" ], "firstDayOfWeek": "sunday" },
                                  "range": { "type": "noEnd", "startDate": "2026-01-05" } } }
                """));

        var series = await Create(handler).ReadEventAsync(new MigrationNode("me/events/S1", "Standup", NodeKind.CalendarEvent), Ct);

        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO;WKST=SU", "EXDATE;TZID=Europe/London:20260112T090000", "EXDATE;TZID=Europe/London:20260119T090000"],
            series.Recurrence);
        var moved = Assert.Single(series.Exceptions);
        Assert.Equal(("Standup (moved)", new DateTime(2026, 1, 19, 10, 0, 0), new DateTime(2026, 1, 19, 9, 0, 0)), (moved.Title, moved.Start.DateTime, moved.OriginalStart!.DateTime));
        Assert.Empty(moved.Recurrence);
    }

    [Fact]
    public async Task Import_CreatesSeries_DeletesExcludedOccurrence_AndAddsExceptions()
    {
        var handler = new FakeHttpHandler(r => (r.Method.Method, r.Url) switch
        {
            ("POST", _) => FakeHttpHandler.Json("""{ "id": "NEW" }""", HttpStatusCode.Created),
            ("GET", var u) when u.Contains("/instances", StringComparison.Ordinal) => FakeHttpHandler.Json("""
                { "value": [
                    { "id": "I1", "start": { "dateTime": "2026-01-05T09:00:00", "timeZone": "UTC" } },
                    { "id": "I2", "start": { "dateTime": "2026-01-12T09:00:00", "timeZone": "UTC" } } ] }
                """),
            ("DELETE", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            var other => throw new InvalidOperationException(other.ToString()),
        });
        var series = new CalendarEvent
        {
            Title = "Standup",
            Start = new EventTime(new DateTime(2026, 1, 5, 9, 0, 0), "Europe/Lisbon"),
            End = new EventTime(new DateTime(2026, 1, 5, 9, 15, 0), "Europe/Lisbon"),
            Recurrence = ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;TZID=Europe/Lisbon:20260112T090000"],
            ICalUid = "series1",
            ReminderMinutesBefore = [10, 30],
            Exceptions =
            [
                new CalendarEvent
                {
                    Title = "Standup (moved)",
                    Start = new EventTime(new DateTime(2026, 1, 12, 11, 0, 0), "Europe/Lisbon"),
                    End = new EventTime(new DateTime(2026, 1, 12, 11, 15, 0), "Europe/Lisbon"),
                    OriginalStart = new EventTime(new DateTime(2026, 1, 12, 9, 0, 0), "Europe/Lisbon"),
                },
            ],
        };

        var node = await Create(handler).ImportEventAsync(new MigrationNode("me/calendars/C1", "Calendar", NodeKind.Calendar), series, CalendarImportOptions.Default, Ct);

        Assert.Equal("me/events/NEW", node.Id);
        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.All(posts, p => Assert.Equal(Graph + "me/calendars/C1/events", p.Url));
        using var master = JsonDocument.Parse(posts[0].Body);
        var root = master.RootElement;
        Assert.Equal("2026-01-05T09:00:00", root.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("GMT Standard Time", root.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.Equal("weekly", root.GetProperty("recurrence").GetProperty("pattern").GetProperty("type").GetString());
        Assert.Equal(10, root.GetProperty("reminderMinutesBeforeStart").GetInt32());
        Assert.Equal("series1", root.GetProperty("singleValueExtendedProperties")[0].GetProperty("value").GetString());
        Assert.False(root.TryGetProperty("attendees", out _));

        Assert.Equal(Graph + "me/events/I2", Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete).Url);

        using var exception = JsonDocument.Parse(posts[1].Body);
        Assert.Equal("Standup (moved)", exception.RootElement.GetProperty("subject").GetString());
        Assert.False(exception.RootElement.TryGetProperty("recurrence", out _));
        Assert.Equal("series1-20260112T090000", exception.RootElement.GetProperty("singleValueExtendedProperties")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Import_AllDayEvent()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "id": "NEW" }""", HttpStatusCode.Created));
        var holiday = new CalendarEvent
        {
            Title = "Holiday",
            IsAllDay = true,
            Start = new EventTime(new DateTime(2026, 12, 25), "Europe/Berlin"),
            End = new EventTime(new DateTime(2026, 12, 26), "Europe/Berlin"),
            ShowAs = EventAvailability.Free,
        };

        await Create(handler).ImportEventAsync(new MigrationNode("me/calendars/C1", "Calendar", NodeKind.Calendar), holiday, CalendarImportOptions.Default, Ct);

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.True(body.RootElement.GetProperty("isAllDay").GetBoolean());
        Assert.Equal(("2026-12-25T00:00:00", "2026-12-26T00:00:00"), (body.RootElement.GetProperty("start").GetProperty("dateTime").GetString(), body.RootElement.GetProperty("end").GetProperty("dateTime").GetString()));
        Assert.Equal("free", body.RootElement.GetProperty("showAs").GetString());
    }

    [Fact]
    public async Task Contains_FiltersOnTheSourceUidProperty()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "value": [] }"""));
        var calendar = new MigrationNode("me/calendars/C1", "Calendar", NodeKind.Calendar);

        Assert.False(await Create(handler).ContainsEventAsync(calendar, new CalendarEvent { Title = "x", Start = new EventTime(DateTime.Today, "UTC"), End = new EventTime(DateTime.Today, "UTC"), ICalUid = "u'1" }, Ct));

        var filter = HttpUtility.ParseQueryString(Assert.Single(handler.Requests).RequestUri.Query)["$filter"];
        Assert.Equal($"singleValueExtendedProperties/Any(ep: ep/id eq '{OutlookCalendarCapability.SourceUidProperty}' and ep/value eq 'u''1')", filter);
    }

    private static OutlookCalendarCapability Create(FakeHttpHandler handler)
        => new(new GraphClient(new HttpClient(handler) { BaseAddress = new Uri(GraphClient.BaseUrl) }, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask));
}
