using DriveMigrator.App.Services;
using DriveMigrator.App.ViewModels;
using DriveMigrator.Engine;
using DriveMigrator.Infrastructure;
using DriveMigrator.Providers.Google;
using DriveMigrator.Providers.Microsoft;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DriveMigrator.App;

internal static class AppHost
{
    public static IHost Build()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddDriveMigratorInfrastructure();

        // Cloud providers. Adding a service means adding its project and one line here.
        builder.Services.AddGoogleProvider();
        builder.Services.AddMicrosoftProvider();

        builder.Services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<AppPaths>();
            AppPaths.EnsurePrivateDirectory(paths.DataDirectory);
            return TransferStore.Open(paths.TransferDatabase);
        });
        builder.Services.AddSingleton<TransferManager>();

        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        return builder.Build();
    }
}
