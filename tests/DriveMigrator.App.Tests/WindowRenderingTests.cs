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

        source.Mail.DisplayName = "Gmail";
        var inbox = source.Mail.AddSpecialFolder(Core.ContainerRole.Inbox, "Inbox");
        source.Mail.AddSpecialFolder(Core.ContainerRole.Sent, "Sent");
        source.Mail.AddContainer(null, "Receipts");

        var target = _microsoft.AddAccount("ada@outlook.com");
        target.Drive.DisplayName = "OneDrive";
        target.Mail.DisplayName = "Outlook Mail";
        target.Drive.AddContainer(null, "Documents");
        target.Drive.AddContainer(null, "Pictures");
        _accountStore.Accounts = [source.Account, target.Account];

        using var transfers = new TempTransfers(_accounts);
        using var vm = new MainWindowViewModel(_accounts, new ProviderRegistry([_google, _microsoft]), new FakeDialogService(), transfers.Manager);
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

        // Mail tree, collapsed drive.
        left.IsExpanded = false;
        var mail = vm.Left.Roots.Single(r => r.Name == "Gmail");
        mail.IsExpanded = true;
        await mail.LoadChildrenAsync();
        source.Mail.AddMessage(inbox, "Flight confirmation", [1]);
        source.Mail.AddMessage(inbox, "Team lunch on Friday", [1]);
        var inboxNode = mail.Children[0];
        inboxNode.IsExpanded = true;
        await inboxNode.LoadChildrenAsync();
        inboxNode.Children[0].IsChecked = true;
        Capture(window, "main-mail");

        // Contacts options dialog.
        target.Contacts.DisplayName = "Outlook Contacts";
        var contactsRequest = new Core.Transfers.TransferRequest(source, target, [new(Core.CapabilityKind.Contacts, null)], [new(Core.CapabilityKind.Contacts, null)]);
        var optionsWindow = new TransferOptionsWindow { DataContext = new TransferOptionsViewModel("From ada@gmail.com · Google to ada@outlook.com · Microsoft\n\n• Google Contacts: everything → Outlook Contacts", contactsRequest) };
        optionsWindow.Show();
        Capture(optionsWindow, "transfer-options-contacts");
        optionsWindow.Close();

        // A finished job with one failure, and a paused one, in the transfers panel.
        target.Drive.OnUpload = (name, _) => name.StartsWith("2025", StringComparison.Ordinal) ? throw new IOException("The service is unavailable.") : Task.CompletedTask;
        var request = MainWindowViewModel.BuildRequest(vm.Left, vm.Right, out _)!;
        var done = transfers.Manager.Start(request, MainWindowViewModel.TitleFor(vm.Left, vm.Right, request));
        while (done.Status == Engine.JobStatus.Running)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        vm.Transfers.Sync();
        vm.Transfers.Jobs[0].Refresh();
        vm.Transfers.Jobs[0].ShowFailures = true;
        Capture(window, "main-transfers");
        window.Close();
    }

    [AvaloniaFact]
    public void TransferOptionsWindow_Renders()
    {
        var source = _google.AddAccount("ada@gmail.com");
        source.Drive.DisplayName = "Google Drive";
        source.Drive.NativeDocumentTypes =
        [
            new("g/doc", "Google Docs", [new("a", ".docx", "Word document"), new("b", ".pdf", "PDF")]),
            new("g/sheet", "Google Sheets", [new("c", ".xlsx", "Excel workbook"), new("b", ".pdf", "PDF")]),
        ];
        var target = _microsoft.AddAccount("ada@outlook.com");
        target.Drive.CanConvertToNativeFormat = true;
        target.Drive.DisplayName = "OneDrive";
        var request = new Core.Transfers.TransferRequest(source, target, [new(Core.CapabilityKind.Drive, null)], [new(Core.CapabilityKind.Drive, null)]);

        var vm = new TransferOptionsViewModel("From ada@gmail.com · Google to ada@outlook.com · Microsoft\n\n• Google Drive: 2 folders, 3 files → OneDrive / Documents", request);
        vm.NativeDocuments[1].Selected = vm.NativeDocuments[1].Choices[^1];
        vm.ConflictKeepBoth = true;
        var options = vm.ToOptions();

        Assert.Equal(Core.Transfers.ConflictPolicy.KeepBoth, options.Conflicts);
        Assert.Equal(".docx", options.NativeExports["g/doc"]!.FileExtension);
        Assert.Null(options.NativeExports["g/sheet"]);
        Assert.False(vm.ConflictSkip);

        var window = new TransferOptionsWindow { DataContext = vm };
        window.Show();
        Capture(window, "transfer-options");
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
