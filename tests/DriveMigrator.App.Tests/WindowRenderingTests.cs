using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using DriveMigrator.App.ViewModels;
using DriveMigrator.App.Views;
using DriveMigrator.Core;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;
using DriveMigrator.Testing;

namespace DriveMigrator.App.Tests;

/// <summary>
/// Renders the windows headlessly to catch XAML and binding errors. Set DRIVEMIGRATOR_SCREENSHOTS to a directory
/// to also save the frames as PNGs for visual review.
/// </summary>
public sealed class WindowRenderingTests : IDisposable
{
    private readonly FakeCloudProvider _google = new("google", "Google")
    {
        CredentialFields = [new CredentialField("clientId", "OAuth client ID") { Help = "Google Cloud console → Credentials." }, new CredentialField("clientSecret", "OAuth client secret", IsSecret: true)],
    };

    private readonly FakeCloudProvider _microsoft = new("microsoft", "Microsoft")
    {
        CredentialFields = [new CredentialField("clientId", "Application (client) ID"), new CredentialField("tenant", "Tenant", IsRequired: false) { DefaultValue = "common" }],
    };

    private readonly InMemorySecretStore _secrets = new();
    private readonly InMemoryAccountStore _accountStore = new();
    private readonly AccountManager _accounts;

    public WindowRenderingTests()
    {
        _accounts = new AccountManager(new ProviderRegistry([_google, _microsoft]), _accountStore);
    }

    public void Dispose() => _accounts.Dispose();

    [AvaloniaFact]
    public async Task SettingsWindow_RendersBothTabs()
    {
        var credentials = new ProviderCredentialStore(_secrets);
        await credentials.SetAsync("microsoft", new Dictionary<string, string> { ["clientId"] = "11111111-2222-3333-4444-555555555555" }, TestContext.Current.CancellationToken);
        var ok = _microsoft.AddAccount("ada@contoso.com");
        var expired = _google.AddAccount("ada@gmail.com");
        _google.ExpireCredentials(expired.Account);
        _accountStore.Accounts = [ok.Account, expired.Account];
        await _accounts.LoadAsync(TestContext.Current.CancellationToken);

        using var vm = new SettingsViewModel(_accounts, new ProviderRegistry([_google, _microsoft]), credentials, _secrets, new FakeDialogService());
        await vm.InitializeAsync();
        var window = new SettingsWindow { DataContext = vm };
        window.Show();

        vm.SelectedTab = 0;
        Capture(window, "settings-accounts");
        vm.SelectedTab = 1;
        Capture(window, "settings-credentials");

        vm.ErrorMessage = "Sign-in failed: AADSTS50020 User account from identity provider does not exist in tenant.";
        vm.BusyMessage = "Finish signing in to Google in your browser…";
        vm.IsBusy = true;
        vm.SelectedTab = 0;
        Capture(window, "settings-busy-error");

        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Capture(window, "settings-busy-error-dark");
        Application.Current.RequestedThemeVariant = ThemeVariant.Default;
        window.Close();
    }

    [AvaloniaFact]
    public void MainWindow_Renders()
    {
        var vm = new MainWindowViewModel(_accounts, new FakeDialogService());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Capture(window, "main");
        window.Close();
    }

    private static void Capture(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var directory = Environment.GetEnvironmentVariable("DRIVEMIGRATOR_SCREENSHOTS");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }
}
