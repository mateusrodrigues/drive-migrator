namespace DriveMigrator.Core.Contacts;

public interface IContactsCapability : ICapability
{
    /// <summary>
    /// Whether <paramref name="folder"/> (the default contact list when null) already has a contact with the same
    /// <see cref="Contact.MatchKey"/>.
    /// </summary>
    Task<bool> ContainsContactAsync(MigrationNode? folder, Contact contact, CancellationToken cancellationToken = default);

    Task<Contact> ReadContactAsync(MigrationNode contact, CancellationToken cancellationToken = default);

    /// <summary>Imports a contact into <paramref name="folder"/>, or the default contact list when null.</summary>
    Task<MigrationNode> ImportContactAsync(MigrationNode? folder, Contact contact, CancellationToken cancellationToken = default);
}
