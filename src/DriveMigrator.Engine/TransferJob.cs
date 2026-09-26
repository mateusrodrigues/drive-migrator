using System.Diagnostics;

namespace DriveMigrator.Engine;

/// <summary>Live state of one job for the UI. Aggregates engine callbacks and raises throttled change notifications.</summary>
public sealed class TransferJob : ITransferObserver
{
    private static readonly long NotifyInterval = Stopwatch.Frequency / 5;

    private readonly Lock _gate = new();
    private long _lastNotify;
    private int _pending;
    private int _done;
    private int _skipped;
    private int _failed;
    private long _bytes;

    internal TransferJob(JobRecord record, JobCounts counts)
    {
        Record = record;
        Status = record.Status;
        SetCounts(counts);
    }

    /// <summary>Raised (throttled, possibly off the UI thread) when progress or status changes.</summary>
    public event EventHandler? Changed;

    public JobRecord Record { get; }

    public long Id => Record.Id;

    public string Title => Record.Title;

    public JobStatus Status { get; private set; }

    /// <summary>Why the job can't run right now (e.g. an account needs reconnecting), if anything.</summary>
    public string? Problem { get; private set; }

    public string? CurrentItem { get; private set; }

    public JobCounts Counts
    {
        get
        {
            lock (_gate)
            {
                return new JobCounts(_pending, _done, _skipped, _failed, _bytes);
            }
        }
    }

    internal CancellationTokenSource? Cancellation { get; set; }

    internal Task? RunTask { get; set; }

    void ITransferObserver.ItemStarted(ItemRecord item)
    {
        CurrentItem = item.Name;
        Notify();
    }

    void ITransferObserver.ItemFinished(ItemRecord item, ItemStatus status, string? message)
    {
        lock (_gate)
        {
            _pending--;
            switch (status)
            {
                case ItemStatus.Done: _done++; break;
                case ItemStatus.Skipped: _skipped++; break;
                case ItemStatus.Failed: _failed++; break;
            }
        }

        Notify();
    }

    void ITransferObserver.ItemsDiscovered(int count)
    {
        lock (_gate)
        {
            _pending += count;
        }
    }

    void ITransferObserver.BytesTransferred(long delta)
    {
        Interlocked.Add(ref _bytes, delta);
        Notify();
    }

    internal void SetCounts(JobCounts counts)
    {
        lock (_gate)
        {
            (_pending, _done, _skipped, _failed, _bytes) = (counts.Pending, counts.Done, counts.Skipped, counts.Failed, counts.Bytes);
        }
    }

    internal void SetStatus(JobStatus status, string? problem = null)
    {
        Status = status;
        Problem = problem;
        if (status != JobStatus.Running)
        {
            CurrentItem = null;
        }

        Notify(force: true);
    }

    private void Notify(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastNotify);
        if (!force && now - last < NotifyInterval)
        {
            return;
        }

        if (force || Interlocked.CompareExchange(ref _lastNotify, now, last) == last)
        {
            Interlocked.Exchange(ref _lastNotify, now);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
