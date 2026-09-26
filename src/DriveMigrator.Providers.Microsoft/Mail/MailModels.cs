namespace DriveMigrator.Providers.Microsoft.Mail;

// Subsets of Graph's mailFolder and message resources used here.
internal sealed record MailFolder(string Id, string DisplayName, int? ChildFolderCount, int? TotalItemCount);

internal sealed record MessageSummary(
    string Id,
    string? Subject,
    Recipient? From,
    DateTimeOffset? ReceivedDateTime,
    bool? IsRead,
    FollowupFlag? Flag,
    string? InternetMessageId);

internal sealed record Recipient(EmailAddress? EmailAddress);

internal sealed record EmailAddress(string? Name, string? Address);

internal sealed record FollowupFlag(string? FlagStatus);

internal sealed record CreatedItem(string Id);

internal sealed record UploadSessionInfo(Uri UploadUrl);
