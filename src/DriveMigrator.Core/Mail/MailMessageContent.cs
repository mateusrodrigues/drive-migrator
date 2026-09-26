namespace DriveMigrator.Core.Mail;

/// <summary>A message as raw RFC 822 MIME plus the mailbox state that is not part of the MIME. Owns and disposes <see cref="Mime"/>.</summary>
public sealed class MailMessageContent(Stream mime) : IAsyncDisposable
{
    public Stream Mime { get; } = mime;

    public bool IsRead { get; init; }

    public bool IsFlagged { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>
    /// Every folder/label the message lives in at the source. Gmail messages can carry several labels;
    /// folder-based providers report exactly one.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    public ValueTask DisposeAsync() => Mime.DisposeAsync();
}
