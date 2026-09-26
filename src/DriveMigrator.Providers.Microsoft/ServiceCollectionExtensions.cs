using DriveMigrator.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DriveMigrator.Providers.Microsoft;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMicrosoftProvider(this IServiceCollection services)
        => services.AddCloudProvider<MicrosoftCloudProvider>();
}
