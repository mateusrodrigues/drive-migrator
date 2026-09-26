using DriveMigrator.App.ViewModels;
using DriveMigrator.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DriveMigrator.App;

internal static class AppHost
{
    public static IHost Build()
    {
        var builder = Host.CreateApplicationBuilder();

        // Cloud providers register here, e.g. builder.Services.AddCloudProvider<GoogleProvider>().
        builder.Services.TryAddSingleton<IProviderRegistry, ProviderRegistry>();

        builder.Services.AddTransient<MainWindowViewModel>();

        return builder.Build();
    }
}
