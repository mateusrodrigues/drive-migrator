namespace DriveMigrator.Core.Contacts;

public interface IContactsCapability : ICapability
{
    Task<Contact> ReadContactAsync(MigrationNode contact, CancellationToken cancellationToken = default);

    /// <summary>Imports a contact into <paramref name="folder"/>, or the default contact list when null.</summary>
    Task<MigrationNode> ImportContactAsync(MigrationNode? folder, Contact contact, CancellationToken cancellationToken = default);
}
