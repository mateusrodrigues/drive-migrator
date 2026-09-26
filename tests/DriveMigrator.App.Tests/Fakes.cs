using DriveMigrator.App.Services;

namespace DriveMigrator.App.Tests;

internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public List<string> Confirmations { get; } = [];

    public Task ShowSettingsAsync() => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add(message);
        return Task.FromResult(ConfirmResult);
    }
}
