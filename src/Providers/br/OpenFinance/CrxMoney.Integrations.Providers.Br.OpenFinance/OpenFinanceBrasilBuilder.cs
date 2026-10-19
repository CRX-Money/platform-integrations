using CrxMoney.Integrations.Abstractions.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

/// <summary>
/// The Open Finance Brasil plugin: one <see cref="IIntegrationPlugin"/> for the provider, with an adapter per
/// aggregator. A sync runs on the adapter its request names (<see cref="Abstractions.Sync.IntegrationSyncRequest.AdapterId"/>).
/// </summary>
public sealed class OpenFinanceBrasilIntegrationPlugin : OpenFinanceIntegrationPlugin
{
    /// <summary>The provider id; equals the platform's <c>Integration.Provider</c> name.</summary>
    public const string Id = "Open Finance Brasil";

    internal OpenFinanceBrasilIntegrationPlugin(IEnumerable<IOpenFinanceSource> adapters, TimeProvider timeProvider, ILogger<OpenFinanceBrasilIntegrationPlugin> logger)
        : base(adapters, timeProvider, logger)
    {
    }

    /// <inheritdoc />
    public override string ProviderId => Id;

    /// <inheritdoc />
    public override string DisplayName => "Open Finance Brasil";
}

/// <summary>
/// Adds the Open Finance Brasil plugin's adapters to a host: <c>services.AddOpenFinanceBrasil().AddPluggy();</c>.
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
    /// <summary>
    /// Registers <see cref="OpenFinanceBrasilIntegrationPlugin"/> as an <see cref="IIntegrationPlugin"/>; add each
    /// adapter the host uses on the returned builder. The plugin uses every registered <see cref="IOpenFinanceSource"/>.
    /// </summary>
    /// <param name="services">The host's services.</param>
    public static OpenFinanceBrasilBuilder AddOpenFinanceBrasil(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IIntegrationPlugin, OpenFinanceBrasilIntegrationPlugin>(sp => new OpenFinanceBrasilIntegrationPlugin(
            sp.GetServices<IOpenFinanceSource>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<OpenFinanceBrasilIntegrationPlugin>>() ?? NullLogger<OpenFinanceBrasilIntegrationPlugin>.Instance)));
        return new OpenFinanceBrasilBuilder(services);
    }
}
