using DriveMigrator.Core.Calendar;

namespace DriveMigrator.Core.Transfers;

/// <summary>Choices the user makes for one transfer job in the options dialog.</summary>
public sealed record TransferOptions
{
    public static TransferOptions Default { get; } = new();

    /// <summary>What to do when a file with the same name already exists in the destination folder.</summary>
    public ConflictPolicy Conflicts { get; init; } = ConflictPolicy.Skip;

    /// <summary>
    /// Export format per native document MIME type (e.g. Google Docs → .docx). A null value means "don't copy
    /// documents of this type". Types missing from the map use their first (preferred) format.
    /// </summary>
    public IReadOnlyDictionary<string, ExportFormat?> NativeExports { get; init; } = new Dictionary<string, ExportFormat?>();

    /// <summary>Convert files to the destination's native format when it supports that (e.g. .docx → Google Docs).</summary>
    public bool ConvertToNativeFormat { get; init; }

    public CalendarImportOptions Calendar { get; init; } = CalendarImportOptions.Default;
}

public enum ConflictPolicy
{
    /// <summary>Leave the existing file alone and don't copy this one.</summary>
    Skip,

    /// <summary>Replace the existing file's content.</summary>
    Overwrite,

    /// <summary>Copy under a new name such as "report (1).pdf".</summary>
    KeepBoth,
}
