using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Countries.Br.OpenFinance;
using CrxMoney.Integrations.Providers.Br.Pluggy.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrxMoney.Integrations.Providers.Br.Pluggy;

/// <summary>
/// Brazilian Open Finance through Pluggy. All mapping and keys come from
/// <see cref="OpenFinanceIntegrationPlugin"/>; this class only names the provider and supplies the
/// Pluggy source. Credentials: <c>ApiKey</c> = client id, <c>ApiSecret</c> = client secret,
/// <c>Passphrase</c> = item id.
/// </summary>
public sealed class PluggyIntegrationPlugin : OpenFinanceIntegrationPlugin
{
    /// <summary>The provider id; equals the platform's <c>Integration.Provider</c> name.</summary>
    public const string Id = "Pluggy";

    internal PluggyIntegrationPlugin(PluggyOpenFinanceSource source, TimeProvider timeProvider, ILogger<PluggyIntegrationPlugin> logger)
        : base(source, timeProvider, logger)
    {
    }

    /// <inheritdoc />
    public override string ProviderId => Id;

    /// <inheritdoc />
    public override string DisplayName => "Pluggy (Open Finance)";
}

/// <summary>Registers the Pluggy plugin with a host's service collection.</summary>
public static class PluggyServiceCollectionExtensions
{
    /// <summary>The default Pluggy API address.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://api.pluggy.ai/");

    /// <summary>
    /// Adds <see cref="PluggyIntegrationPlugin"/> as an <see cref="IIntegrationPlugin"/> with a typed
    /// HttpClient on the standard resilience pipeline (retries honouring <c>Retry-After</c>, timeouts,
    /// circuit breaker) and a shared API-key cache.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="baseAddress">Override for the API address (tests, proxies); defaults to <see cref="DefaultBaseAddress"/>.</param>
    public static IServiceCollection AddPluggyIntegration(this IServiceCollection services, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PluggyApiKeyCache>();
        services.AddHttpClient<PluggyApiClient>(client => client.BaseAddress = baseAddress ?? DefaultBaseAddress)
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
            });
        services.TryAddScoped<PluggyOpenFinanceSource>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IIntegrationPlugin, PluggyIntegrationPlugin>(sp => new PluggyIntegrationPlugin(
            sp.GetRequiredService<PluggyOpenFinanceSource>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<PluggyIntegrationPlugin>>() ?? NullLogger<PluggyIntegrationPlugin>.Instance)));
        return services;
    }
}
