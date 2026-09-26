using System.Runtime.CompilerServices;
using DriveMigrator.Core;
using DriveMigrator.Core.Contacts;
using DriveMigrator.Providers.Microsoft.Graph;

namespace DriveMigrator.Providers.Microsoft.Contacts;

/// <summary>
/// Outlook contacts through Microsoft Graph. The default "Contacts" folder (id "me") is the only top-level node;
/// contact folders live inside it. Folder ids are "me/contactFolders/{id}", contact ids "me/contacts/{id}".
/// </summary>
internal sealed class OutlookContactsCapability(GraphClient graph) : IContactsCapability
{
    internal const string DefaultFolderId = "me";

    private const string ListSelect = "$select=id,displayName,givenName,surname,emailAddresses";

    private static readonly MigrationNode DefaultFolder = new(DefaultFolderId, "Contacts", NodeKind.ContactFolder) { Role = ContainerRole.DefaultContacts };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<HashSet<string>>> _keys = [];

    public CapabilityKind Kind => CapabilityKind.Contacts;

    public string DisplayName => "Outlook Contacts";

    public bool SupportsNestedContainers => true;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (parent is null)
        {
            yield return DefaultFolder;
            yield break;
        }

        var folders = parent.Id == DefaultFolderId ? "me/contactFolders" : $"{parent.Id}/childFolders";
        await foreach (var folder in graph.GetPagedAsync<ContactFolder>($"{folders}?$select=id,displayName&$top=100", cancellationToken).ConfigureAwait(false))
        {
            yield return new MigrationNode($"me/contactFolders/{folder.Id}", folder.DisplayName, NodeKind.ContactFolder);
        }

        await foreach (var contact in ListContactsAsync(parent, cancellationToken).ConfigureAwait(false))
        {
            var mapped = GraphContactMapper.ToContact(contact);
            yield return new MigrationNode($"me/contacts/{contact.Id}", mapped.DisplayName, NodeKind.Contact)
            {
                Detail = mapped.Emails is [var first, ..] ? first.Value : null,
            };
        }
    }

    public Task<MigrationNode?> GetSpecialContainerAsync(ContainerRole role, CancellationToken cancellationToken = default)
        => Task.FromResult(role == ContainerRole.DefaultContacts ? DefaultFolder : null);

    public async Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        var url = parent is null || parent.Id == DefaultFolderId ? "me/contactFolders" : $"{parent.Id}/childFolders";
        var created = await graph.SendJsonAsync<ContactFolder>(HttpMethod.Post, url, new Dictionary<string, object> { ["displayName"] = name }, cancellationToken).ConfigureAwait(false);
        var node = new MigrationNode($"me/contactFolders/{created.Id}", created.DisplayName, NodeKind.ContactFolder);
        lock (_gate)
        {
            _keys.TryAdd(node.Id, Task.FromResult(new HashSet<string>()));
        }

        return node;
    }

    public async Task<bool> ContainsContactAsync(MigrationNode? folder, Contact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var keys = await GetKeysAsync(folder ?? DefaultFolder, cancellationToken).ConfigureAwait(false);
        lock (keys)
        {
            return keys.Contains(contact.MatchKey);
        }
    }

    public async Task<Contact> ReadContactAsync(MigrationNode contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return GraphContactMapper.ToContact(await graph.GetAsync<GraphContact>(contact.Id, cancellationToken).ConfigureAwait(false));
    }

    public async Task<MigrationNode> ImportContactAsync(MigrationNode? folder, Contact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        folder ??= DefaultFolder;
        var created = await graph.SendJsonAsync<GraphContact>(HttpMethod.Post, ContactsUrl(folder), GraphContactMapper.ToGraph(contact), cancellationToken).ConfigureAwait(false);

        var keys = await GetKeysAsync(folder, cancellationToken).ConfigureAwait(false);
        lock (keys)
        {
            keys.Add(contact.MatchKey);
        }

        return new MigrationNode($"me/contacts/{created.Id}", contact.DisplayName, NodeKind.Contact);
    }

    private static string ContactsUrl(MigrationNode folder) => folder.Id == DefaultFolderId ? "me/contacts" : $"{folder.Id}/contacts";

    private IAsyncEnumerable<GraphContact> ListContactsAsync(MigrationNode folder, CancellationToken cancellationToken)
        => graph.GetPagedAsync<GraphContact>($"{ContactsUrl(folder)}?{ListSelect}&$orderby=displayName&$top=100", cancellationToken);

    /// <summary>Match keys of the contacts in a folder, loaded once and kept up to date as contacts are imported.</summary>
    private Task<HashSet<string>> GetKeysAsync(MigrationNode folder, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(folder.Id, out var keys) || keys.IsFaulted || keys.IsCanceled)
            {
                keys = LoadKeysAsync(folder, cancellationToken);
                _keys[folder.Id] = keys;
            }

            return keys;
        }
    }

    private async Task<HashSet<string>> LoadKeysAsync(MigrationNode folder, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>();
        await foreach (var contact in ListContactsAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            keys.Add(GraphContactMapper.ToContact(contact).MatchKey);
        }

        return keys;
    }
}
