using DriveMigrator.Core;
using DriveMigrator.Core.Contacts;

namespace DriveMigrator.Testing;

public sealed class FakeContactsCapability() : InMemoryCapability(CapabilityKind.Contacts, NodeKind.ContactFolder, supportsNestedContainers: true), IContactsCapability
{
    public MigrationNode AddContact(MigrationNode? folder, Contact contact)
        => Add(folder, ContactNode(contact), contact);

    /// <summary>Adds a top-level well-known folder, such as the default contact list.</summary>
    public MigrationNode AddSpecialFolder(ContainerRole role, string name)
        => Add(null, new MigrationNode(NewId(), name, NodeKind.ContactFolder) { Role = role }, payload: null);

    public async Task<MigrationNode?> GetSpecialContainerAsync(ContainerRole role, CancellationToken cancellationToken = default)
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

    public async Task<bool> ContainsContactAsync(MigrationNode? folder, Contact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        await foreach (var node in GetChildrenAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            if (!node.IsContainer && GetPayload<Contact>(node).MatchKey == contact.MatchKey)
            {
                return true;
            }
        }

        return false;
    }

    public Task<Contact> ReadContactAsync(MigrationNode contact, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetPayload<Contact>(contact));
    }

    public Task<MigrationNode> ImportContactAsync(MigrationNode? folder, Contact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AddContact(folder, contact));
    }

    private static MigrationNode ContactNode(Contact contact)
        => new(NewId(), contact.DisplayName, NodeKind.Contact) { Detail = contact.Emails is [var first, ..] ? first.Value : null };
}
