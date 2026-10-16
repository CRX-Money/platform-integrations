using System.Security.Cryptography.X509Certificates;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.Inter.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;

/// <summary>
/// Builds the plugin the way a host does (<see cref="InterServiceCollectionExtensions.AddInterIntegration"/>),
/// with the mTLS transport replaced by <see cref="FakeInterServer"/> and fast retries.
/// </summary>
internal sealed class InterTestHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;

    public InterTestHost(FakeInterServer server, Action<InterIntegrationOptions>? configure = null, TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        if (timeProvider is not null)
            services.AddSingleton(timeProvider);
        services.AddSingleton<InterTransportFactory>(new FakeTransportFactory(server));
        services.AddInterIntegration(options =>
        {
            options.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            options.MaxRetryDelay = TimeSpan.FromMilliseconds(50);
            options.BankingCallsPerWindow = 1000;
            options.TokenCallsPerWindow = 1000;
            configure?.Invoke(options);
        });
        services.TryAddSingleton(TimeProvider.System);

        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateScope();
        Plugin = _scope.ServiceProvider.GetServices<IIntegrationPlugin>().OfType<InterIntegrationPlugin>().Single();
    }

    public InterIntegrationPlugin Plugin { get; }

    public static IntegrationSyncRequest Request(
        long connectionId = 42,
        string? bundle = null,
        DateTimeOffset? windowStartUtc = null,
        DateTimeOffset? windowEndUtc = null,
        IReadOnlyList<IntegrationCheckpoint>? checkpoints = null) =>
        new(
            ConnectionId: connectionId,
            ProviderId: InterIntegrationPlugin.Id,
            WindowStartUtc: windowStartUtc ?? new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero),
            WindowEndUtc: windowEndUtc ?? new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero),
            ApiKey: "synthetic-client-id",
            ApiSecret: "synthetic-client-secret",
            Passphrase: bundle ?? TestCertificates.Bundle(),
            PreviousCheckpoints: checkpoints);

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
    }

    private sealed class FakeTransportFactory(FakeInterServer server) : InterTransportFactory
    {
        public override HttpMessageHandler Create(X509Certificate2 clientCertificate)
        {
            Assert.True(clientCertificate.HasPrivateKey, "The transport must receive the client certificate with its key.");
            return new NonDisposingHandler(server);
        }
    }

    /// <summary>Lets several clients share one fake server without the first disposal tearing it down.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
