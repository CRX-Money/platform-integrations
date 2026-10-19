using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

/// <summary>
/// Adds the Open Finance Brasil plugin's adapters to a host: <c>services.AddOpenFinanceBrasil().AddPluggy();</c>.
/// Each adapter registers its own <see cref="Abstractions.Plugins.IIntegrationPlugin"/>.
/// </summary>
public sealed class OpenFinanceBrasilBuilder
{
    internal OpenFinanceBrasilBuilder(IServiceCollection services) => Services = services;

    /// <summary>The host's services.</summary>
    public IServiceCollection Services { get; }
}

/// <summary>Registers the Open Finance Brasil plugin with a host's service collection.</summary>
public static class OpenFinanceBrasilServiceCollectionExtensions
{
    /// <summary>Starts the plugin's registration; add each adapter the host uses on the returned builder.</summary>
    /// <param name="services">The host's services.</param>
    public static OpenFinanceBrasilBuilder AddOpenFinanceBrasil(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        return new OpenFinanceBrasilBuilder(services);
    }
}
