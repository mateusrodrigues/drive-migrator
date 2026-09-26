using DriveMigrator.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DriveMigrator.Providers.Google;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGoogleProvider(this IServiceCollection services)
        => services.AddCloudProvider<GoogleCloudProvider>();
}
