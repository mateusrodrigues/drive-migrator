using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
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
        HasFiles = kinds.Contains(CapabilityKind.Drive);

        CanConvert = kinds.Contains(CapabilityKind.Drive)
            && request.Destination.GetCapability(CapabilityKind.Drive) is IDriveCapability { CanConvertToNativeFormat: true };
        ConvertLabel = $"Convert Word, Excel, PowerPoint and OpenDocument files into native {request.Destination.GetCapability(CapabilityKind.Drive)?.DisplayName} documents";
    }

    public string Summary { get; }

    /// <summary>File conflict choices only matter when drive items are being copied.</summary>
    public bool HasFiles { get; }

    public bool HasMail { get; }

    public bool HasContacts { get; }

    /// <summary>The "skip what's already there" option applies to messages and contacts.</summary>
    public bool ShowDuplicateOption => HasMail || HasContacts;

    public string DuplicateOptionLabel => (HasMail, HasContacts) switch
    {
        (true, true) => "Skip messages and contacts that are already in the destination (so running a copy again adds no duplicates)",
        (true, false) => "Skip messages that are already in the destination folder (so running a copy again adds no duplicates)",
        _ => "Skip contacts that are already there, matched by email address, or by name when there is none (so running a copy again adds no duplicates)",
    };

    [ObservableProperty]
    public partial bool SkipDuplicates { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictSkip), nameof(ConflictOverwrite), nameof(ConflictKeepBoth))]
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
