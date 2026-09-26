using System.Globalization;

namespace DriveMigrator.Core.Calendar;

/// <summary>Helpers for the RFC 5545 recurrence lines in <see cref="CalendarEvent.Recurrence"/>.</summary>
public static class Recurrence
{
    private const string DateFormat = "yyyyMMdd";
    private const string DateTimeFormat = "yyyyMMdd'T'HHmmss";

    /// <summary>The RRULE parts ("FREQ" → "WEEKLY"...), or null when there is no RRULE line.</summary>
    public static Dictionary<string, string>? ParseRule(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rule = lines.FirstOrDefault(l => l.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));
        return rule?[6..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].ToUpperInvariant(), kv => kv[1], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Formats RRULE parts back into a line.</summary>
    public static string FormatRule(IEnumerable<KeyValuePair<string, string>> parts)
        => "RRULE:" + string.Join(';', parts.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>An EXDATE line excluding the occurrence that starts at <paramref name="start"/>.</summary>
    public static string ExDate(EventTime start, bool allDay)
    {
        ArgumentNullException.ThrowIfNull(start);
        return allDay
            ? $"EXDATE;VALUE=DATE:{start.DateTime.ToString(DateFormat, CultureInfo.InvariantCulture)}"
            : $"EXDATE;TZID={start.TimeZone}:{start.DateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The excluded occurrences, as wall-clock times in <paramref name="timeZone"/> (UTC values are converted;
    /// TZID values in another zone are converted too).
    /// </summary>
    public static IReadOnlyList<DateTime> ParseExDates(IEnumerable<string> lines, string timeZone)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<DateTime>();
        foreach (var line in lines.Where(l => l.StartsWith("EXDATE", StringComparison.OrdinalIgnoreCase)))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var parameters = line[..colon];
            var tzid = parameters.Split(';').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Equals("TZID", StringComparison.OrdinalIgnoreCase))?[1];
            foreach (var value in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (value.Length == DateFormat.Length && DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    result.Add(date);
                }
                else if (value.EndsWith('Z') && DateTime.TryParseExact(value[..^1], DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc))
                {
                    result.Add(EventTime.FromUtc(utc, timeZone).DateTime);
                }
                else if (DateTime.TryParseExact(value, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                {
                    result.Add(tzid is null || tzid == timeZone ? local : EventTime.FromUtc(new EventTime(local, tzid).ToUtc(), timeZone).DateTime);
                }
            }
        }

        return result;
    }

    /// <summary>Formats a UTC instant for RRULE UNTIL.</summary>
    public static string FormatUtc(DateTime utc) => utc.ToString(DateTimeFormat, CultureInfo.InvariantCulture) + "Z";

    /// <summary>Parses an RRULE UNTIL value (date, local date-time, or UTC date-time) into a date in <paramref name="timeZone"/>.</summary>
    public static DateOnly? ParseUntilDate(string? value, string timeZone)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == DateFormat.Length && DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return DateOnly.FromDateTime(date);
        }

        if (value.EndsWith('Z') && DateTime.TryParseExact(value[..^1], DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc))
        {
            return DateOnly.FromDateTime(EventTime.FromUtc(utc, timeZone).DateTime);
        }

        return DateTime.TryParseExact(value, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ? DateOnly.FromDateTime(local) : null;
    }
}
