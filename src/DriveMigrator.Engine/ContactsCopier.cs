using DriveMigrator.Core;
using DriveMigrator.Core.Contacts;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.Engine;

/// <summary>Copies one contact: read, skip if the same person is already there, import.</summary>
internal static class ContactsCopier
{
    public static async Task<ItemOutcome> CopyAsync(
        IContactsCapability source,
        IContactsCapability destination,
        MigrationNode contactNode,
        MigrationNode? targetFolder,
        TransferOptions options,
        CancellationToken cancellationToken)
    {
        // A null folder means the destination's default contact list.
        var contact = await source.ReadContactAsync(contactNode, cancellationToken).ConfigureAwait(false);
        if (options.SkipDuplicates && await destination.ContainsContactAsync(targetFolder, contact, cancellationToken).ConfigureAwait(false))
        {
            return ItemOutcome.Skipped("Skipped: a contact with the same email (or name) is already there.");
        }

        var imported = await destination.ImportContactAsync(targetFolder, contact, cancellationToken).ConfigureAwait(false);
        return ItemOutcome.Done(imported, 0);
    }
}
