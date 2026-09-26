namespace DriveMigrator.App.Services;

public interface IDialogService
{
    Task ShowSettingsAsync();

    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    Task ShowMessageAsync(string title, string message);

    Task CopyToClipboardAsync(string text);

    /// <summary>Shows the per-transfer options; true when the user chose to start.</summary>
    Task<bool> ShowTransferOptionsAsync(ViewModels.TransferOptionsViewModel options);
}
