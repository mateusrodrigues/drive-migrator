using System.Text.Json;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Providers.Microsoft.Calendar;

namespace DriveMigrator.Providers.Tests;

public class GraphRecurrenceTests
{
    // Monday 5 January 2026, 09:00 in Lisbon.
    private static readonly EventTime Start = new(new DateTime(2026, 1, 5, 9, 0, 0), "Europe/Lisbon");

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("RRULE:FREQ=DAILY;INTERVAL=2;COUNT=10", """{"interval":2,"type":"daily"}""", """{"type":"numbered","numberOfOccurrences":10}""")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=MO,WE", """{"interval":1,"type":"weekly","daysOfWeek":["monday","wednesday"]}""", """{"type":"noEnd"}""")]
    [InlineData("RRULE:FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", """{"interval":1,"type":"weekly","daysOfWeek":["monday","tuesday","wednesday","thursday","friday"]}""", """{"type":"noEnd"}""")]
    [InlineData("RRULE:FREQ=WEEKLY", """{"interval":1,"type":"weekly","daysOfWeek":["monday"]}""", """{"type":"noEnd"}""")]
    [InlineData("RRULE:FREQ=MONTHLY;BYMONTHDAY=15;UNTIL=20261231T235959Z", """{"interval":1,"type":"absoluteMonthly","dayOfMonth":15}""", """{"type":"endDate","endDate":"2026-12-31"}""")]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=2TU", """{"interval":1,"type":"relativeMonthly","index":"second","daysOfWeek":["tuesday"]}""", """{"type":"noEnd"}""")]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1", """{"interval":1,"type":"relativeMonthly","index":"last","daysOfWeek":["monday","tuesday","wednesday","thursday","friday"]}""", """{"type":"noEnd"}""")]
    [InlineData("RRULE:FREQ=YEARLY", """{"interval":1,"type":"absoluteYearly","month":1,"dayOfMonth":5}""", """{"type":"noEnd"}""")]
    [InlineData("RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=4TH", """{"interval":1,"type":"relativeYearly","month":11,"index":"fourth","daysOfWeek":["thursday"]}""", """{"type":"noEnd"}""")]
    public void RuleToGraph(string rule, string expectedPattern, string expectedRange)
    {
        var graph = GraphRecurrence.ToGraph(Recurrence.ParseRule([rule])!, Start, allDay: false);

        AssertJsonSubset(expectedPattern, graph["pattern"]);
        AssertJsonSubset(expectedRange, graph["range"]);
        AssertJsonSubset("""{"startDate":"2026-01-05","recurrenceTimeZone":"GMT Standard Time"}""", graph["range"]);
    }

    [Theory]
    [InlineData("RRULE:FREQ=HOURLY")]
    [InlineData("RRULE:FREQ=MONTHLY;BYMONTHDAY=-1")]
    [InlineData("RRULE:FREQ=MONTHLY;BYMONTHDAY=1,15")]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=1MO,3MO")]
    [InlineData("RRULE:FREQ=YEARLY;BYWEEKNO=20")]
    public void UnsupportedRules_FailClearly(string rule)
    {
        var ex = Assert.Throws<NotSupportedException>(() => GraphRecurrence.ToGraph(Recurrence.ParseRule([rule])!, Start, allDay: false));
        Assert.StartsWith("Outlook can't represent this event's repeat pattern", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RRULE:FREQ=DAILY;INTERVAL=2;COUNT=10")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=MO,WE;WKST=SU")]
    [InlineData("RRULE:FREQ=MONTHLY;BYMONTHDAY=15")]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=2TU")]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=MO,FR;BYSETPOS=-1")]
    [InlineData("RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=4TH")]
    [InlineData("RRULE:FREQ=YEARLY;BYMONTH=1;BYMONTHDAY=5")]
    public void RoundTripsThroughGraph(string rule)
    {
        var graph = GraphRecurrence.ToGraph(Recurrence.ParseRule([rule])!, Start, allDay: false);
        var recurrence = JsonSerializer.Deserialize<PatternedRecurrence>(JsonSerializer.Serialize(graph), Web)!;

        Assert.Equal(rule, GraphRecurrence.ToRule(recurrence, Start, allDay: false));
    }

    [Fact]
    public void EndDate_BecomesUntilAtEndOfDay()
    {
        var recurrence = new PatternedRecurrence(
            new RecurrencePattern("daily", 1, null, null, null, null, null),
            new RecurrenceRange("endDate", "2026-01-05", "2026-07-31", null, null));

        Assert.Equal("RRULE:FREQ=DAILY;UNTIL=20260731T225959Z", GraphRecurrence.ToRule(recurrence, Start, allDay: false));
        Assert.Equal("RRULE:FREQ=DAILY;UNTIL=20260731", GraphRecurrence.ToRule(recurrence, Start, allDay: true));
    }

    private static void AssertJsonSubset(string expected, object actual)
    {
        using var expectedJson = JsonDocument.Parse(expected);
        using var actualJson = JsonDocument.Parse(JsonSerializer.Serialize(actual));
        foreach (var property in expectedJson.RootElement.EnumerateObject())
        {
            Assert.True(actualJson.RootElement.TryGetProperty(property.Name, out var value), $"missing {property.Name} in {actualJson.RootElement}");
            Assert.Equal(property.Value.GetRawText(), value.GetRawText());
        }
    }
}
