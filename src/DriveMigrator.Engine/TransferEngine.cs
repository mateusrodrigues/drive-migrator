using System.Threading.Channels;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Calendar;
using DriveMigrator.Core.Contacts;
using DriveMigrator.Core.Drive;
using DriveMigrator.Core.Mail;

namespace DriveMigrator.Engine;

/// <summary>Receives progress from a running job. Called from worker threads.</summary>
public interface ITransferObserver
{
    void ItemStarted(ItemRecord item);

    void ItemFinished(ItemRecord item, ItemStatus status, string? message);

    void ItemsDiscovered(int count);

    void BytesTransferred(long delta);
}

/// <summary>
/// Runs one job's pending items with a fixed number of parallel workers. Containers are created first and their
/// contents are discovered as they complete, so work starts immediately even for very large selections.
/// Cancelling leaves unfinished items pending in the store; running again resumes them.
/// </summary>
public sealed class TransferEngine(TransferStore store, int parallelism = 4)
{
    public async Task RunAsync(
        JobRecord job,
        IAccountSession source,
        IAccountSession destination,
        ITransferObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(observer);

        var pending = store.LoadPendingItems(job.Id);
        if (pending.Count == 0)
        {
            return;
        }

        var indexes = new Dictionary<CapabilityKind, TargetIndex>();
        foreach (var capability in destination.Capabilities)
        {
            indexes[capability.Kind] = new TargetIndex(capability);
        }

        var queue = Channel.CreateUnbounded<ItemRecord>();
        var outstanding = pending.Count;
        foreach (var item in pending)
        {
            queue.Writer.TryWrite(item);
        }

        var run = new Run(job, source, destination, indexes, observer);
        var workers = Enumerable.Range(0, parallelism).Select(_ => Task.Run(
            async () =>
            {
                await foreach (var item in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var discovered = await ProcessAsync(run, item, cancellationToken).ConfigureAwait(false);
                    foreach (var child in discovered)
                    {
                        Interlocked.Increment(ref outstanding);
                        queue.Writer.TryWrite(child);
                    }

                    if (Interlocked.Decrement(ref outstanding) == 0)
                    {
                        queue.Writer.TryComplete();
                    }
                }
            },
            cancellationToken));

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ItemRecord>> ProcessAsync(Run run, ItemRecord item, CancellationToken cancellationToken)
    {
        store.MarkInProgress(item.Id);
        run.Observer.ItemStarted(item);
        try
        {
            var sourceCapability = run.Source.GetCapability(item.Kind)
                ?? throw new InvalidOperationException($"{run.Source.Account.DisplayName} has no {item.Kind}.");
            var destinationCapability = run.Destination.GetCapability(item.Kind)
                ?? throw new InvalidOperationException($"{run.Destination.Account.DisplayName} has no {item.Kind}.");
            var index = run.Indexes[item.Kind];

            if (item.Source is null || item.Source.IsContainer)
            {
                var target = item.Source is null
                    ? item.TargetParent
                    : await EnsureContainerAsync(destinationCapability, index, item.TargetParent, item.Source, cancellationToken).ConfigureAwait(false);

                var children = new List<NewItem>();
                await foreach (var child in sourceCapability.GetChildrenAsync(item.Source, cancellationToken).ConfigureAwait(false))
                {
                    children.Add(new NewItem(item.Kind, child, target, child.Name));
                }

                var added = store.CompleteContainer(item, target, children);
                run.Observer.ItemsDiscovered(added.Count);
                run.Observer.ItemFinished(item, ItemStatus.Done, null);
                return added;
            }

            var progress = new DeltaProgress(run.Observer.BytesTransferred);
            var outcome = item.Kind switch
            {
                CapabilityKind.Drive => await DriveCopier.CopyAsync(
                    (IDriveCapability)sourceCapability,
                    (IDriveCapability)destinationCapability,
                    index,
                    item.Source,
                    item.TargetParent,
                    run.Job.Options,
                    progress,
                    cancellationToken).ConfigureAwait(false),
                CapabilityKind.Mail => await MailCopier.CopyAsync(
                    (IMailCapability)sourceCapability,
                    (IMailCapability)destinationCapability,
                    item.Source,
                    item.TargetParent,
                    run.Job.Options,
                    progress,
                    cancellationToken).ConfigureAwait(false),
                CapabilityKind.Contacts => await ContactsCopier.CopyAsync(
                    (IContactsCapability)sourceCapability,
                    (IContactsCapability)destinationCapability,
                    item.Source,
                    item.TargetParent,
                    run.Job.Options,
                    cancellationToken).ConfigureAwait(false),
                CapabilityKind.Calendar => await CalendarCopier.CopyAsync(
                    (ICalendarCapability)sourceCapability,
                    (ICalendarCapability)destinationCapability,
                    item.Source,
                    item.TargetParent,
                    run.Job.Options,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Copying {item.Kind.ToString().ToLowerInvariant()} is not supported yet."),
            };

            store.MarkFinished(item.Id, outcome.Status, outcome.Target, outcome.Message, outcome.Bytes);
            run.Observer.ItemFinished(item, outcome.Status, outcome.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Left in progress; the next run treats it as pending.
            throw;
        }
#pragma warning disable CA1031 // A failing item must not stop the job; the error is recorded for the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            store.MarkFinished(item.Id, ItemStatus.Failed, error: ex.Message, details: ex.ToString());
            run.Observer.ItemFinished(item, ItemStatus.Failed, ex.Message);
        }

        return [];
    }

    /// <summary>
    /// Finds the destination for a source container: at the top level a well-known container maps to its
    /// counterpart (Gmail "Sent" → Outlook "Sent Items", Google "All contacts" → Outlook "Contacts"); otherwise a
    /// same-named folder is reused (merged into) or created.
    /// </summary>
    private static async Task<MigrationNode> EnsureContainerAsync(
        ICapability destination,
        TargetIndex index,
        MigrationNode? parent,
        MigrationNode source,
        CancellationToken cancellationToken)
    {
        if (parent is null
            && source.Role is { } role
            && await destination.GetSpecialContainerAsync(role, cancellationToken).ConfigureAwait(false) is { } special)
        {
            return special;
        }

        var name = destination.ToValidName(source.Name);
        var claim = await index.ClaimAsync(parent, name, cancellationToken).ConfigureAwait(false);
        if (claim.Existing is { } existing)
        {
            if (existing.IsContainer)
            {
                return existing;
            }

            // A file already has this name; create the folder beside it under a new name.
            claim = await index.ClaimUniqueAsync(parent, name, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var created = await destination.CreateContainerAsync(parent, claim.Name, cancellationToken).ConfigureAwait(false);
            index.RegisterEmptyContainer(created);
            claim.Complete(created);
            return created;
        }
        catch
        {
            claim.Release();
            throw;
        }
    }

    private sealed record Run(
        JobRecord Job,
        IAccountSession Source,
        IAccountSession Destination,
        IReadOnlyDictionary<CapabilityKind, TargetIndex> Indexes,
        ITransferObserver Observer);

    /// <summary>Turns a provider's cumulative byte count for one file into deltas. Reports synchronously.</summary>
    private sealed class DeltaProgress(Action<long> report) : IProgress<long>
    {
        private long _last;

        public void Report(long value)
        {
            var delta = value - Interlocked.Exchange(ref _last, value);
            if (delta != 0)
            {
                report(delta);
            }
        }
    }
}
