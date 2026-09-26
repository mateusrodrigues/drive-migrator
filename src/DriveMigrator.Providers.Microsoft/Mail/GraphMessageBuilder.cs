using System.Globalization;
using MimeKit;

namespace DriveMigrator.Providers.Microsoft.Mail;

/// <summary>
/// Turns a MIME message into a Graph message payload that Outlook stores as a received message rather than a draft.
/// Graph always saves MIME uploads as drafts, so the message is rebuilt as JSON and MAPI properties are set at
/// creation: PR_MESSAGE_FLAGS without "unsent", the original delivery/submit times, Message-ID and threading ids.
/// </summary>
internal static class GraphMessageBuilder
{
    /// <summary>Attachments up to this size go inline in the create request; larger ones use upload sessions.</summary>
    internal const int InlineAttachmentLimit = 3 * 1024 * 1024;

    /// <summary>Keep the create request safely under Graph's 4 MB limit.</summary>
    internal const int InlinePayloadBudget = 3 * 1024 * 1024;

    public static GraphMessage Build(MimeMessage message, bool isRead, bool isFlagged, DateTimeOffset? receivedAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        var html = message.HtmlBody;
        var body = new Dictionary<string, object>
        {
            ["contentType"] = html is null ? "text" : "html",
            ["content"] = html ?? message.TextBody ?? string.Empty,
        };

        var sent = message.Date == DateTimeOffset.MinValue ? (DateTimeOffset?)null : message.Date;
        var delivered = receivedAt ?? sent;
        var properties = new List<Dictionary<string, object>>
        {
            // MSGFLAG_READ (1) or nothing; crucially without MSGFLAG_UNSENT (8), so it isn't a draft.
            Property("Integer 0x0E07", isRead ? "1" : "0"),
        };
        if (delivered is { } d)
        {
            properties.Add(Property("SystemTime 0x0E06", Iso(d)));
        }

        if (sent is { } s)
        {
            properties.Add(Property("SystemTime 0x0039", Iso(s)));
        }

        if (!string.IsNullOrEmpty(message.MessageId))
        {
            properties.Add(Property("String 0x1035", $"<{message.MessageId}>"));
        }

        if (!string.IsNullOrEmpty(message.InReplyTo))
        {
            properties.Add(Property("String 0x1042", $"<{message.InReplyTo}>"));
        }

        if (message.References.Count > 0)
        {
            properties.Add(Property("String 0x1039", string.Join(' ', message.References.Select(r => $"<{r}>"))));
        }

        var payload = new Dictionary<string, object>
        {
            ["subject"] = message.Subject ?? string.Empty,
            ["body"] = body,
            ["isRead"] = isRead,
            ["importance"] = message.Importance switch
            {
                MessageImportance.High => "high",
                MessageImportance.Low => "low",
                _ => "normal",
            },
            ["flag"] = new Dictionary<string, object> { ["flagStatus"] = isFlagged ? "flagged" : "notFlagged" },
            ["toRecipients"] = Recipients(message.To),
            ["ccRecipients"] = Recipients(message.Cc),
            ["bccRecipients"] = Recipients(message.Bcc),
            ["replyTo"] = Recipients(message.ReplyTo),
            ["singleValueExtendedProperties"] = properties,
        };
        if (Recipients(message.From) is [var from, ..])
        {
            payload["from"] = from;
            payload["sender"] = from;
        }

        // Attachments: small ones travel with the create request (within budget), the rest are added afterwards.
        var inline = new List<Dictionary<string, object>>();
        var deferred = new List<GraphAttachment>();
        var budget = InlinePayloadBudget - (html?.Length ?? message.TextBody?.Length ?? 0) * 2;
        foreach (var attachment in Attachments(message, html is not null))
        {
            var encodedSize = (attachment.Content.Length + 2) / 3 * 4;
            if (attachment.Content.Length <= InlineAttachmentLimit && encodedSize <= budget)
            {
                inline.Add(attachment.ToJson());
                budget -= encodedSize;
            }
            else
            {
                deferred.Add(attachment);
            }
        }

        if (inline.Count > 0)
        {
            payload["attachments"] = inline;
        }

        return new GraphMessage(payload, deferred);
    }

    private static IEnumerable<GraphAttachment> Attachments(MimeMessage message, bool htmlBody)
    {
        var bodyPart = htmlBody ? message.HtmlBody : message.TextBody;
        var index = 0;
        foreach (var entity in message.BodyParts)
        {
            index++;
            switch (entity)
            {
                // The text/html body parts themselves (not attached text files).
                case TextPart text when !text.IsAttachment && (text.IsHtml || text.IsPlain || text.Text == bodyPart):
                    continue;

                case MessagePart { Message: { } attached } attachedMessage:
                {
                    using var stream = new MemoryStream();
                    attached.WriteTo(stream);
                    var name = attachedMessage.ContentDisposition?.FileName ?? $"{attached.Subject ?? "message"}.eml";
                    yield return new GraphAttachment(name, "message/rfc822", stream.ToArray(), IsInline: false, ContentId: null);
                    break;
                }

                case MimePart part:
                {
                    using var stream = new MemoryStream();
                    part.Content?.DecodeTo(stream);
                    var isInline = !part.IsAttachment && part.ContentId is not null;
                    var name = part.FileName ?? part.ContentId ?? $"attachment-{index}";
                    yield return new GraphAttachment(name, part.ContentType.MimeType, stream.ToArray(), isInline, part.ContentId);
                    break;
                }
            }
        }
    }

    private static List<Dictionary<string, object>> Recipients(InternetAddressList list)
        => [.. list.Mailboxes.Select(m => new Dictionary<string, object>
        {
            ["emailAddress"] = new Dictionary<string, object> { ["name"] = m.Name ?? m.Address, ["address"] = m.Address },
        })];

    private static Dictionary<string, object> Property(string id, string value) => new() { ["id"] = id, ["value"] = value };

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

internal sealed record GraphMessage(Dictionary<string, object> Payload, IReadOnlyList<GraphAttachment> DeferredAttachments);

internal sealed record GraphAttachment(string Name, string ContentType, byte[] Content, bool IsInline, string? ContentId)
{
    public Dictionary<string, object> ToJson()
    {
        var json = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.fileAttachment",
            ["name"] = Name,
            ["contentType"] = ContentType,
            ["contentBytes"] = Convert.ToBase64String(Content),
            ["isInline"] = IsInline,
        };
        if (ContentId is not null)
        {
            json["contentId"] = ContentId;
        }

        return json;
    }
}
