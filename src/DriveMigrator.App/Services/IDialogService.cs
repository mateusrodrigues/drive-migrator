namespace DriveMigrator.App.Services;

public interface IDialogService
{
    Task ShowSettingsAsync();

    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    Task ShowMessageAsync(string title, string message);
}
