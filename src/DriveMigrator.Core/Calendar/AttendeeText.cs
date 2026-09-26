using System.Text;

namespace DriveMigrator.Core.Calendar;

/// <summary>
/// Implements <see cref="AttendeeHandling.EmbedInDescription"/>: removes attendees (so creating the event invites
/// nobody) and lists them, with the organizer, at the end of the description.
/// </summary>
public static class AttendeeText
{
    public static CalendarEvent Embed(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var exceptions = calendarEvent.Exceptions.Select(Embed).ToList();
        if (calendarEvent.Attendees.Count == 0 && calendarEvent.Organizer is null)
        {
            return calendarEvent with { Exceptions = exceptions };
        }

        var text = new StringBuilder(calendarEvent.Description?.TrimEnd());
        if (text.Length > 0)
        {
            text.AppendLine().AppendLine();
        }

        if (calendarEvent.Organizer is { } organizer)
        {
            text.Append("Organizer: ").AppendLine(Describe(organizer));
        }

        if (calendarEvent.Attendees.Count > 0)
        {
            text.AppendLine("Attendees:");
            foreach (var attendee in calendarEvent.Attendees)
            {
                text.Append("• ").Append(Describe(attendee));
                var notes = new List<string>();
                if (attendee.Response != AttendeeResponse.None)
                {
                    notes.Add(attendee.Response.ToString().ToLowerInvariant());
                }

                if (attendee.IsOptional)
                {
                    notes.Add("optional");
                }

                if (notes.Count > 0)
                {
                    text.Append(" (").Append(string.Join(", ", notes)).Append(')');
                }

                text.AppendLine();
            }
        }

        return calendarEvent with
        {
            Description = text.ToString().TrimEnd(),
            Attendees = [],
            Organizer = null,
            Exceptions = exceptions,
        };
    }

    private static string Describe(EventParticipant participant)
        => string.IsNullOrWhiteSpace(participant.Name) || participant.Name == participant.Email
            ? participant.Email
            : $"{participant.Name} <{participant.Email}>";
}
