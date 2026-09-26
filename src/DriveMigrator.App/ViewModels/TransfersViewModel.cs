using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMigrator.App.Services;
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

    public ObservableCollection<string> Failures { get; } = [];

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(ResumeCommand), nameof(RetryFailedCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    public partial bool HasFailures { get; private set; }

    [ObservableProperty]
    public partial bool ShowFailures { get; set; }

    public void Dispose() => Job.Changed -= OnJobChanged;

    internal void Refresh()
    {
        var counts = Job.Counts;
        IsRunning = Job.Status == JobStatus.Running;
        IsPaused = Job.Status == JobStatus.Paused;
        HasFailures = counts.Failed > 0;
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

        if (ShowFailures)
        {
            LoadFailures();
        }
    }

    partial void OnShowFailuresChanged(bool value)
    {
        if (value)
        {
            LoadFailures();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Pause() => _manager.Pause(Job);

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void Resume() => _manager.Resume(Job);

    private bool CanRetry() => HasFailures && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void RetryFailed()
    {
        ShowFailures = false;
        Failures.Clear();
        _manager.RetryFailed(Job);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Job.Status is JobStatus.Running or JobStatus.Paused
            && !await _dialogs.ConfirmAsync("Remove transfer", $"Stop and forget \"{Title}\"? Items already copied stay where they are.", "Remove"))
        {
            return;
        }

        await _manager.RemoveAsync(Job);
    }

    private void LoadFailures()
    {
        Failures.Clear();
        foreach (var item in _manager.GetFailures(Job))
        {
            Failures.Add($"{item.Name}: {item.Error}");
        }
    }

    private void OnJobChanged(object? sender, EventArgs e) => OnUiThread(Refresh);
}
