using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Transfers;

namespace DriveMigrator.Engine;

/// <summary>
/// Starts, pauses, resumes and removes transfer jobs, keeping them in the <see cref="TransferStore"/> so they
/// survive restarts. Jobs that were running when the app closed come back paused.
/// </summary>
public sealed class TransferManager(TransferStore store, AccountManager accounts, int parallelism = 4) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<TransferJob> _jobs = [];
    private readonly TransferEngine _engine = new(store, parallelism);

    /// <summary>Raised when jobs are added or removed. Individual progress is on <see cref="TransferJob.Changed"/>.</summary>
    public event EventHandler? JobsChanged;

    public IReadOnlyList<TransferJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs];
            }
        }
    }

    public void Load()
    {
        foreach (var record in store.LoadJobs())
        {
            var status = record.Status == JobStatus.Running ? JobStatus.Paused : record.Status;
            if (status != record.Status)
            {
                store.SetJobStatus(record.Id, status);
            }

            lock (_gate)
            {
                _jobs.Add(new TransferJob(record with { Status = status }, store.GetCounts(record.Id)));
            }
        }

        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public TransferJob Start(TransferRequest request, string title)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = request.Items.Select(i => new NewItem(
            i.Kind,
            i.Node,
            request.GetTarget(i.Kind)?.Container,
            i.Node?.Name ?? request.Source.GetCapability(i.Kind)?.DisplayName ?? i.Kind.ToString()));
        var record = store.CreateJob(title, Ref(request.Source.Account), Ref(request.Destination.Account), request.Options, request.Targets, items);
        var job = new TransferJob(record, store.GetCounts(record.Id));

        lock (_gate)
        {
            _jobs.Add(job);
        }

        JobsChanged?.Invoke(this, EventArgs.Empty);
        Run(job, request.Source, request.Destination);
        return job;
    }

    /// <summary>Stops a running job after the items in flight; it can be resumed later.</summary>
    public void Pause(TransferJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            if (!_jobs.Contains(job))
            {
                return;
            }
        }

        job.Cancellation?.Cancel();
    }

    /// <summary>Continues a paused job. Needs both of its accounts to be connected.</summary>
    public void Resume(TransferJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Status == JobStatus.Running)
        {
            return;
        }

        var source = FindSession(job.Record.Source);
        var destination = FindSession(job.Record.Destination);
        if (source is null || destination is null)
        {
            job.SetStatus(job.Status, "Reconnect both accounts of this transfer in Settings, then resume.");
            return;
        }

        Run(job, source, destination);
    }

    /// <summary>Marks failed items as pending again and resumes the job.</summary>
    public void RetryFailed(TransferJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Status == JobStatus.Running)
        {
            return;
        }

        store.ResetFailed(job.Id);
        job.SetCounts(store.GetCounts(job.Id));
        Resume(job);
    }

    /// <summary>Forgets a job (stopping it first). Copied data stays where it is.</summary>
    public async Task RemoveAsync(TransferJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var running = job.Cancellation;
        running?.Cancel();
        while (job.Status == JobStatus.Running)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        store.DeleteJob(job.Id);
        lock (_gate)
        {
            _jobs.Remove(job);
        }

        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ItemRecord> GetFailures(TransferJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return store.LoadItems(job.Id, ItemStatus.Failed);
    }

    public void Dispose()
    {
        foreach (var job in Jobs)
        {
            job.Cancellation?.Cancel();
        }
    }

    private void Run(TransferJob job, IAccountSession source, IAccountSession destination)
    {
        var cancellation = new CancellationTokenSource();
        job.Cancellation = cancellation;
        store.SetJobStatus(job.Id, JobStatus.Running);
        job.SetCounts(store.GetCounts(job.Id));
        job.SetStatus(JobStatus.Running);

        _ = Task.Run(async () =>
        {
            JobStatus final;
            string? problem = null;
            try
            {
                await _engine.RunAsync(job.Record, source, destination, job, cancellation.Token).ConfigureAwait(false);
                var counts = store.GetCounts(job.Id);
                job.SetCounts(counts);
                final = counts.Failed > 0 ? JobStatus.CompletedWithErrors : JobStatus.Completed;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                final = JobStatus.Paused;
            }
#pragma warning disable CA1031 // Unexpected engine failures pause the job with the message shown, instead of crashing the app.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                final = JobStatus.Paused;
                problem = $"Transfer stopped: {ex.Message}";
            }

            store.SetJobStatus(job.Id, final);
            job.SetCounts(store.GetCounts(job.Id));
            job.Cancellation = null;
            cancellation.Dispose();
            job.SetStatus(final, problem);
        });
    }

    private IAccountSession? FindSession(AccountRef account)
        => accounts.Accounts.FirstOrDefault(a =>
            string.Equals(a.Info.ProviderId, account.ProviderId, StringComparison.OrdinalIgnoreCase)
            && a.Info.AccountId == account.AccountId)?.Session;

    private static AccountRef Ref(AccountInfo account) => new(account.ProviderId, account.AccountId);
}
