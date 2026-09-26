using DriveMigrator.Core;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Testing;

namespace DriveMigrator.Engine.Tests;

public sealed class CalendarTransferTests : IDisposable
{
    private readonly EngineFixture _f = new();

    private FakeCalendarCapability Src => _f.Source.Calendar;

    private FakeCalendarCapability Dst => _f.Destination.Calendar;

    private static CancellationToken Ct => EngineFixture.Ct;

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task DefaultCalendarMapsToDefault_OthersAreCreated()
    {
        var primary = Src.AddSpecialCalendar(ContainerRole.DefaultCalendar, "ada@gmail.com");
        Src.AddEvent(primary, Event("Standup", "u1"));
        var trips = Src.AddContainer(null, "Trips");
        Src.AddEvent(trips, Event("Lisbon", "u2"));
        var calendar = Dst.AddSpecialCalendar(ContainerRole.DefaultCalendar, "Calendar");

        await _f.RunAsync(_f.CreateJob([primary, trips], kind: CapabilityKind.Calendar));

        Assert.Equal(["Standup"], (await Dst.GetChildrenAsync(calendar, Ct).ToListAsync(Ct)).Select(e => e.Name));
        var roots = await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct);
        Assert.Equal(["Calendar", "Trips"], roots.Select(r => r.Name));
    }

    [Fact]
    public async Task AttendeesAreEmbedded_WhenDestinationWouldInviteThem()
    {
        Dst.ImportNotifiesAttendees = true;
        var calendar = Src.AddContainer(null, "Work");
        Src.AddEvent(calendar, Event("Review", "u1") with
        {
            Description = "Agenda",
            Organizer = new EventParticipant("boss@x", "Boss"),
            Attendees = [new EventParticipant("ada@x", "Ada") { Response = AttendeeResponse.Accepted }, new EventParticipant("bob@x", null) { IsOptional = true }],
        });

        await _f.RunAsync(_f.CreateJob([calendar], kind: CapabilityKind.Calendar));

        var copied = await Dst.ReadEventAsync(await SingleEventAsync(), Ct);
        Assert.Empty(copied.Attendees);
        Assert.Null(copied.Organizer);
        Assert.Equal("Agenda\n\nOrganizer: Boss <boss@x>\nAttendees:\n• Ada <ada@x> (accepted)\n• bob@x (optional)", copied.Description!.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(true, AttendeeHandling.KeepAttendees, 1)]
    [InlineData(false, AttendeeHandling.EmbedInDescription, 1)]
    public async Task AttendeesAreKept_WhenChosenOrHarmless(bool notifies, AttendeeHandling handling, int expected)
    {
        Dst.ImportNotifiesAttendees = notifies;
        var calendar = Src.AddContainer(null, "Work");
        Src.AddEvent(calendar, Event("Review", "u1") with { Attendees = [new EventParticipant("ada@x", "Ada")] });
        var options = new TransferOptions { Calendar = new CalendarImportOptions(handling) };

        await _f.RunAsync(_f.CreateJob([calendar], options: options, kind: CapabilityKind.Calendar));

        var node = await SingleEventAsync();
        Assert.Equal(expected, (await Dst.ReadEventAsync(node, Ct)).Attendees.Count);
        Assert.Equal(handling, Dst.GetImportOptions(node)!.AttendeeHandling);
    }

    [Fact]
    public async Task RerunningSkipsEventsAlreadyCopied()
    {
        var calendar = Src.AddContainer(null, "Work");
        Src.AddEvent(calendar, Event("A", "u1"));
        Src.AddEvent(calendar, Event("No uid", null));

        await _f.RunAsync(_f.CreateJob([calendar], kind: CapabilityKind.Calendar));
        await _f.RunAsync(_f.CreateJob([calendar], kind: CapabilityKind.Calendar));

        var work = (await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Single();
        Assert.Equal(3, (await Dst.GetChildrenAsync(work, Ct).ToListAsync(Ct)).Count);
        Assert.Contains(_f.Observer.Finished, f => f.Status == ItemStatus.Skipped && f.Name == "A");
    }

    [Fact]
    public async Task LooseEventsGoToTheDefaultCalendar()
    {
        var calendar = Dst.AddSpecialCalendar(ContainerRole.DefaultCalendar, "Calendar");
        var node = Src.AddEvent(Src.AddContainer(null, "Work"), Event("A", "u1"));

        await _f.RunAsync(_f.CreateJob([node], kind: CapabilityKind.Calendar));

        Assert.Single(await Dst.GetChildrenAsync(calendar, Ct).ToListAsync(Ct));
    }

    private async Task<MigrationNode> SingleEventAsync()
    {
        var calendar = (await Dst.GetChildrenAsync(null, Ct).ToListAsync(Ct)).Single();
        return (await Dst.GetChildrenAsync(calendar, Ct).ToListAsync(Ct)).Single();
    }

    private static CalendarEvent Event(string title, string? uid) => new()
    {
        Title = title,
        Start = new EventTime(new DateTime(2026, 10, 1, 9, 0, 0), "Europe/Lisbon"),
        End = new EventTime(new DateTime(2026, 10, 1, 10, 0, 0), "Europe/Lisbon"),
        ICalUid = uid,
    };
}
