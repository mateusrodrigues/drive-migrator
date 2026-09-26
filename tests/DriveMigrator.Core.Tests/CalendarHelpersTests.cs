using DriveMigrator.Core.Calendar;

namespace DriveMigrator.Core.Tests;

public class CalendarHelpersTests
{
    [Theory]
    [InlineData("W. Europe Standard Time", "Europe/Berlin")]
    [InlineData("Europe/Lisbon", "Europe/Lisbon")]
    [InlineData("tzone://Microsoft/Custom", null)]
    [InlineData(null, null)]
    public void ToIana(string? id, string? expected) => Assert.Equal(expected, TimeZones.ToIana(id));

    [Theory]
    [InlineData("Europe/Berlin", "W. Europe Standard Time")]
    [InlineData("America/New_York", "Eastern Standard Time")]
    [InlineData("Not/AZone", "UTC")]
    [InlineData(null, "UTC")]
    public void ToWindows(string? id, string expected) => Assert.Equal(expected, TimeZones.ToWindows(id));

    [Fact]
    public void EventTime_ConvertsAcrossDaylightSaving()
    {
        var summer = new EventTime(new DateTime(2026, 7, 1, 9, 0, 0), "Europe/Lisbon");
        var winter = new EventTime(new DateTime(2026, 1, 5, 9, 0, 0), "Europe/Lisbon");

        Assert.Equal(new DateTime(2026, 7, 1, 8, 0, 0), summer.ToUtc());
        Assert.Equal(new DateTime(2026, 1, 5, 9, 0, 0), winter.ToUtc());
        Assert.Equal(summer, EventTime.FromUtc(new DateTime(2026, 7, 1, 8, 0, 0), "Europe/Lisbon"));
    }

    [Fact]
    public void Recurrence_ParsesRuleAndExDates()
    {
        string[] lines =
        [
            "RRULE:FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20261231T235959Z",
            "EXDATE;TZID=Europe/Lisbon:20260105T090000,20260107T090000",
            "EXDATE:20260112T090000Z",
            "EXDATE;TZID=America/New_York:20260114T040000",
        ];

        var rule = Recurrence.ParseRule(lines)!;
        var exdates = Recurrence.ParseExDates(lines, "Europe/Lisbon");

        Assert.Equal(("WEEKLY", "MO,WE"), (rule["FREQ"], rule["BYDAY"]));
        Assert.Equal(
            [new DateTime(2026, 1, 5, 9, 0, 0), new DateTime(2026, 1, 7, 9, 0, 0), new DateTime(2026, 1, 12, 9, 0, 0), new DateTime(2026, 1, 14, 9, 0, 0)],
            exdates);
        Assert.Equal(new DateOnly(2026, 12, 31), Recurrence.ParseUntilDate(rule["UNTIL"], "Europe/Lisbon"));
    }

    [Fact]
    public void Recurrence_FormatsExDates()
    {
        Assert.Equal("EXDATE;TZID=Europe/Lisbon:20260105T090000", Recurrence.ExDate(new EventTime(new DateTime(2026, 1, 5, 9, 0, 0), "Europe/Lisbon"), allDay: false));
        Assert.Equal("EXDATE;VALUE=DATE:20260105", Recurrence.ExDate(new EventTime(new DateTime(2026, 1, 5), "Europe/Lisbon"), allDay: true));
        Assert.Equal(new DateTime(2026, 1, 5), Assert.Single(Recurrence.ParseExDates(["EXDATE;VALUE=DATE:20260105"], "UTC")));
    }

    [Fact]
    public void AttendeeText_LeavesEventsWithoutPeopleAlone()
    {
        var plain = new CalendarEvent { Title = "x", Description = "d", Start = new EventTime(DateTime.Today, "UTC"), End = new EventTime(DateTime.Today, "UTC") };

        Assert.Equal("d", AttendeeText.Embed(plain).Description);
    }
}
