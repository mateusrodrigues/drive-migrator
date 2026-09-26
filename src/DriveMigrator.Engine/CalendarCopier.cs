using DriveMigrator.Core;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.Engine;

/// <summary>Copies one event (with its recurrence exceptions): read, skip if already copied, handle attendees, import.</summary>
internal static class CalendarCopier
{
    public static async Task<ItemOutcome> CopyAsync(
        ICalendarCapability source,
        ICalendarCapability destination,
        MigrationNode eventNode,
        MigrationNode? targetCalendar,
        TransferOptions options,
        CancellationToken cancellationToken)
    {
        // Events always live in a calendar; loose events copied to the top level go to the default one.
        var calendar = targetCalendar
            ?? await destination.GetSpecialContainerAsync(ContainerRole.DefaultCalendar, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The destination has no default calendar; choose a destination calendar.");

        var calendarEvent = await source.ReadEventAsync(eventNode, cancellationToken).ConfigureAwait(false);
        if (options.SkipDuplicates && await destination.ContainsEventAsync(calendar, calendarEvent, cancellationToken).ConfigureAwait(false))
        {
            return ItemOutcome.Skipped("Skipped: this event was already copied to the destination calendar.");
        }

        // Only destinations that send invitations need the attendees moved out of the way.
        if (destination.ImportNotifiesAttendees && options.Calendar.AttendeeHandling == AttendeeHandling.EmbedInDescription)
        {
            calendarEvent = AttendeeText.Embed(calendarEvent);
        }

        var imported = await destination.ImportEventAsync(calendar, calendarEvent, options.Calendar, cancellationToken).ConfigureAwait(false);
        return ItemOutcome.Done(imported, 0);
    }
}
