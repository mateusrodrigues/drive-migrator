using CommunityToolkit.Mvvm.Input;
using DriveMigrator.Core.Accounts;

namespace DriveMigrator.App.ViewModels;

public sealed partial class AccountItemViewModel(
    ConnectedAccount account,
    string providerName,
    Func<AccountItemViewModel, Task> reauthorize,
    Func<AccountItemViewModel, Task> remove) : ViewModelBase
{
    public AccountInfo Info => account.Info;

    public string ProviderName { get; } = providerName;

    public string DisplayName => account.Info.DisplayName;

    public string? Email => account.Info.Email;

    public bool IsConnected => account.Status == AccountStatus.Connected;

    public bool NeedsAttention => !IsConnected;

    public string StatusText => account.Status switch
    {
        AccountStatus.Connected => "Connected",
        AccountStatus.NeedsReauthorization => "Needs re-authorization",
        _ => "Error",
    };

    public string? Error => account.Error;

    [RelayCommand]
    private Task ReauthorizeAsync() => reauthorize(this);

    [RelayCommand]
    private Task RemoveAsync() => remove(this);
}
