using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using DriveMigrator.Core;
using DriveMigrator.Core.Drive;
using DriveMigrator.Core.Mail;
using DriveMigrator.Providers.Microsoft.Graph;
using MimeKit;

namespace DriveMigrator.Providers.Microsoft.Mail;

/// <summary>
/// Outlook mail through Microsoft Graph. Folder ids are "me/mailFolders/{id}", message ids "me/messages/{id}".
/// Drafts and Outbox are hidden: their contents aren't mail that was received or sent.
/// </summary>
internal sealed class OutlookMailCapability(GraphClient graph) : IMailCapability
{
    internal const int AttachmentChunkSize = 3 * 1024 * 1024;

    private const string FolderSelect = "$select=id,displayName,childFolderCount,totalItemCount";
    private const string MessageSelect = "$select=id,subject,from,receivedDateTime,isRead,flag,internetMessageId";

    private static readonly (string WellKnownName, ContainerRole? Role)[] WellKnownFolders =
    [
        ("inbox", ContainerRole.Inbox),
        ("sentitems", ContainerRole.Sent),
        ("junkemail", ContainerRole.Junk),
        ("deleteditems", ContainerRole.Deleted),
        ("archive", ContainerRole.Archive),
        ("drafts", null),
        ("outbox", null),
    ];

    private readonly Lock _gate = new();
    private Task<Dictionary<string, (ContainerRole? Role, MailFolder Folder)>>? _wellKnown;

    public CapabilityKind Kind => CapabilityKind.Mail;

    public string DisplayName => "Outlook Mail";

    public bool SupportsNestedContainers => true;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var wellKnown = await GetWellKnownAsync(cancellationToken).ConfigureAwait(false);
        var folders = parent is null ? $"me/mailFolders?{FolderSelect}&$top=100" : $"{parent.Id}/childFolders?{FolderSelect}&$top=100";
        await foreach (var folder in graph.GetPagedAsync<MailFolder>(folders, cancellationToken).ConfigureAwait(false))
        {
            wellKnown.TryGetValue(folder.Id, out var known);
            if (known.Folder is not null && known.Role is null)
            {
                continue; // Drafts, Outbox
            }

            yield return FolderNode(folder, known.Role);
        }

        if (parent is null)
        {
            yield break;
        }

