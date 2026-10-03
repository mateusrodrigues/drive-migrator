namespace DriveMigrator.App.Services;

public interface IDialogService
{
    Task ShowSettingsAsync();

    /// <summary>Opens the guide for creating a provider's OAuth app, if it has one; it stays open beside the form.</summary>
    Task ShowSetupGuideAsync(string providerId);

    /// <summary>
    /// Asks a yes/no question. <paramref name="confirmText"/> names the action ("Remove"), never a bare "Yes";
    /// <paramref name="destructive"/> shows it as a danger button.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);

    Task ShowMessageAsync(string title, string message);

    Task CopyToClipboardAsync(string text);

    /// <summary>Shows the per-transfer options; true when the user chose to start.</summary>
    Task<bool> ShowTransferOptionsAsync(ViewModels.TransferOptionsViewModel options);
}
