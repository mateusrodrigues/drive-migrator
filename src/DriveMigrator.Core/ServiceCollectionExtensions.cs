using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DriveMigrator.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCloudProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, ICloudProvider
    {
        services.AddSingleton<ICloudProvider, TProvider>();
        services.TryAddSingleton<IProviderRegistry, ProviderRegistry>();
        return services;
    }
}
