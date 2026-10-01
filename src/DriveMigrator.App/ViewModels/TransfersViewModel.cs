using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMigrator.App.Services;
using DriveMigrator.Core.Transfers;
using DriveMigrator.Engine;

namespace DriveMigrator.App.ViewModels;

/// <summary>The transfers panel: one row per job, newest first.</summary>
public sealed class TransfersViewModel : ViewModelBase, IDisposable
{
    private readonly TransferManager _manager;
    private readonly IDialogService _dialogs;

    public TransfersViewModel(TransferManager manager, IDialogService dialogs)
    {
        _manager = manager;
        _dialogs = dialogs;
        _manager.JobsChanged += OnJobsChanged;
    }

    public ObservableCollection<TransferJobViewModel> Jobs { get; } = [];

    public bool HasJobs => Jobs.Count > 0;

    public void Dispose()
    {
        _manager.JobsChanged -= OnJobsChanged;
        foreach (var job in Jobs)
        {
            job.Dispose();
        }
    }

    internal void Sync()
    {
        var current = _manager.Jobs;
        foreach (var stale in Jobs.Where(vm => !current.Contains(vm.Job)).ToList())
        {
            stale.Dispose();
            Jobs.Remove(stale);
        }

        foreach (var job in current.Where(j => Jobs.All(vm => vm.Job != j)))
        {
            Jobs.Insert(0, new TransferJobViewModel(job, _manager, _dialogs));
        }

        OnPropertyChanged(nameof(HasJobs));
    }

    private void OnJobsChanged(object? sender, EventArgs e) => OnUiThread(Sync);
}

public sealed partial class TransferJobViewModel : ViewModelBase, IDisposable
{
    private readonly TransferManager _manager;
    private readonly IDialogService _dialogs;
    private (int Failed, JobStatus Status)? _loaded;

    public TransferJobViewModel(TransferJob job, TransferManager manager, IDialogService dialogs)
    {
        Job = job;
        _manager = manager;
        _dialogs = dialogs;
        Job.Changed += OnJobChanged;
        Refresh();
    }

    public TransferJob Job { get; }

    public string Title => Job.Title;

    /// <summary>Items that failed with an ordinary error.</summary>
    public ObservableCollection<TransferFailureLine> Failures { get; } = [];

    /// <summary>Copies that don't match their original's checksum.</summary>
    public ObservableCollection<TransferFailureLine> Mismatches { get; } = [];

    /// <summary>Files that differ from the same-named file already in the destination.</summary>
    public ObservableCollection<TransferFailureLine> Differences { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    /// <summary>"Working on big.iso and 2 others", or null when idle.</summary>
    [ObservableProperty]
    public partial string? WorkingOn { get; private set; }

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; }

