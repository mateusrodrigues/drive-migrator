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
    public async Task MainWindow_RendersPanesAndTrees()
    {
        var source = _google.AddAccount("ada@gmail.com");
        source.Drive.DisplayName = "Google Drive";
        var taxes = source.Drive.AddContainer(null, "Taxes");
        source.Drive.AddFile(taxes, "2025 return.pdf", new byte[2_400_000]);
        source.Drive.AddNativeDocument(taxes, "Budget", "application/vnd.google-apps.spreadsheet",
            (new Core.ExportFormat("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx", "Excel"), [1]));
        source.Drive.AddContainer(null, "Photos");
        source.Drive.AddFile(null, "notes.txt", new byte[1200]);

        var target = _microsoft.AddAccount("ada@outlook.com");
        target.Drive.DisplayName = "OneDrive";
        target.Drive.AddContainer(null, "Documents");
        target.Drive.AddContainer(null, "Pictures");
        _accountStore.Accounts = [source.Account, target.Account];

        using var vm = new MainWindowViewModel(_accounts, new ProviderRegistry([_google, _microsoft]), new FakeDialogService());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.InitializeAsync();

        var left = vm.Left.Roots[0];
        left.IsExpanded = true;
        await left.LoadChildrenAsync();
        var taxesNode = left.Children[1];
        taxesNode.IsExpanded = true;
        await taxesNode.LoadChildrenAsync();
        taxesNode.Children[0].IsChecked = true;
        left.Children[0].IsChecked = true;

        var right = vm.Right.Roots[0];
        right.IsExpanded = true;
        await right.LoadChildrenAsync();
        vm.Right.SelectedNode = right.Children[0];

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
