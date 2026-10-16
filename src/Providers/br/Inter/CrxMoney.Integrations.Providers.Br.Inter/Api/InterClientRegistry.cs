using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;

namespace CrxMoney.Integrations.Providers.Br.Inter.Api;

/// <summary>
/// Creates the mTLS transport for a client certificate. Replaced in tests by a fake handler.
/// </summary>
internal class InterTransportFactory
{
    public virtual HttpMessageHandler Create(X509Certificate2 clientCertificate) => new SocketsHttpHandler
    {
        SslOptions = { ClientCertificates = new X509CertificateCollection { clientCertificate } },
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.All,
    };
}

/// <summary>
/// Keeps one <see cref="InterApiClient"/> per distinct set of credentials, so the certificate is
/// loaded once and tokens are reused across syncs, and one pair of call budgets per Inter
/// integration (environment + client id), shared by every connection that uses it. Clients idle for
/// longer than <see cref="InterIntegrationOptions.IdleClientLifetime"/> are disposed.
/// </summary>
internal sealed class InterClientRegistry : IDisposable
{
    private readonly InterIntegrationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly InterTransportFactory _transportFactory;
    private readonly ConcurrentDictionary<string, Lazy<InterApiClient>> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InterThrottles> _throttles = new(StringComparer.Ordinal);

    public InterClientRegistry(IOptions<InterIntegrationOptions> options, TimeProvider timeProvider, InterTransportFactory transportFactory)
    {
        _options = options.Value;
        _options.Validate();
        _timeProvider = timeProvider;
        _transportFactory = transportFactory;
    }

    public InterIntegrationOptions Options => _options;

    /// <exception cref="InterCredentialException">The certificate cannot be loaded.</exception>
    public InterApiClient GetClient(InterConnectionSettings settings)
    {
        EvictIdleClients();

        var key = Fingerprint(settings);
        var lazy = _clients.GetOrAdd(key, _ => new Lazy<InterApiClient>(() => CreateClient(settings), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _clients.TryRemove(new KeyValuePair<string, Lazy<InterApiClient>>(key, lazy));
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var lazy in _clients.Values)
        {
            if (lazy.IsValueCreated)
                lazy.Value.Dispose();
        }

        _clients.Clear();
    }

    private InterApiClient CreateClient(InterConnectionSettings settings)
    {
        var certificate = InterClientCertificate.Load(settings.Certificate);
        try
        {
            var throttles = _throttles.GetOrAdd($"{settings.Environment}|{settings.ClientId}", _ => new InterThrottles(
                new InterCallThrottle(_options.TokenCallsPerWindow, _options.RateLimitWindow, _timeProvider),
                new InterCallThrottle(_options.BankingCallsPerWindow, _options.RateLimitWindow, _timeProvider)));

            var handler = InterHttpPipeline.Create(_transportFactory.Create(certificate), _options, throttles, _timeProvider);
            var httpClient = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = _options.BaseAddressFor(settings.Environment),
                Timeout = Timeout.InfiniteTimeSpan, // the resilience pipeline owns attempt and total timeouts
            };

            return new InterApiClient(httpClient, settings, _options, _timeProvider, clientCertificate: certificate);
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private void EvictIdleClients()
    {
        var cutoff = _timeProvider.GetUtcNow() - _options.IdleClientLifetime;
        foreach (var entry in _clients)
        {
            if (entry.Value.IsValueCreated
                && entry.Value.Value.LastUsedUtc < cutoff
                && _clients.TryRemove(entry))
            {
                entry.Value.Value.Dispose();
            }
        }
    }

    private static string Fingerprint(InterConnectionSettings settings)
    {
        var material = string.Join('\n',
            settings.Environment,
            settings.ClientId,
            settings.ClientSecret,
            settings.AccountNumber,
            settings.Certificate.PfxBase64,
            settings.Certificate.CertificatePem,
            settings.Certificate.PrivateKeyPem,
            settings.Certificate.Password);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