        await foreach (var message in graph.GetPagedAsync<MessageSummary>($"{parent.Id}/messages?{MessageSelect}&$orderby=receivedDateTime desc&$top=100", cancellationToken).ConfigureAwait(false))
        {
            yield return new MigrationNode($"me/messages/{message.Id}", string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject, NodeKind.MailMessage)
            {
                Detail = message.From?.EmailAddress?.Name ?? message.From?.EmailAddress?.Address,
                ModifiedAt = message.ReceivedDateTime,
                MimeType = "message/rfc822",
            };
        }
    }

    public async Task<MigrationNode?> GetSpecialContainerAsync(ContainerRole role, CancellationToken cancellationToken = default)
    {
        var wellKnown = await GetWellKnownAsync(cancellationToken).ConfigureAwait(false);
        var match = wellKnown.Values.FirstOrDefault(f => f.Role == role);
        return match.Folder is null ? null : FolderNode(match.Folder, role);
    }

    public async Task<bool> ContainsMessageAsync(MigrationNode folder, string internetMessageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var filter = Uri.EscapeDataString($"internetMessageId eq '{internetMessageId.Replace("'", "''", StringComparison.Ordinal)}'");
        var page = await graph.GetAsync<GraphPage<CreatedItem>>($"{folder.Id}/messages?$filter={filter}&$select=id&$top=1", cancellationToken).ConfigureAwait(false);
        return page.Value.Count > 0;
    }

    public async Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        var url = parent is null ? "me/mailFolders" : $"{parent.Id}/childFolders";
        var created = await graph.SendJsonAsync<MailFolder>(HttpMethod.Post, url, new Dictionary<string, object> { ["displayName"] = name }, cancellationToken).ConfigureAwait(false);
        return FolderNode(created, role: null);
    }

    public async Task<MailMessageContent> ReadMessageAsync(MigrationNode message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var summary = await graph.GetAsync<MessageSummary>($"{message.Id}?{MessageSelect}", cancellationToken).ConfigureAwait(false);
        var response = await graph.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{message.Id}/$value"), cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new MailMessageContent(new HttpResponseStream(stream, response))
            {
                IsRead = summary.IsRead ?? false,
                IsFlagged = string.Equals(summary.Flag?.FlagStatus, "flagged", StringComparison.OrdinalIgnoreCase),
                ReceivedAt = summary.ReceivedDateTime,
                InternetMessageId = summary.InternetMessageId,
            };
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<MigrationNode> ImportMessageAsync(MigrationNode folder, MailMessageContent message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(message);

        var mime = await MimeMessage.LoadAsync(message.Mime, cancellationToken).ConfigureAwait(false);
        var built = GraphMessageBuilder.Build(mime, message.IsRead, message.IsFlagged, message.ReceivedAt);
        var created = await graph.SendJsonAsync<CreatedItem>(HttpMethod.Post, $"{folder.Id}/messages", built.Payload, cancellationToken).ConfigureAwait(false);
        var path = $"me/messages/{created.Id}";

        foreach (var attachment in built.DeferredAttachments)
        {
            await AddAttachmentAsync(path, attachment, cancellationToken).ConfigureAwait(false);
        }

        return new MigrationNode(path, string.IsNullOrWhiteSpace(mime.Subject) ? "(no subject)" : mime.Subject, NodeKind.MailMessage)
        {
            ModifiedAt = message.ReceivedAt,
        };
    }

    private static MigrationNode FolderNode(MailFolder folder, ContainerRole? role)
        => new($"me/mailFolders/{folder.Id}", folder.DisplayName, NodeKind.MailFolder) { Role = role };

    /// <summary>Adds an attachment too large for the create request, using a Graph attachment upload session.</summary>
    private async Task AddAttachmentAsync(string messagePath, GraphAttachment attachment, CancellationToken cancellationToken)
    {
        if (attachment.Content.Length <= GraphMessageBuilder.InlineAttachmentLimit)
        {
            await graph.SendJsonAsync<CreatedItem>(HttpMethod.Post, $"{messagePath}/attachments", attachment.ToJson(), cancellationToken).ConfigureAwait(false);
            return;
        }

        var item = new Dictionary<string, object>
        {
            ["attachmentType"] = "file",
            ["name"] = attachment.Name,
            ["size"] = attachment.Content.Length,
            ["contentType"] = attachment.ContentType,
            ["isInline"] = attachment.IsInline,
        };
        if (attachment.ContentId is not null)
        {
            item["contentId"] = attachment.ContentId;
        }

        var session = await graph.SendJsonAsync<UploadSessionInfo>(
            HttpMethod.Post,
            $"{messagePath}/attachments/createUploadSession",
            new Dictionary<string, object> { ["AttachmentItem"] = item },
            cancellationToken).ConfigureAwait(false);

        var length = attachment.Content.Length;
        for (var offset = 0; offset < length; offset += AttachmentChunkSize)
        {
            var count = Math.Min(AttachmentChunkSize, length - offset);
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, session.UploadUrl)
                {
                    Content = new ByteArrayContent(attachment.Content, offset, count),
                };
                request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);
                using var response = await graph.SendUnauthenticatedAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }

                if (GraphClient.IsTransient(response.StatusCode) && attempt < GraphClient.MaxRetries)
                {
                    await graph.DelayAsync(GraphClient.RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw await GraphException.FromResponseAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Resolves the well-known folders once per session (Graph v1.0 doesn't mark them in folder listings).</summary>
    private Task<Dictionary<string, (ContainerRole? Role, MailFolder Folder)>> GetWellKnownAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_wellKnown is null || _wellKnown.IsFaulted || _wellKnown.IsCanceled)
            {
                _wellKnown = LoadWellKnownAsync(cancellationToken);
            }

            return _wellKnown;
        }
    }

    private async Task<Dictionary<string, (ContainerRole? Role, MailFolder Folder)>> LoadWellKnownAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (ContainerRole?, MailFolder)>();
        foreach (var (name, role) in WellKnownFolders)
        {
            try
            {
                var folder = await graph.GetAsync<MailFolder>($"me/mailFolders/{name}?{FolderSelect}", cancellationToken).ConfigureAwait(false);
                result[folder.Id] = (role, folder);
            }
            catch (GraphException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                // Not every mailbox has every well-known folder (e.g. Archive).
            }
        }

        return result;
    }
}
