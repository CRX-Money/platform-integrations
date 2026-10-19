using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

/// <summary>Adds the Pluggy adapter to the Open Finance Brasil plugin.</summary>
public static class PluggyOpenFinanceBrasilBuilderExtensions
{
    /// <summary>
    /// Registers the Pluggy adapter (<see cref="PluggyAdapter.Name"/>) with a typed HttpClient on the standard
    /// resilience pipeline (retries honouring <c>Retry-After</c>, timeouts, circuit breaker) and a shared API-key
    /// cache. Replaces <c>AddPluggyIntegration()</c> of the former Pluggy package.
    /// </summary>
    /// <param name="builder">From <see cref="OpenFinanceBrasilServiceCollectionExtensions.AddOpenFinanceBrasil"/>.</param>
    /// <param name="baseAddress">Override for the API address (tests, proxies); defaults to <see cref="PluggyAdapter.DefaultBaseAddress"/>.</param>
    public static OpenFinanceBrasilBuilder AddPluggy(this OpenFinanceBrasilBuilder builder, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        services.TryAddSingleton<PluggyApiKeyCache>();
        services.AddHttpClient<PluggyApiClient>(client => client.BaseAddress = baseAddress ?? PluggyAdapter.DefaultBaseAddress)
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
            });
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOpenFinanceSource, PluggyOpenFinanceSource>());
        return builder;
    }
}
