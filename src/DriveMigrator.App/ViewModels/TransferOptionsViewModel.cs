using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Core.Drive;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.App.ViewModels;

/// <summary>The choices shown before a transfer starts. Only relevant sections are visible.</summary>
public sealed partial class TransferOptionsViewModel : ViewModelBase
{
    public TransferOptionsViewModel(string summary, TransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Summary = summary;

        var kinds = request.Items.Select(i => i.Kind).ToHashSet();
        if (kinds.Contains(CapabilityKind.Drive)
            && request.Source.GetCapability(CapabilityKind.Drive) is IDriveCapability sourceDrive)
        {
            foreach (var type in sourceDrive.NativeDocumentTypes)
            {
                NativeDocuments.Add(new NativeDocumentChoiceViewModel(type));
            }
        }

        HasMail = kinds.Contains(CapabilityKind.Mail);
        HasContacts = kinds.Contains(CapabilityKind.Contacts);
        HasCalendar = kinds.Contains(CapabilityKind.Calendar);
        AskAboutAttendees = HasCalendar
            && request.Destination.GetCapability(CapabilityKind.Calendar) is ICalendarCapability { ImportNotifiesAttendees: true };
        HasFiles = kinds.Contains(CapabilityKind.Drive);

        CanConvert = kinds.Contains(CapabilityKind.Drive)
            && request.Destination.GetCapability(CapabilityKind.Drive) is IDriveCapability { CanConvertToNativeFormat: true };
        ConvertLabel = $"Convert Word, Excel, PowerPoint and OpenDocument files into native {request.Destination.GetCapability(CapabilityKind.Drive)?.DisplayName} documents";
    }

    public string Summary { get; }

    /// <summary>The summary's first line: "From … to …".</summary>
    public string SummaryTitle => SummaryLines[0];

    /// <summary>The rest of the summary: one "• service: counts → destination" line per service.</summary>
    public string SummaryDetails => string.Join(Environment.NewLine, SummaryLines.Skip(1));

    private string[] SummaryLines => Summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } lines ? lines : [string.Empty];

    /// <summary>File conflict choices only matter when drive items are being copied.</summary>
    public bool HasFiles { get; }

    public bool HasMail { get; }

    public bool HasContacts { get; }

    public bool HasCalendar { get; }

    /// <summary>The destination would send invitations to attendees (Outlook), so the user decides per job.</summary>
    public bool AskAboutAttendees { get; }

    public bool AttendeesKeptSilently => HasCalendar && !AskAboutAttendees;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepAttendees))]
    public partial bool EmbedAttendees { get; set; } = true;

    public bool KeepAttendees
    {
        get => !EmbedAttendees;
        set => EmbedAttendees = !value;
    }

    /// <summary>The "skip what's already there" option applies to messages, contacts and events.</summary>
    public bool ShowDuplicateOption => HasMail || HasContacts || HasCalendar;

    public string DuplicateOptionLabel
    {
        get
        {
            List<string> kinds = [];
            if (HasMail)
            {
                kinds.Add("messages");
            }

            if (HasContacts)
            {
                kinds.Add("contacts");
            }

            if (HasCalendar)
            {
                kinds.Add("events");
            }

            var list = kinds.Count == 1 ? kinds[0] : $"{string.Join(", ", kinds[..^1])} and {kinds[^1]}";
            var contactsNote = HasContacts ? " Contacts match by email address, or by name when there is none." : string.Empty;
            return $"Skip {list} that are already in the destination, so running a copy again adds no duplicates.{contactsNote}";
        }
    }

    [ObservableProperty]
    public partial bool SkipDuplicates { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictSkip), nameof(ConflictOverwrite), nameof(ConflictKeepBoth), nameof(CanCompareExisting))]
    public partial ConflictPolicy Conflicts { get; set; } = ConflictPolicy.Skip;

    // One property per radio button; checking one selects its policy.
    public bool ConflictSkip
    {
        get => Conflicts == ConflictPolicy.Skip;
        set => Select(value, ConflictPolicy.Skip);
    }

    public bool ConflictOverwrite
    {
        get => Conflicts == ConflictPolicy.Overwrite;
        set => Select(value, ConflictPolicy.Overwrite);
    }

    public bool ConflictKeepBoth
    {
        get => Conflicts == ConflictPolicy.KeepBoth;
        set => Select(value, ConflictPolicy.KeepBoth);
    }

    [ObservableProperty]
    public partial bool VerifyHashes { get; set; } = true;

    /// <summary>Existing files are only compared when they are left alone; the other policies replace or keep both anyway.</summary>
    public bool CanCompareExisting => Conflicts == ConflictPolicy.Skip;

    [ObservableProperty]
    public partial bool CompareExisting { get; set; }

    public ObservableCollection<NativeDocumentChoiceViewModel> NativeDocuments { get; } = [];

    public bool HasNativeDocuments => NativeDocuments.Count > 0;

    public bool CanConvert { get; }

    public string ConvertLabel { get; }

    [ObservableProperty]
    public partial bool ConvertToNativeFormat { get; set; }

    public TransferOptions ToOptions() => new()
    {
        Conflicts = Conflicts,
        NativeExports = NativeDocuments.ToDictionary(d => d.Type.MimeType, d => d.Selected.Format),
        ConvertToNativeFormat = CanConvert && ConvertToNativeFormat,
        SkipDuplicates = SkipDuplicates,
        VerifyHashes = HasFiles && VerifyHashes,
        CompareExisting = HasFiles && CanCompareExisting && CompareExisting,
        Calendar = new CalendarImportOptions(EmbedAttendees ? AttendeeHandling.EmbedInDescription : AttendeeHandling.KeepAttendees),
    };

    private void Select(bool selected, ConflictPolicy policy)
    {
        if (selected)
        {
            Conflicts = policy;
        }
    }
}

/// <summary>"Google Docs → [Word document ▾]".</summary>
public sealed partial class NativeDocumentChoiceViewModel : ViewModelBase
{
    public NativeDocumentChoiceViewModel(NativeDocumentType type)
    {
        Type = type;
        Choices = [.. type.ExportFormats.Select(f => new ExportChoice(f, $"{f.DisplayName} ({f.FileExtension})")), new ExportChoice(null, "Don't copy")];
        Selected = Choices[0];
    }

    public NativeDocumentType Type { get; }

    public string Label => Type.DisplayName;

    public IReadOnlyList<ExportChoice> Choices { get; }

    [ObservableProperty]
    public partial ExportChoice Selected { get; set; }
}

public sealed record ExportChoice(ExportFormat? Format, string Label);
