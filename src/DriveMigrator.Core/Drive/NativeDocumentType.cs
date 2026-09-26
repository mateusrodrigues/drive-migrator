namespace DriveMigrator.Core.Drive;

/// <summary>A provider-native document type such as "Google Docs", and the formats it can be exported to (preferred first).</summary>
public sealed record NativeDocumentType(string MimeType, string DisplayName, IReadOnlyList<ExportFormat> ExportFormats);
