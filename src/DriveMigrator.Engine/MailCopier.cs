using DriveMigrator.Core;
using DriveMigrator.Core.Mail;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.Engine;

/// <summary>Copies one message: read, skip if already there (same Message-ID), import.</summary>
internal static class MailCopier
{
    public static async Task<ItemOutcome> CopyAsync(
        IMailCapability source,
        IMailCapability destination,
        MigrationNode message,
        MigrationNode? targetFolder,
        TransferOptions options,
        IProgress<long> progress,
        CancellationToken cancellationToken)
    {
        // Messages always live in a folder; loose messages copied to the top level go to the Inbox.
        var folder = targetFolder
            ?? await destination.GetSpecialFolderAsync(MailFolderRole.Inbox, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The destination mailbox has no Inbox; choose a destination folder.");

        var content = await source.ReadMessageAsync(message, cancellationToken).ConfigureAwait(false);
        await using (content.ConfigureAwait(false))
        {
            if (options.SkipExistingMessages
                && content.InternetMessageId is { Length: > 0 } messageId
                && await destination.ContainsMessageAsync(folder, messageId, cancellationToken).ConfigureAwait(false))
            {
                return ItemOutcome.Skipped("Skipped: this message is already in the destination folder.");
            }

            var imported = await destination.ImportMessageAsync(folder, content, cancellationToken).ConfigureAwait(false);
            var size = message.Size ?? imported.Size ?? 0;
            progress.Report(size);
            return ItemOutcome.Done(imported, size);
        }
    }
}
