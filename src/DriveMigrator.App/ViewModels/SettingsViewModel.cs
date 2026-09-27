using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMigrator.App.Services;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;

namespace DriveMigrator.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AccountManager _accounts;
    private readonly IProviderRegistry _providers;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _signIn;

    public SettingsViewModel(
        AccountManager accounts,
        IProviderRegistry providers,
        ProviderCredentialStore credentials,
        ISecretStore secrets,
        IDialogService dialogs)
    {
        _accounts = accounts;
        _providers = providers;
        _dialogs = dialogs;
        IsSecretStorageProtected = secrets.IsProtected;

        foreach (var provider in providers.Providers)
        {
            Credentials.Add(new ProviderCredentialsViewModel(provider, credentials, OnCredentialsSavedAsync));
            AddAccountOptions.Add(new AddAccountOptionViewModel(provider, AddAccountAsync));
        }

        _accounts.AccountsChanged += OnAccountsChanged;
        RefreshAccounts();
    }

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    public ObservableCollection<AddAccountOptionViewModel> AddAccountOptions { get; } = [];

    public ObservableCollection<ProviderCredentialsViewModel> Credentials { get; } = [];

    public bool IsSecretStorageProtected { get; }

    public bool HasAccounts => Accounts.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string? BusyMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>0 = Accounts tab, 1 = Credentials tab.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Credentials.Select(c => c.LoadAsync()));
        RefreshAddOptions();

        // Nothing can be connected until the user has entered their own client IDs.
        if (!HasAccounts && Credentials.All(c => !c.IsConfigured))
        {
            SelectedTab = 1;
        }
    }

    public void Dispose()
    {
        _accounts.AccountsChanged -= OnAccountsChanged;

        // Closing Settings abandons any sign-in still waiting on the browser.
        _signIn?.Cancel();
    }

    [RelayCommand]
    private void CancelSignIn() => _signIn?.Cancel();

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private Task AddAccountAsync(ICloudProvider provider)
        => RunSignInAsync(
            $"Finish signing in to {provider.DisplayName} in your browser…",
            ct => _accounts.AddAccountAsync(provider.Id, ct));

    private Task ReauthorizeAsync(AccountItemViewModel item)
        => RunSignInAsync(
            $"Sign in again as {item.Email ?? item.DisplayName} in your browser…",
            ct => _accounts.ReauthorizeAsync(item.Info, ct));

    private async Task RemoveAsync(AccountItemViewModel item)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Remove account",
            $"Remove {item.Email ?? item.DisplayName} ({item.ProviderName})? Its saved sign-in will be deleted from this computer. No data in the account is changed.",
            "Remove",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        await RunAsync("Removing account…", ct => _accounts.RemoveAsync(item.Info, ct));
    }

    private async Task OnCredentialsSavedAsync(ProviderCredentialsViewModel _)
    {
        RefreshAddOptions();

        // Changed client IDs invalidate saved sign-ins; re-check every account so the list shows it.
        await RunAsync("Checking accounts…", _accounts.LoadAsync);
    }

    private Task RunSignInAsync(string message, Func<CancellationToken, Task> signIn)
        => RunAsync(message, signIn, cancellable: true);

    private async Task RunAsync(string message, Func<CancellationToken, Task> action, bool cancellable = false)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        BusyMessage = message;
        IsBusy = true;
        using var cts = new CancellationTokenSource();
        _signIn = cancellable ? cts : null;
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The user cancelled.
        }
        catch (ProviderNotConfiguredException ex)
        {
            ErrorMessage = ex.Message;
            SelectedTab = 1;
        }
#pragma warning disable CA1031 // Sign-in can fail in many provider-specific ways; all are reported to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            _signIn = null;
            IsBusy = false;
            BusyMessage = null;
        }
    }

    private void OnAccountsChanged(object? sender, EventArgs e) => OnUiThread(RefreshAccounts);

    private void RefreshAccounts()
    {
        Accounts.Clear();
        foreach (var account in _accounts.Accounts)
        {
            var providerName = _providers.Providers.FirstOrDefault(p => p.Id == account.Info.ProviderId)?.DisplayName ?? account.Info.ProviderId;
            Accounts.Add(new AccountItemViewModel(account, providerName, ReauthorizeAsync, RemoveAsync));
        }

        OnPropertyChanged(nameof(HasAccounts));
    }

    private void RefreshAddOptions()
    {
        foreach (var option in AddAccountOptions)
        {
            option.IsConfigured = Credentials.First(c => c.Provider == option.Provider).IsConfigured;
        }
    }
}

public sealed partial class AddAccountOptionViewModel(ICloudProvider provider, Func<ICloudProvider, Task> add) : ViewModelBase
{
    public ICloudProvider Provider { get; } = provider;

    public string Label => $"Add {Provider.DisplayName} account";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial bool IsConfigured { get; set; }

    public string Hint => IsConfigured ? string.Empty : $"Enter {Provider.DisplayName} credentials first.";

    [RelayCommand]
    private Task AddAsync() => add(Provider);
}
