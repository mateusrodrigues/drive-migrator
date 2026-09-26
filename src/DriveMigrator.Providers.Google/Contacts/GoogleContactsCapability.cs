using System.Runtime.CompilerServices;
using DriveMigrator.Core;
using DriveMigrator.Core.Contacts;
using Google.Apis.PeopleService.v1;
using Google.Apis.PeopleService.v1.Data;

namespace DriveMigrator.Providers.Google.Contacts;

/// <summary>
/// Google Contacts through the People API: "All contacts" (every contact) plus the user's contact groups, which
/// don't nest. Node ids are "ALL", group resource names ("contactGroups/…") and person resource names ("people/c…").
/// </summary>
internal sealed class GoogleContactsCapability : IContactsCapability, IDisposable
{
    internal const string AllContacts = "ALL";

    /// <summary>People API batch reads accept at most 200 resource names.</summary>
    private const int BatchSize = 200;

    private static readonly MigrationNode AllContactsNode = new(AllContacts, "All contacts", NodeKind.ContactFolder) { Role = ContainerRole.DefaultContacts };

    private readonly PeopleServiceService _people;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<HashSet<string>>> _keys = [];

    // Google asks for one contact write at a time per user; parallel writes cause errors and throttling.
    private readonly SemaphoreSlim _writes = new(1, 1);

    public GoogleContactsCapability(PeopleServiceService people)
    {
        _people = people;
        GoogleBackOff.Install(people);
    }

    public CapabilityKind Kind => CapabilityKind.Contacts;

    public string DisplayName => "Google Contacts";

    public bool SupportsNestedContainers => false;

    public async IAsyncEnumerable<MigrationNode> GetChildrenAsync(
        MigrationNode? parent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (parent is null)
        {
            yield return AllContactsNode;
            await foreach (var group in ListGroupsAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new MigrationNode(group.ResourceName, group.FormattedName ?? group.Name, NodeKind.ContactFolder);
            }

            yield break;
        }

        await foreach (var person in ListPeopleAsync(parent, cancellationToken).ConfigureAwait(false))
        {
            var contact = PersonMapper.ToContact(person);
            yield return new MigrationNode(person.ResourceName, contact.DisplayName, NodeKind.Contact)
            {
                Detail = contact.Emails is [var first, ..] ? first.Value : null,
            };
        }
    }

    public Task<MigrationNode?> GetSpecialContainerAsync(ContainerRole role, CancellationToken cancellationToken = default)
        => Task.FromResult(role == ContainerRole.DefaultContacts ? AllContactsNode : null);

    /// <summary>Groups can't nest; a folder inside a group becomes a group named "Parent - Child".</summary>
    public async Task<MigrationNode> CreateContainerAsync(MigrationNode? parent, string name, CancellationToken cancellationToken = default)
    {
        var fullName = parent is null || parent.Id == AllContacts ? name : $"{parent.Name} - {name}";
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var created = await _people.ContactGroups.Create(new CreateContactGroupRequest { ContactGroup = new ContactGroup { Name = fullName } })
                .ExecuteAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"Google returned nothing when creating the contact group '{fullName}'.");
            var node = new MigrationNode(created.ResourceName, created.FormattedName ?? fullName, NodeKind.ContactFolder);
            lock (_gate)
            {
                _keys.TryAdd(node.Id, Task.FromResult(new HashSet<string>()));
            }

            return node;
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<bool> ContainsContactAsync(MigrationNode? folder, Contact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var keys = await GetKeysAsync(folder ?? AllContactsNode, cancellationToken).ConfigureAwait(false);
        lock (keys)
        {
            return keys.Contains(contact.MatchKey);
        }
    }

    public async Task<Contact> ReadContactAsync(MigrationNode contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var request = _people.People.Get(contact.Id);
        request.PersonFields = PersonMapper.FullFields;
        var person = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Google returned nothing for contact {contact.Id}.");
        return PersonMapper.ToContact(person);
    }

    public async Task<MigrationNode> ImportContactAsync(MigrationNode? folder, Contact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        folder ??= AllContactsNode;
        var group = folder.Id == AllContacts ? null : folder.Id;

        Person created;
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = _people.People.CreateContact(PersonMapper.ToPerson(contact, group));
            request.PersonFields = PersonMapper.ListFields;
            created = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"Google returned nothing when creating the contact '{contact.DisplayName}'.");
        }
        finally
        {
            _writes.Release();
        }

        // The new contact is in its group and in "All contacts".
        foreach (var id in group is null ? [AllContacts] : new[] { group, AllContacts })
        {
            Task<HashSet<string>>? cached;
            lock (_gate)
            {
                _keys.TryGetValue(id, out cached);
            }

            if (cached is { IsCompletedSuccessfully: true })
            {
                var keys = await cached.ConfigureAwait(false);
                lock (keys)
                {
                    keys.Add(contact.MatchKey);
                }
            }
        }

        return new MigrationNode(created.ResourceName, contact.DisplayName, NodeKind.Contact);
    }

    public void Dispose() => _writes.Dispose();

    private async IAsyncEnumerable<ContactGroup> ListGroupsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = _people.ContactGroups.List();
        request.PageSize = 1000;
        do
        {
            var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var group in (page?.ContactGroups ?? []).Where(g => g.GroupType == "USER_CONTACT_GROUP"))
            {
                yield return group;
            }

            request.PageToken = page?.NextPageToken;
        }
        while (request.PageToken is not null);
    }

    private async IAsyncEnumerable<Person> ListPeopleAsync(MigrationNode folder, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (folder.Id == AllContacts)
        {
            var request = _people.People.Connections.List("people/me");
            request.PersonFields = PersonMapper.ListFields;
            request.PageSize = 1000;
            request.SortOrder = PeopleResource.ConnectionsResource.ListRequest.SortOrderEnum.FIRSTNAMEASCENDING;
            do
            {
                var page = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                foreach (var person in page?.Connections ?? [])
                {
                    yield return person;
                }

                request.PageToken = page?.NextPageToken;
            }
            while (request.PageToken is not null);
            yield break;
        }

        var groupRequest = _people.ContactGroups.Get(folder.Id);
        groupRequest.MaxMembers = 100_000;
        var group = await groupRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in (group?.MemberResourceNames ?? []).Chunk(BatchSize))
        {
            var batch = _people.People.GetBatchGet();
            batch.ResourceNames = chunk;
            batch.PersonFields = PersonMapper.ListFields;
            var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var person in (response?.Responses ?? []).Select(r => r.Person).OfType<Person>())
            {
                yield return person;
            }
        }
    }

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
        await foreach (var person in ListPeopleAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            keys.Add(PersonMapper.ToContact(person).MatchKey);
        }

        return keys;
    }
}
