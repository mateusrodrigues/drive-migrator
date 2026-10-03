using DriveMigrator.App.Services;
using DriveMigrator.App.ViewModels;
using DriveMigrator.Core.Accounts;
using DriveMigrator.Engine;

namespace DriveMigrator.App.Tests;

internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public List<string> Confirmations { get; } = [];

    public Task ShowSettingsAsync() => Task.CompletedTask;

    public List<string> GuidesShown { get; } = [];

    public Task ShowSetupGuideAsync(string providerId)
    {
        GuidesShown.Add(providerId);
        return Task.CompletedTask;
    }

    public string? Clipboard { get; private set; }

    public Task CopyToClipboardAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }

    public List<(string Title, string Message)> Messages { get; } = [];

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }

    /// <summary>What the options dialog returns; set <see cref="ConfigureOptions"/> to change choices first.</summary>
    public bool StartTransfers { get; set; } = true;

    public Action<TransferOptionsViewModel>? ConfigureOptions { get; set; }

    public List<TransferOptionsViewModel> OptionsShown { get; } = [];

    public Task<bool> ShowTransferOptionsAsync(TransferOptionsViewModel options)
    {
        OptionsShown.Add(options);
        ConfigureOptions?.Invoke(options);
        return Task.FromResult(StartTransfers);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        Confirmations.Add(message);
        return Task.FromResult(ConfirmResult);
    }
}

/// <summary>A transfer manager over a throwaway SQLite database.</summary>
internal sealed class TempTransfers : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("drivemigrator-app-").FullName;

    public TempTransfers(AccountManager accounts)
    {
        Store = TransferStore.Open(Path.Combine(_directory, "transfers.db"));
        Manager = new TransferManager(Store, accounts);
    }

    public TransferStore Store { get; }

    public TransferManager Manager { get; }

    public void Dispose()
    {
        Manager.Dispose();
        Store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
