using DriveMigrator.Core;
using DriveMigrator.Core.Contacts;

namespace DriveMigrator.Testing;

public sealed class FakeContactsCapability() : InMemoryCapability(CapabilityKind.Contacts, NodeKind.ContactFolder, supportsNestedContainers: true), IContactsCapability
{
    public MigrationNode AddContact(MigrationNode? folder, Contact contact)
        => Add(folder, ContactNode(contact), contact);

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

    private static MigrationNode ContactNode(Contact contact) => new(NewId(), contact.DisplayName, NodeKind.Contact);
}
