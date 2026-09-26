using System.Collections.Concurrent;
using System.Diagnostics;

namespace DriveMigrator.Engine;

/// <summary>
/// Live state of one job for the UI. Aggregates engine callbacks and raises throttled change notifications: at most
/// one per interval, but the last change in a burst is always delivered (shortly after it happens).
/// </summary>
public sealed class TransferJob : ITransferObserver
{
    private static readonly long NotifyInterval = Stopwatch.Frequency / 5;

    private static readonly TimeSpan TrailingDelay = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly Lock _notifyGate = new();
    private readonly ConcurrentDictionary<long, string> _inFlight = new();
    private long _lastNotify;
    private bool _trailingScheduled;
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

    /// <summary>Names of the items being worked on right now (several run in parallel), oldest first.</summary>
    public IReadOnlyList<string> CurrentItems => [.. _inFlight.OrderBy(kv => kv.Key).Select(kv => kv.Value)];

    /// <summary>The oldest item still in progress, or null when idle.</summary>
    public string? CurrentItem => CurrentItems is [var first, ..] ? first : null;

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
        _inFlight[item.Id] = item.Name;
        Notify();
    }

    void ITransferObserver.ItemFinished(ItemRecord item, ItemStatus status, string? message)
    {
        _inFlight.TryRemove(item.Id, out _);
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
            // Cancelled items never report finishing.
            _inFlight.Clear();
        }

        Notify(force: true);
    }

    private void Notify(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_notifyGate)
        {
            if (!force && now - _lastNotify < NotifyInterval)
            {
                // Too soon: make sure this state still reaches the UI once the burst is over.
                if (!_trailingScheduled)
                {
                    _trailingScheduled = true;
                    _ = Task.Delay(TrailingDelay).ContinueWith(_ => NotifyTrailing(), TaskScheduler.Default);
                }

                return;
            }

            _lastNotify = now;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyTrailing()
    {
        lock (_notifyGate)
        {
            _trailingScheduled = false;
        }

        Notify(force: true);
    }
}
