using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMigrator.App.Services;
using DriveMigrator.Core.Accounts;

namespace DriveMigrator.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AccountManager _accounts;
    private readonly IDialogService _dialogs;

    public MainWindowViewModel(AccountManager accounts, IDialogService dialogs)
    {
        _accounts = accounts;
        _dialogs = dialogs;
        _accounts.AccountsChanged += (_, _) => OnUiThread(UpdateStatus);
    }

    [ObservableProperty]
    public partial string Status { get; set; } = "Loading accounts…";

    [ObservableProperty]
    public partial bool NeedsAttention { get; set; }

    public async Task InitializeAsync()
    {
        try
        {
            await _accounts.LoadAsync();
        }
#pragma warning disable CA1031 // Start-up must not crash on a broken account list; show the problem instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = $"Could not load accounts: {ex.Message}";
            NeedsAttention = true;
        }
    }

    [RelayCommand]
    private Task OpenSettingsAsync() => _dialogs.ShowSettingsAsync();

    private void UpdateStatus()
    {
        var accounts = _accounts.Accounts;
        var broken = accounts.Count(a => a.Status != AccountStatus.Connected);
        NeedsAttention = broken > 0;
        Status = accounts.Count switch
        {
            0 => "No accounts connected. Open Settings to connect Google or Microsoft accounts.",
            _ when broken > 0 => $"{accounts.Count} account(s) connected; {broken} need attention in Settings.",
            _ => $"{accounts.Count} account(s) connected.",
        };
    }
}
