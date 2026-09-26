using System.Globalization;
using DriveMigrator.Core.Calendar;

namespace DriveMigrator.Providers.Microsoft.Calendar;

/// <summary>
/// Converts between RFC 5545 RRULEs (the neutral model, as Google uses them) and Graph's patternedRecurrence.
/// Rules Outlook can't express (hourly repeats, "last day of the month", BYWEEKNO...) are rejected with a clear
/// message rather than approximated.
/// </summary>
internal static class GraphRecurrence
{
    private static readonly (string Rfc, string Graph)[] Days =
    [
        ("MO", "monday"), ("TU", "tuesday"), ("WE", "wednesday"), ("TH", "thursday"), ("FR", "friday"), ("SA", "saturday"), ("SU", "sunday"),
    ];

    private static readonly (int Position, string Index)[] Indexes = [(1, "first"), (2, "second"), (3, "third"), (4, "fourth"), (-1, "last")];

    private static readonly HashSet<string> SupportedParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "FREQ", "INTERVAL", "COUNT", "UNTIL", "BYDAY", "BYMONTHDAY", "BYMONTH", "BYSETPOS", "WKST",
    };

    public static Dictionary<string, object> ToGraph(IReadOnlyDictionary<string, string> rule, EventTime start, bool allDay)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(start);
        if (rule.Keys.FirstOrDefault(k => !SupportedParts.Contains(k)) is { } unsupported)
        {
            throw Unsupported($"{unsupported} is not supported");
        }

        var interval = rule.TryGetValue("INTERVAL", out var i) ? int.Parse(i, CultureInfo.InvariantCulture) : 1;
        var pattern = new Dictionary<string, object> { ["interval"] = interval };
        var byDay = rule.TryGetValue("BYDAY", out var d) ? d.Split(',') : [];
        var setPos = rule.TryGetValue("BYSETPOS", out var p) ? int.Parse(p, CultureInfo.InvariantCulture) : (int?)null;
        var monthDay = rule.TryGetValue("BYMONTHDAY", out var md) ? ParseSingle(md, "BYMONTHDAY") : (int?)null;
        var month = rule.TryGetValue("BYMONTH", out var m) ? ParseSingle(m, "BYMONTH") : (int?)null;
        if (monthDay is < 1)
        {
            throw Unsupported("counting days from the end of the month is not supported");
        }

        switch (rule.GetValueOrDefault("FREQ")?.ToUpperInvariant())
        {
            case "DAILY" when byDay.Length == 0:
                pattern["type"] = "daily";
                break;

            // "Every weekday" is written as a daily rule with BYDAY; Outlook calls it weekly.
            case "DAILY" or "WEEKLY":
                pattern["type"] = "weekly";
                pattern["daysOfWeek"] = byDay.Length == 0 ? [GraphDay(start.DateTime.DayOfWeek)] : byDay.Select(PlainDay).ToList();
                break;

            case "MONTHLY" when byDay.Length > 0:
                pattern["type"] = "relativeMonthly";
                SetRelative(pattern, byDay, setPos);
                break;

            case "MONTHLY":
                pattern["type"] = "absoluteMonthly";
                pattern["dayOfMonth"] = monthDay ?? start.DateTime.Day;
                break;

            case "YEARLY" when byDay.Length > 0:
                pattern["type"] = "relativeYearly";
                pattern["month"] = month ?? start.DateTime.Month;
                SetRelative(pattern, byDay, setPos);
                break;

            case "YEARLY":
                pattern["type"] = "absoluteYearly";
                pattern["month"] = month ?? start.DateTime.Month;
                pattern["dayOfMonth"] = monthDay ?? start.DateTime.Day;
                break;

            case var frequency:
                throw Unsupported($"repeating {frequency?.ToLowerInvariant() ?? "without a frequency"} is not supported");
        }

        if (rule.TryGetValue("WKST", out var weekStart))
        {
            pattern["firstDayOfWeek"] = PlainDay(weekStart);
        }

        var range = new Dictionary<string, object>
        {
            ["startDate"] = start.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["recurrenceTimeZone"] = TimeZones.ToWindows(start.TimeZone),
        };
        if (rule.TryGetValue("COUNT", out var count))
        {
            range["type"] = "numbered";
            range["numberOfOccurrences"] = int.Parse(count, CultureInfo.InvariantCulture);
        }
        else if (Recurrence.ParseUntilDate(rule.GetValueOrDefault("UNTIL"), start.TimeZone) is { } until)
        {
            range["type"] = "endDate";
            range["endDate"] = until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else
        {
            range["type"] = "noEnd";
        }

        return new Dictionary<string, object> { ["pattern"] = pattern, ["range"] = range };
    }

    /// <summary>The RRULE line for a Graph recurrence of an event starting at <paramref name="start"/>.</summary>
    public static string ToRule(PatternedRecurrence recurrence, EventTime start, bool allDay)
    {
        ArgumentNullException.ThrowIfNull(recurrence);
        ArgumentNullException.ThrowIfNull(start);
        var pattern = recurrence.Pattern;
        var parts = new List<KeyValuePair<string, string>>();
        void Add(string key, object value) => parts.Add(new(key, Convert.ToString(value, CultureInfo.InvariantCulture)!));

        var days = (pattern.DaysOfWeek ?? []).Select(RfcDay).ToList();
        var index = Indexes.FirstOrDefault(x => string.Equals(x.Index, pattern.Index, StringComparison.OrdinalIgnoreCase)).Position;
        switch (pattern.Type.ToUpperInvariant())
        {
            case "DAILY":
                Add("FREQ", "DAILY");
                break;
            case "WEEKLY":
                Add("FREQ", "WEEKLY");
                Add("BYDAY", string.Join(',', days));
                break;
            case "ABSOLUTEMONTHLY":
                Add("FREQ", "MONTHLY");
                Add("BYMONTHDAY", pattern.DayOfMonth ?? start.DateTime.Day);
                break;
            case "RELATIVEMONTHLY":
                Add("FREQ", "MONTHLY");
                AddRelative(days, index == 0 ? 1 : index);
                break;
            case "ABSOLUTEYEARLY":
                Add("FREQ", "YEARLY");
                Add("BYMONTH", pattern.Month ?? start.DateTime.Month);
                Add("BYMONTHDAY", pattern.DayOfMonth ?? start.DateTime.Day);
                break;
            case "RELATIVEYEARLY":
                Add("FREQ", "YEARLY");
                Add("BYMONTH", pattern.Month ?? start.DateTime.Month);
                AddRelative(days, index == 0 ? 1 : index);
                break;
            default:
                throw new NotSupportedException($"Unknown Outlook recurrence type '{pattern.Type}'.");
        }

        if (pattern.Interval > 1)
        {
            Add("INTERVAL", pattern.Interval);
        }

        if (pattern.Type.Equals("weekly", StringComparison.OrdinalIgnoreCase) && pattern.FirstDayOfWeek is { } firstDay)
        {
            Add("WKST", RfcDay(firstDay));
        }

        var range = recurrence.Range;
        if (range.Type.Equals("numbered", StringComparison.OrdinalIgnoreCase) && range.NumberOfOccurrences is > 0)
        {
            Add("COUNT", range.NumberOfOccurrences.Value);
        }
        else if (range.Type.Equals("endDate", StringComparison.OrdinalIgnoreCase)
            && DateOnly.TryParseExact(range.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
        {
            // UNTIL is inclusive: all-day events use the date, timed events the end of that day in UTC.
            Add("UNTIL", allDay
                ? endDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : Recurrence.FormatUtc(new EventTime(endDate.ToDateTime(new TimeOnly(23, 59, 59)), start.TimeZone).ToUtc()));
        }

        return Recurrence.FormatRule(parts);

        void AddRelative(List<string> byDay, int position)
        {
            if (byDay.Count == 1)
            {
                Add("BYDAY", $"{position}{byDay[0]}");
            }
            else
            {
                Add("BYDAY", string.Join(',', byDay));
                Add("BYSETPOS", position);
            }
        }
    }

    private static void SetRelative(Dictionary<string, object> pattern, string[] byDay, int? setPos)
    {
        // "2TU" (ordinal on the day) or "TU;BYSETPOS=2"; Outlook needs the same position for every day.
        var positions = new HashSet<int>();
        var days = new List<string>();
        foreach (var day in byDay)
        {
            var code = day[^2..];
            var ordinal = day[..^2];
            if (ordinal.Length > 0)
            {
                positions.Add(int.Parse(ordinal, CultureInfo.InvariantCulture));
            }

            days.Add(PlainDay(code));
        }

        if (setPos is { } p)
        {
            positions.Add(p);
        }

        if (positions.Count != 1 || !Indexes.Any(x => x.Position == positions.Single()))
        {
            throw Unsupported("this kind of monthly or yearly repeat is not supported");
        }

        pattern["index"] = Indexes.Single(x => x.Position == positions.Single()).Index;
        pattern["daysOfWeek"] = days;
    }

    private static int ParseSingle(string value, string part)
        => value.Contains(',', StringComparison.Ordinal)
            ? throw Unsupported($"several {part} values are not supported")
            : int.Parse(value, CultureInfo.InvariantCulture);

    private static string PlainDay(string rfc)
    {
        var code = rfc[^2..].ToUpperInvariant();
        return Days.FirstOrDefault(d => d.Rfc == code).Graph ?? throw Unsupported($"unknown day '{rfc}'");
    }

    private static string RfcDay(string graph)
        => Days.FirstOrDefault(d => d.Graph.Equals(graph, StringComparison.OrdinalIgnoreCase)).Rfc
            ?? throw new NotSupportedException($"Unknown day '{graph}'.");

    private static string GraphDay(DayOfWeek day) => Days[((int)day + 6) % 7].Graph;

    private static NotSupportedException Unsupported(string reason)
        => new($"Outlook can't represent this event's repeat pattern ({reason}).");
}
