using Avalonia.Headless.XUnit;
using DriveMigrator.App.ViewModels;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;
using DriveMigrator.Testing;

namespace DriveMigrator.App.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly FakeCloudProvider _google = new("google", "Google")
    {
        CredentialFields = [new CredentialField("clientId", "OAuth client ID"), new CredentialField("clientSecret", "OAuth client secret", IsSecret: true)],
    };

    private readonly InMemorySecretStore _secrets = new();
    private readonly ProviderCredentialStore _credentials;
    private readonly InMemoryAccountStore _accountStore = new();
    private readonly AccountManager _accounts;
    private readonly FakeDialogService _dialogs = new();

    public SettingsViewModelTests()
    {
        _credentials = new ProviderCredentialStore(_secrets);
        _accounts = new AccountManager(new ProviderRegistry([_google]), _accountStore);
    }

    public void Dispose() => _accounts.Dispose();

    [AvaloniaFact]
    public async Task Unconfigured_OpensCredentialsTabAndDisablesAdd()
    {
        using var vm = CreateViewModel();

        await vm.InitializeAsync();

        Assert.Equal(1, vm.SelectedTab);
        Assert.False(Assert.Single(vm.AddAccountOptions).IsConfigured);
    }

    [AvaloniaFact]
    public void CredentialsOfAProviderWithAGuide_OpenItFromTheForm()
    {
        using var vm = CreateViewModel();
        var google = Assert.Single(vm.Credentials);

        Assert.True(google.HasGuide);
        google.ShowGuideCommand.Execute(null);

        Assert.Equal(["google"], _dialogs.GuidesShown);
    }

    [AvaloniaFact]
    public void CredentialsOfAProviderWithoutAGuide_OfferNone()
    {
        var other = new FakeCloudProvider("other", "Other") { CredentialFields = [new CredentialField("clientId", "Client ID")] };
        using var vm = new SettingsViewModel(_accounts, new ProviderRegistry([other]), _credentials, _secrets, _dialogs);

        Assert.False(Assert.Single(vm.Credentials).HasGuide);
    }

    [AvaloniaFact]
    public async Task SavingCredentials_EnablesAddingAccounts()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        var google = Assert.Single(vm.Credentials);

        google.Fields[0].Value = "id.apps.googleusercontent.com";
        google.Fields[1].Value = "secret";
        await google.SaveCommand.ExecuteAsync(null);

        Assert.True(google.IsConfigured);
        Assert.True(vm.AddAccountOptions[0].IsConfigured);
        Assert.Equal("secret", (await _credentials.GetAsync("google", TestContext.Current.CancellationToken))["clientSecret"]);
    }

    [AvaloniaFact]
    public async Task AddAndRemoveAccount()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.AddAccountOptions[0].AddCommand.ExecuteAsync(null);
        var item = Assert.Single(vm.Accounts);
        Assert.True(item.IsConnected);
        Assert.False(vm.IsBusy);

        _dialogs.ConfirmResult = false;
        await item.RemoveCommand.ExecuteAsync(null);
        Assert.Single(vm.Accounts);

        _dialogs.ConfirmResult = true;
        await item.RemoveCommand.ExecuteAsync(null);
        Assert.Empty(vm.Accounts);
        Assert.Equal(2, _dialogs.Confirmations.Count);
    }

    [AvaloniaFact]
    public async Task FailedSignIn_ShowsError()
    {
        _google.OnSignIn = () => throw new InvalidOperationException("AADSTS50020: user does not exist in tenant");
        using var vm = CreateViewModel();

        await vm.AddAccountOptions[0].AddCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("AADSTS50020", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(vm.Accounts);

        vm.DismissErrorCommand.Execute(null);
        Assert.False(vm.HasError);
    }

    [AvaloniaFact]
    public async Task NotConfiguredSignIn_SwitchesToCredentialsTab()
    {
        _google.OnSignIn = () => throw new ProviderNotConfiguredException("Google needs a client ID.");
        using var vm = CreateViewModel();

        await vm.AddAccountOptions[0].AddCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.SelectedTab);
        Assert.Equal("Google needs a client ID.", vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task CancelledSignIn_IsSilent()
    {
        using var vm = CreateViewModel();
        _google.OnSignIn = () =>
        {
            vm.CancelSignInCommand.Execute(null);
            throw new OperationCanceledException();
        };

        await vm.AddAccountOptions[0].AddCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Reauthorize_FixesAccountNeedingAttention()
    {
        var session = _google.AddAccount("ada@example.com");
        _google.ExpireCredentials(session.Account);
        _accountStore.Accounts = [session.Account];
        await _accounts.LoadAsync(TestContext.Current.CancellationToken);
        using var vm = CreateViewModel();
        Assert.True(Assert.Single(vm.Accounts).NeedsAttention);

        await vm.Accounts[0].ReauthorizeCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(vm.Accounts).IsConnected);
    }

    private SettingsViewModel CreateViewModel()
        => new(_accounts, new ProviderRegistry([_google]), _credentials, _secrets, _dialogs);
}
