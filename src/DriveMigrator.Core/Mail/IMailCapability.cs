namespace DriveMigrator.Core.Mail;

public interface IMailCapability : ICapability
{
    /// <summary>Whether <paramref name="folder"/> already holds a message with this Message-ID header value.</summary>
    Task<bool> ContainsMessageAsync(MigrationNode folder, string internetMessageId, CancellationToken cancellationToken = default);

    Task<MailMessageContent> ReadMessageAsync(MigrationNode message, CancellationToken cancellationToken = default);

    /// <summary>Imports a message into <paramref name="folder"/> as a received (non-draft) message without sending it.</summary>
    Task<MigrationNode> ImportMessageAsync(MigrationNode folder, MailMessageContent message, CancellationToken cancellationToken = default);
}
