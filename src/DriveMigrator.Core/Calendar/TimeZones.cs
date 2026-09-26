namespace DriveMigrator.Core.Calendar;

/// <summary>
/// Converts between IANA time zone ids (Google, and this app's neutral model) and Windows ids (Outlook).
/// Unknown or custom zones fall back to UTC.
/// </summary>
public static class TimeZones
{
    public const string Utc = "UTC";

    /// <summary>Finds a zone by IANA or Windows id; UTC when unknown.</summary>
    public static TimeZoneInfo Find(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    /// <summary>The IANA id for an IANA or Windows id, or null when it isn't a known zone (e.g. "tzone://Microsoft/Custom").</summary>
    public static string? ToIana(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana))
        {
            return iana;
        }

        return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _) || id is "UTC" or "Etc/UTC" ? id : null;
    }

    /// <summary>The Windows id for an IANA id (Outlook expects Windows ids); UTC when there is no equivalent.</summary>
    public static string ToWindows(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId) || ianaId is "UTC" or "Etc/UTC")
        {
            return Utc;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out var windows))
        {
            return windows;
        }

        // Already a Windows id?
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(ianaId, out _) ? ianaId : Utc;
    }
}