    /// <summary>Running or paused: still has a progress bar. Finished jobs show only their status line.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(ResumeCommand), nameof(RetryFailedCommand))]
    [NotifyPropertyChangedFor(nameof(CanRetryFailed), nameof(ShowMismatches), nameof(ShowDifferences))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    [NotifyPropertyChangedFor(nameof(CanRetryFailed))]
    public partial bool HasFailures { get; private set; }

    /// <summary>Some items failed and the job isn't running, so they can be tried again.</summary>
    public bool CanRetryFailed => HasFailures && !IsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMismatches))]
    public partial string? MismatchSummary { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDifferences))]
    public partial string? DifferencesSummary { get; private set; }

    /// <summary>Damaged copies are listed once the job has stopped, with the action to copy them again.</summary>
    public bool ShowMismatches => MismatchSummary is not null && !IsRunning;

    /// <summary>Files that differ from the destination's are listed once the job has stopped, for the user to decide.</summary>
    public bool ShowDifferences => DifferencesSummary is not null && !IsRunning;

    [ObservableProperty]
    public partial bool ShowFailures { get; set; }

    public void Dispose() => Job.Changed -= OnJobChanged;

    internal void Refresh()
    {
        var counts = Job.Counts;
        IsRunning = Job.Status == JobStatus.Running;
        IsPaused = Job.Status == JobStatus.Paused;
        IsActive = IsRunning || IsPaused;
        Problem = Job.Problem;
        var current = IsRunning ? Job.CurrentItems : [];
        WorkingOn = current.Count switch
        {
            0 => null,
            1 => $"Working on {current[0]}",
            2 => $"Working on {current[0]} and 1 other",
            _ => $"Working on {current[0]} and {current.Count - 1} others",
        };
        StatusText = Job.Status switch
        {
            JobStatus.Running => "Copying…",
            JobStatus.Paused => "Paused",
            JobStatus.Completed => "Done",
            _ => "Done with errors",
        };

        IsIndeterminate = IsRunning && counts.Total == 0;
        Progress = counts.Total == 0 ? 0 : 100.0 * counts.Finished / counts.Total;

        var parts = new List<string> { $"{counts.Finished} of {counts.Total} items" };
        if (counts.Skipped > 0)
        {
            parts.Add($"{counts.Skipped} skipped");
        }

        if (counts.Failed > 0)
        {
            parts.Add($"{counts.Failed} failed");
        }

        parts.Add(NodeViewModel.FormatSize(counts.Bytes));
        Details = string.Join(" · ", parts);

        if (_loaded != (counts.Failed, Job.Status))
        {
            _loaded = (counts.Failed, Job.Status);
            LoadFailures(counts.Failed);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Pause() => _manager.Pause(Job);

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void Resume() => _manager.Resume(Job);

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private void RetryFailed()
    {
        ShowFailures = false;
        _manager.RetryFailed(Job);
    }

    /// <summary>Copies damaged files again, overwriting their copies in the destination.</summary>
    [RelayCommand]
    private async Task CopyMismatchesAgainAsync()
    {
        var count = Mismatches.Count;
        var files = count == 1 ? "the file that didn't match its checksum" : $"the {count} files that didn't match their checksums";
        if (await _dialogs.ConfirmAsync(
            "Copy again",
            $"Copy {files} again? Their copies in the destination are taken to be damaged and will be overwritten.",
            "Copy again and overwrite",
            destructive: true))
        {
            _manager.RetryMismatched(Job, FailureKind.ChecksumMismatch, ConflictPolicy.Overwrite);
        }
    }

    /// <summary>Replaces the destination's different files with the source's.</summary>
    [RelayCommand]
    private async Task OverwriteDifferencesAsync()
    {
        var count = Differences.Count;
        var files = count == 1 ? "the file that is different" : $"the {count} files that are different";
        if (await _dialogs.ConfirmAsync(
            "Overwrite files",
            $"Replace {files} in the destination with the source's version? What is in the destination now will be lost.",
            "Overwrite",
            destructive: true))
        {
            _manager.RetryMismatched(Job, FailureKind.DiffersFromExisting, ConflictPolicy.Overwrite);
        }
    }

    /// <summary>Copies the source's version of the different files beside the destination's, as "name (1)".</summary>
    [RelayCommand]
    private void KeepBothDifferences() => _manager.RetryMismatched(Job, FailureKind.DiffersFromExisting, ConflictPolicy.KeepBoth);

    /// <summary>Leaves the destination's version of the different files.</summary>
    [RelayCommand]
    private void KeepDestinationDifferences() => _manager.KeepDestinationVersions(Job, FailureKind.DiffersFromExisting);

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Job.Status is JobStatus.Running or JobStatus.Paused
            && !await _dialogs.ConfirmAsync("Remove transfer", $"Stop and forget \"{Title}\"? Items already copied stay where they are.", "Remove", destructive: true))
        {
            return;
        }

        await _manager.RemoveAsync(Job);
    }

    /// <summary>Copies every failure with its full technical details, for bug reports.</summary>
    [RelayCommand]
    private async Task CopyErrorDetailsAsync()
    {
        var failures = _manager.GetFailures(Job);
        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            failures.Select(f => $"{f.Kind} item \"{f.Name}\" ({f.Source?.MimeType ?? f.Source?.Kind.ToString()}):{Environment.NewLine}{f.Details ?? f.Error}"));
        await _dialogs.CopyToClipboardAsync($"Drive Migrator transfer \"{Title}\"{Environment.NewLine}{Environment.NewLine}{text}");
        CopiedDetails = true;
    }

    [ObservableProperty]
    public partial bool CopiedDetails { get; private set; }

    /// <summary>Reads the failed items from the store, split by kind. Only when the failure count or status changes.</summary>
    private void LoadFailures(int failed)
    {
        Failures.Clear();
        Mismatches.Clear();
        Differences.Clear();
        foreach (var item in failed == 0 ? [] : _manager.GetFailures(Job))
        {
            var list = item.Failure switch
            {
                FailureKind.ChecksumMismatch => Mismatches,
                FailureKind.DiffersFromExisting => Differences,
                _ => Failures,
            };
            list.Add(new TransferFailureLine(item.Name, item.Error));
        }

        HasFailures = Failures.Count > 0;
        MismatchSummary = Mismatches.Count switch
        {
            0 => null,
            1 => "1 file didn't match its checksum. Its copy in the destination is probably damaged.",
            var n => $"{n} files didn't match their checksums. Their copies in the destination are probably damaged.",
        };
        DifferencesSummary = Differences.Count switch
        {
            0 => null,
            1 => "1 file is different from the file with its name already in the destination.",
            var n => $"{n} files are different from the files with their names already in the destination.",
        };
    }

    private void OnJobChanged(object? sender, EventArgs e) => OnUiThread(Refresh);
}

/// <summary>One failed item in a job's error list: its name, and what went wrong with it.</summary>
public sealed record TransferFailureLine(string Name, string? Message);
