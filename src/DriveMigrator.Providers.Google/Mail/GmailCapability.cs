using System.Runtime.CompilerServices;
using DriveMigrator.Core;
using DriveMigrator.Core.Mail;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using MimeKit;

namespace DriveMigrator.Providers.Google.Mail;

/// <summary>
/// Gmail, presented as folders: system labels (Inbox, Sent, Spam, Trash), a virtual "All Mail" holding every
/// message including archived ones, and user labels nested by their "/" separators. A message with several labels
/// appears under each of them. Node ids are label ids (or "ALL") and message ids.
/// </summary>
internal sealed class GmailCapability : IMailCapability
{
    internal const string AllMail = "ALL";
    private const string Me = "me";

    private static readonly (string Id, string Name, MailFolderRole Role)[] SystemFolders =
    [
        ("INBOX", "Inbox", MailFolderRole.Inbox),
        ("SENT", "Sent", MailFolderRole.Sent),
        ("SPAM", "Spam", MailFolderRole.Junk),
        ("TRASH", "Trash", MailFolderRole.Deleted),
        (AllMail, "All Mail", MailFolderRole.Archive),
    ];

    /// <summary>Names Gmail reserves; user labels can't use them.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Inbox", "Sent", "Sent Mail", "Drafts", "Draft", "Spam", "Trash", "Starred", "Important", "Unread",
        "Chat", "Chats", "All Mail", "Scheduled", "Snoozed", "Outbox",
    };

    private static readonly string[] DetailHeaders = ["Subject", "From"];

    private readonly GmailService _gmail;
    private readonly Lock _gate = new();
    private Task<List<Label>>? _labels;

    public GmailCapability(GmailService gmail)
    {
        _gmail = gmail;
        GoogleBackOff.Install(gmail);
    }

    public CapabilityKind Kind => CapabilityKind.Mail;

    public string DisplayName => "Gmail";

    public bool SupportsNestedContainers => true;

    public IAsyncEnumerable<MigrationNode> GetChildrenAsync(MigrationNode? parent, CancellationToken cancellationToken = default)
        => ListAsync(parent, maxMessages: int.MaxValue, withDetails: false, cancellationToken);

    /// <summary>For the tree: shows subjects and senders, which costs one request per message, so only for a page of them.</summary>
    public IAsyncEnumerable<MigrationNode> BrowseChildrenAsync(MigrationNode? parent, int maxItems, CancellationToken cancellationToken = default)
        => ListAsync(parent, maxItems, withDetails: true, cancellationToken);

    public string ToValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // "/" would nest labels.
        var valid = name.Replace('/', '-').Trim();
        return valid.Length == 0 || ReservedNames.Contains(valid) ? $"{valid} (imported)".Trim() : valid;
    }

    public async Task<MigrationNode?> GetSpecialFolderAsync(MailFolderRole role, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return SystemFolders.Where(f => f.Role == role).Select(f => SystemNode(f.Id, f.Name, f.Role)).FirstOrDefault();
    }

    public async Task<bool> ContainsMessageAsync(MigrationNode folder, string internetMessageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return await FindByMessageIdAsync(internetMessageId, folder.Id, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        var labels = await GetLabelsAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        var parentLabel = parent is null ? null : labels.FirstOrDefault(l => l.Id == parent.Id && l.Type == "user");

        // Labels can't nest under system labels (Inbox...), so those children become top-level labels.
        var fullName = parentLabel is null ? name : $"{parentLabel.Name}/{name}";
        var created = await _gmail.Users.Labels.Create(
            new Label { Name = fullName, LabelListVisibility = "labelShow", MessageListVisibility = "show", Type = "user" },
            Me).ExecuteAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Gmail returned nothing when creating the label '{fullName}'.");

        lock (_gate)
        {
            labels.Add(created);
        }

        return new MigrationNode(created.Id, name, NodeKind.MailFolder);
    }

    public async Task<MailMessageContent> ReadMessageAsync(MigrationNode message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var request = _gmail.Users.Messages.Get(Me, message.Id);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
        request.Fields = "id,raw,labelIds,internalDate";
        var raw = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        var bytes = DecodeBase64Url(raw?.Raw ?? throw new IOException($"Gmail returned no content for message {message.Id}."));
        var stream = new MemoryStream(bytes, writable: false);
        var headers = await HeaderList.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;

        var labels = raw.LabelIds ?? [];
        return new MailMessageContent(stream)
        {
            IsRead = !labels.Contains("UNREAD"),
            IsFlagged = labels.Contains("STARRED"),
            ReceivedAt = raw.InternalDate is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
            InternetMessageId = headers[HeaderId.MessageId]?.Trim(),
            Labels = [.. labels],
        };
    }

    public async Task<MigrationNode> ImportMessageAsync(MigrationNode folder, MailMessageContent message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(message);

        // Gmail keeps one copy of a message with many labels: if it's already here, just add this label.
        if (message.InternetMessageId is { Length: > 0 } messageId
            && await FindByMessageIdAsync(messageId, labelId: null, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (folder.Id != AllMail)
            {
                await _gmail.Users.Messages.Modify(new ModifyMessageRequest { AddLabelIds = [folder.Id] }, Me, existing)
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }

            return MessageNode(existing);
        }

        var labelIds = new List<string>();
        if (folder.Id != AllMail)
        {
            labelIds.Add(folder.Id);
        }

        if (!message.IsRead)
        {
            labelIds.Add("UNREAD");
        }

        if (message.IsFlagged)
        {
            labelIds.Add("STARRED");
        }

        // Import (unlike insert) runs the message through Gmail's normal classification; keep it out of spam and
        // don't create calendar events from invitations, and date it by its own Date header.
        var upload = _gmail.Users.Messages.Import(new Message { LabelIds = labelIds }, Me, message.Mime, "message/rfc822");
        upload.InternalDateSource = UsersResource.MessagesResource.ImportMediaUpload.InternalDateSourceEnum.DateHeader;
        upload.NeverMarkSpam = true;
        upload.ProcessForCalendar = false;
        var result = await upload.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status != global::Google.Apis.Upload.UploadStatus.Completed)
        {
            throw new IOException($"Importing a message into Gmail failed: {result.Exception?.Message ?? result.Status.ToString()}", result.Exception);
        }

        return MessageNode(upload.ResponseBody?.Id ?? throw new IOException("Gmail returned no id for the imported message."));
    }

    internal static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }

    private async IAsyncEnumerable<MigrationNode> ListAsync(
        MigrationNode? parent,
        int maxMessages,
        bool withDetails,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var labels = await GetLabelsAsync(refresh: parent is null, cancellationToken).ConfigureAwait(false);
        var userLabels = labels.Where(l => l.Type == "user" && !string.IsNullOrEmpty(l.Name)).OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var names = userLabels.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);

        if (parent is null)
        {
            foreach (var (id, name, role) in SystemFolders)
            {
                yield return SystemNode(id, name, role);
            }

            // Top level: labels without a parent label (a "A/B" label whose "A" doesn't exist shows in full).
            foreach (var label in userLabels.Where(l => ParentPath(l.Name) is not { } p || !names.Contains(p)))
            {
                yield return new MigrationNode(label.Id, label.Name, NodeKind.MailFolder);
            }

            yield break;
        }

        var parentName = userLabels.FirstOrDefault(l => l.Id == parent.Id)?.Name;
        if (parentName is not null)
        {
            foreach (var label in userLabels.Where(l => ParentPath(l.Name) == parentName))
            {
                yield return new MigrationNode(label.Id, label.Name[(parentName.Length + 1)..], NodeKind.MailFolder);
            }
        }

        var list = _gmail.Users.Messages.List(Me);
        if (parent.Id != AllMail)
        {
            list.LabelIds = parent.Id;
        }

        list.IncludeSpamTrash = parent.Id is "SPAM" or "TRASH";
        list.MaxResults = Math.Min(500, maxMessages);
        list.Fields = "nextPageToken,messages(id)";
        var count = 0;
        do
        {
            // With a field filter, Gmail answers an empty result with an empty body, which the client returns as null.
            var page = await list.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var ids = (page?.Messages ?? []).Select(m => m.Id).Take(maxMessages - count).ToList();
            count += ids.Count;

            if (withDetails)
            {
                foreach (var node in await GetDetailsAsync(ids, cancellationToken).ConfigureAwait(false))
                {
                    yield return node;
                }
            }
            else
            {
                foreach (var id in ids)
                {
                    yield return MessageNode(id);
                }
            }

            list.PageToken = count < maxMessages ? page?.NextPageToken : null;
        }
        while (list.PageToken is not null);
    }

    /// <summary>Fetches subject, sender, date and size for a page of messages, a few requests at a time.</summary>
    private async Task<MigrationNode[]> GetDetailsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(8);
        return await Task.WhenAll(ids.Select(async id =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var request = _gmail.Users.Messages.Get(Me, id);
                request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                request.MetadataHeaders = DetailHeaders;
                request.Fields = "id,internalDate,sizeEstimate,payload/headers";
                var message = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                var headers = message?.Payload?.Headers ?? [];
                var subject = headers.FirstOrDefault(h => string.Equals(h.Name, "Subject", StringComparison.OrdinalIgnoreCase))?.Value;
                var from = headers.FirstOrDefault(h => string.Equals(h.Name, "From", StringComparison.OrdinalIgnoreCase))?.Value;
                return MessageNode(id) with
                {
                    Name = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject,
                    Detail = from is null ? null : InternetAddressList.TryParse(from, out var parsed) && parsed.Mailboxes.FirstOrDefault() is { } box ? box.Name ?? box.Address : from,
                    Size = message?.SizeEstimate,
                    ModifiedAt = message?.InternalDate is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
                };
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);
    }

    private async Task<string?> FindByMessageIdAsync(string internetMessageId, string? labelId, CancellationToken cancellationToken)
    {
        var list = _gmail.Users.Messages.List(Me);
        list.Q = $"rfc822msgid:{internetMessageId.Trim().Trim('<', '>')}";
        if (labelId is not null and not AllMail)
        {
            list.LabelIds = labelId;
        }

        list.IncludeSpamTrash = labelId is null or "SPAM" or "TRASH";
        list.MaxResults = 1;
        list.Fields = "messages(id)";
        // No match comes back as an empty body, i.e. null.
        var page = await list.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return page?.Messages?.FirstOrDefault()?.Id;
    }

    private Task<List<Label>> GetLabelsAsync(bool refresh, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (refresh || _labels is null || _labels.IsFaulted || _labels.IsCanceled)
            {
                _labels = LoadLabelsAsync(cancellationToken);
            }

            return _labels;
        }
    }

    private async Task<List<Label>> LoadLabelsAsync(CancellationToken cancellationToken)
        => [.. (await _gmail.Users.Labels.List(Me).ExecuteAsync(cancellationToken).ConfigureAwait(false))?.Labels ?? []];

    private static string? ParentPath(string name) => name.LastIndexOf('/') is var i and > 0 ? name[..i] : null;

    private static MigrationNode SystemNode(string id, string name, MailFolderRole role) => new(id, name, NodeKind.MailFolder) { Role = role };

    private static MigrationNode MessageNode(string id) => new(id, $"Message {id}", NodeKind.MailMessage) { MimeType = "message/rfc822" };
}
