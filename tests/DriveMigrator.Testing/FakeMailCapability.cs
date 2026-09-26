using DriveMigrator.Core;
using DriveMigrator.Core.Mail;

namespace DriveMigrator.Testing;

public sealed class FakeMailCapability() : InMemoryCapability(CapabilityKind.Mail, NodeKind.MailFolder, supportsNestedContainers: true), IMailCapability
{
    public MigrationNode AddMessage(
        MigrationNode folder,
        string subject,
        byte[] mime,
        bool isRead = false,
        bool isFlagged = false,
        DateTimeOffset? receivedAt = null,
        string? messageId = null)
        => Add(folder, MessageNode(subject, mime.Length, receivedAt), new StoredMessage(mime, isRead, isFlagged, receivedAt, messageId));

    /// <summary>Adds a top-level well-known folder such as the Inbox.</summary>
    public MigrationNode AddSpecialFolder(MailFolderRole role, string name)
        => Add(null, new MigrationNode(NewId(), name, NodeKind.MailFolder) { Role = role }, payload: null);

    public bool IsRead(MigrationNode message) => GetPayload<StoredMessage>(message).IsRead;

    public bool IsFlagged(MigrationNode message) => GetPayload<StoredMessage>(message).IsFlagged;

    public string? GetMessageId(MigrationNode message) => GetPayload<StoredMessage>(message).MessageId;

    public async Task<MigrationNode?> GetSpecialFolderAsync(MailFolderRole role, CancellationToken cancellationToken = default)
    {
        await foreach (var node in GetChildrenAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (node.Role == role)
            {
                return node;
            }
        }

        return null;
    }

    public async Task<bool> ContainsMessageAsync(MigrationNode folder, string internetMessageId, CancellationToken cancellationToken = default)
    {
        await foreach (var node in GetChildrenAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            if (!node.IsContainer && GetPayload<StoredMessage>(node).MessageId == internetMessageId)
            {
                return true;
            }
        }

        return false;
    }

    public byte[] GetMime(MigrationNode message) => GetPayload<StoredMessage>(message).Mime;

    public Task<MailMessageContent> ReadMessageAsync(MigrationNode message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = GetPayload<StoredMessage>(message);
        var folder = GetParent(message);

        return Task.FromResult(new MailMessageContent(new MemoryStream(stored.Mime, writable: false))
        {
            IsRead = stored.IsRead,
            IsFlagged = stored.IsFlagged,
            ReceivedAt = stored.ReceivedAt,
            InternetMessageId = stored.MessageId,
            Labels = folder is null ? [] : [folder.Name],
        });
    }

    public async Task<MigrationNode> ImportMessageAsync(MigrationNode folder, MailMessageContent message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var buffer = new MemoryStream();
        await message.Mime.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var mime = buffer.ToArray();

        // The fake does not parse MIME; imported messages are named after their size.
        return Add(folder, MessageNode($"Imported message ({mime.Length} bytes)", mime.Length, message.ReceivedAt),
            new StoredMessage(mime, message.IsRead, message.IsFlagged, message.ReceivedAt, message.InternetMessageId));
    }

    private static MigrationNode MessageNode(string subject, long size, DateTimeOffset? receivedAt)
        => new(NewId(), subject, NodeKind.MailMessage) { MimeType = "message/rfc822", Size = size, ModifiedAt = receivedAt };

    private sealed record StoredMessage(byte[] Mime, bool IsRead, bool IsFlagged, DateTimeOffset? ReceivedAt, string? MessageId);
}
