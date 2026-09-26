using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DriveMigrator.App.ViewModels;
using DriveMigrator.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DriveMigrator.App.Services;

internal sealed class DialogService(IServiceProvider services) : IDialogService
{
    public async Task ShowSettingsAsync()
    {
        using var viewModel = services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow { DataContext = viewModel };
        window.Opened += async (_, _) => await viewModel.InitializeAsync();
        await window.ShowDialog(Owner());
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
        => new ConfirmDialog(title, message, confirmText).ShowDialog<bool>(Owner());

    public Task ShowMessageAsync(string title, string message)
        => new ConfirmDialog(title, message, "OK", showCancel: false).ShowDialog(Owner());

    private static Window Owner()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        return desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow!;
    }
}
