using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Providers.Br.Inter.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrxMoney.Integrations.Providers.Br.Inter;

/// <summary>Registers the Inter plugin with a host's service collection.</summary>
public static class InterServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="InterIntegrationPlugin"/> as an <see cref="IIntegrationPlugin"/>, plus the shared
    /// client registry that keeps certificates loaded, tokens cached and call budgets per Inter integration.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Optional changes to <see cref="InterIntegrationOptions"/>; the defaults follow Inter's published limits.</param>
    public static IServiceCollection AddInterIntegration(this IServiceCollection services, Action<InterIntegrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<InterIntegrationOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InterTransportFactory>();
        services.TryAddSingleton(sp => new InterClientRegistry(
            sp.GetRequiredService<IOptions<InterIntegrationOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<InterTransportFactory>()));

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IIntegrationPlugin, InterIntegrationPlugin>(sp => new InterIntegrationPlugin(
            sp.GetRequiredService<InterClientRegistry>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<InterIntegrationPlugin>>() ?? NullLogger<InterIntegrationPlugin>.Instance)));

        return services;
    }
}
