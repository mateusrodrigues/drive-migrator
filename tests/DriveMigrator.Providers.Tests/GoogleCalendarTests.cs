using System.Text.Json;
using System.Web;
using DriveMigrator.Core;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Providers.Google.Calendar;
using Google.Apis.Calendar.v3;
using Google.Apis.Http;
using Google.Apis.Services;

namespace DriveMigrator.Providers.Tests;

public class GoogleCalendarTests
{
    private const string Api = "https://www.googleapis.com/calendar/v3/";

    private const string CalendarList = """
        { "items": [
            { "id": "holidays@group", "summary": "Holidays", "timeZone": "Europe/Lisbon" },
            { "id": "ada@gmail.com", "summary": "ada@gmail.com", "primary": true, "timeZone": "Europe/Lisbon" } ] }
        """;

    private const string Events = """
        { "items": [
            { "id": "s1", "status": "confirmed", "summary": "Standup", "start": { "dateTime": "2026-01-05T09:00:00Z" }, "recurrence": [ "RRULE:FREQ=WEEKLY;BYDAY=MO" ] },
            { "id": "s1_20260112T090000Z", "status": "cancelled", "recurringEventId": "s1", "originalStartTime": { "dateTime": "2026-01-12T09:00:00Z", "timeZone": "Europe/Lisbon" } },
            { "id": "s1_20260119T090000Z", "status": "confirmed", "recurringEventId": "s1", "summary": "Standup (moved)", "originalStartTime": { "dateTime": "2026-01-19T09:00:00Z" } },
            { "id": "d1", "status": "cancelled" },
            { "id": "w1", "status": "confirmed", "eventType": "workingLocation", "summary": "Home" },
            { "id": "h1", "status": "confirmed", "summary": "Holiday", "start": { "date": "2026-12-25" } } ] }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Root_PrimaryFirstWithDefaultRole()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(CalendarList));

        var nodes = await Create(handler).GetChildrenAsync(null, Ct).ToListAsync(Ct);

        Assert.Equal([("ada@gmail.com", ContainerRole.DefaultCalendar), ("holidays@group", (ContainerRole?)null)], nodes.Select(n => (n.Id, n.Role)));
    }

    [Fact]
    public async Task Calendar_ListsSeriesAndSingles_NotExceptionsOrWorkingLocations()
    {
        var handler = new FakeHttpHandler(r => r.Url.Contains("calendarList", StringComparison.Ordinal) ? FakeHttpHandler.Json(CalendarList) : FakeHttpHandler.Json(Events));

        var nodes = await Create(handler).GetChildrenAsync(new MigrationNode("ada@gmail.com", "ada", NodeKind.Calendar), Ct).ToListAsync(Ct);

        Assert.Equal([("ada@gmail.com|s1", "Standup", "repeats from 2026-01-05 09:00"), ("ada@gmail.com|h1", "Holiday", "2026-12-25")], nodes.Select(n => (n.Id, n.Name, n.Detail!)));
        var list = HttpUtility.ParseQueryString(handler.Requests.Single(r => r.Url.Contains("/events?", StringComparison.Ordinal)).RequestUri.Query);
        Assert.Equal(("false", "true"), (list["singleEvents"], list["showDeleted"]));
    }

    [Fact]
    public async Task Read_SeriesGetsExdatesAndModifiedOccurrences()
    {
        var handler = new FakeHttpHandler(r => r.Url switch
        {
            var u when u.Contains("calendarList", StringComparison.Ordinal) => FakeHttpHandler.Json(CalendarList),
            var u when u.Contains("/events?", StringComparison.Ordinal) => FakeHttpHandler.Json(Events),
            var u when u.EndsWith("/events/s1", StringComparison.Ordinal) => FakeHttpHandler.Json("""
                { "id": "s1", "summary": "Standup", "iCalUID": "s1@google.com", "recurrence": [ "RRULE:FREQ=WEEKLY;BYDAY=MO" ],
                  "start": { "dateTime": "2026-01-05T09:00:00Z", "timeZone": "Europe/Lisbon" }, "end": { "dateTime": "2026-01-05T09:15:00Z", "timeZone": "Europe/Lisbon" },
                  "attendees": [ { "email": "ada@x", "responseStatus": "accepted" }, { "email": "room@resource", "resource": true } ],
                  "reminders": { "useDefault": false, "overrides": [ { "method": "popup", "minutes": 10 } ] }, "transparency": "transparent" }
                """),
            var u => FakeHttpHandler.Json("""
                { "id": "s1_20260119T090000Z", "summary": "Standup (moved)", "recurringEventId": "s1",
                  "start": { "dateTime": "2026-01-19T11:00:00Z" }, "end": { "dateTime": "2026-01-19T11:15:00Z" } }
                """),
        });

        var series = await Create(handler).ReadEventAsync(new MigrationNode("ada@gmail.com|s1", "Standup", NodeKind.CalendarEvent), Ct);

        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;TZID=Europe/Lisbon:20260112T090000", "EXDATE;TZID=Europe/Lisbon:20260119T090000"],
            series.Recurrence);
        var moved = Assert.Single(series.Exceptions);
        Assert.Equal(("Standup (moved)", new DateTime(2026, 1, 19, 11, 0, 0)), (moved.Title, moved.Start.DateTime));
        Assert.Equal(["ada@x"], series.Attendees.Select(a => a.Email));
        Assert.Equal((EventAvailability.Free, 10), (series.ShowAs, series.ReminderMinutesBefore[0]));
    }

    [Fact]
    public async Task Import_UsesImportWithUidAndWallClockTime()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{ "id": "new1" }"""));
        var standup = new CalendarEvent
        {
            Title = "Standup",
            Start = new EventTime(new DateTime(2026, 1, 5, 9, 0, 0), "Europe/Lisbon"),
            End = new EventTime(new DateTime(2026, 1, 5, 9, 15, 0), "Europe/Lisbon"),
            Recurrence = ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;TZID=Europe/Lisbon:20260112T090000"],
            Attendees = [new EventParticipant("ada@x", "Ada") { Response = AttendeeResponse.Tentative }],
            ICalUid = "AAA-outlook-uid",
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

        var node = await Create(handler).ImportEventAsync(new MigrationNode("ada@gmail.com", "ada", NodeKind.Calendar), standup, CalendarImportOptions.Default, Ct);

        Assert.Equal("ada@gmail.com|new1", node.Id);
        Assert.All(handler.Requests, r => Assert.Equal(Api + "calendars/ada%40gmail.com/events/import", r.Url));
        using var master = JsonDocument.Parse(handler.Requests[0].Body);
        var root = master.RootElement;
        Assert.Equal("AAA-outlook-uid", root.GetProperty("iCalUID").GetString());
        Assert.Equal(("2026-01-05T09:00:00", "Europe/Lisbon"), (root.GetProperty("start").GetProperty("dateTime").GetString(), root.GetProperty("start").GetProperty("timeZone").GetString()));
        Assert.Equal(2, root.GetProperty("recurrence").GetArrayLength());
        Assert.Equal("tentative", root.GetProperty("attendees")[0].GetProperty("responseStatus").GetString());
        Assert.True(root.GetProperty("reminders").GetProperty("useDefault").GetBoolean());

        using var exception = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("AAA-outlook-uid-20260112T090000", exception.RootElement.GetProperty("iCalUID").GetString());
        Assert.False(exception.RootElement.TryGetProperty("recurrence", out _));
    }

    [Fact]
    public async Task Contains_LooksUpByUid()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(string.Empty));

        Assert.False(await Create(handler).ContainsEventAsync(
            new MigrationNode("ada@gmail.com", "ada", NodeKind.Calendar),
            new CalendarEvent { Title = "x", Start = new EventTime(DateTime.Today, "UTC"), End = new EventTime(DateTime.Today, "UTC"), ICalUid = "u1" },
            Ct));

        Assert.Equal("u1", HttpUtility.ParseQueryString(Assert.Single(handler.Requests).RequestUri.Query)["iCalUID"]);
    }

    private static GoogleCalendarCapability Create(FakeHttpHandler handler)
        => new(new CalendarService(new BaseClientService.Initializer
        {
            HttpClientFactory = new FakeClientFactory(handler),
            ApplicationName = "tests",
            GZipEnabled = false,
        }));

    private sealed class FakeClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }
}
