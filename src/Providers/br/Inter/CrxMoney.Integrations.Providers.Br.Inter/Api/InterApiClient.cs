using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CrxMoney.Integrations.Abstractions.Sync;
using Polly.Timeout;

namespace CrxMoney.Integrations.Providers.Br.Inter.Api;

/// <summary>
/// Typed client for one Inter connection: owns the mTLS <see cref="HttpClient"/>, caches OAuth tokens
/// per scope (renewed <see cref="InterIntegrationOptions.TokenRefreshMargin"/> before expiry, one
/// request at a time), and turns Inter failures into <see cref="InterApiException"/>.
/// </summary>
internal sealed class InterApiClient : IDisposable
{
    /// <summary>The only scope the plugin needs: statement and balance reads.</summary>
    public const string StatementReadScope = "extrato.read";

    private const string AccountHeader = "x-conta-corrente";
    private const int MaxErrorDetailLength = 300;

    private readonly HttpClient _httpClient;
    private readonly X509Certificate2? _clientCertificate;
    private readonly InterConnectionSettings _settings;
    private readonly InterIntegrationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly Dictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);
    private long _lastUsedUtcTicks;

    public InterApiClient(
        HttpClient httpClient,
        InterConnectionSettings settings,
        InterIntegrationOptions options,
        TimeProvider timeProvider,
        X509Certificate2? clientCertificate = null)
    {
        _httpClient = httpClient;
        _settings = settings;
        _options = options;
        _timeProvider = timeProvider;
        _clientCertificate = clientCertificate;
        CertificateNotAfterUtc = clientCertificate is null ? null : new DateTimeOffset(clientCertificate.NotAfter.ToUniversalTime());
        Touch();
    }

    public InterEnvironment Environment => _settings.Environment;

    public string AccountNumber => _settings.AccountNumber;

    /// <summary>End of the client certificate's validity; <c>null</c> only for clients built without one (tests).</summary>
    public DateTimeOffset? CertificateNotAfterUtc { get; }

    /// <summary>Last time this client was used, for idle eviction by the registry.</summary>
    internal DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedUtcTicks), TimeSpan.Zero);

    /// <summary>Returns a valid access token for <paramref name="scope"/>, requesting one only when the cached one is missing or about to expire.</summary>
    public async Task<string> GetAccessTokenAsync(string scope, CancellationToken cancellationToken)
    {
        Touch();
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_tokens.TryGetValue(scope, out var cached) && cached.RenewAfterUtc > now)
                return cached.AccessToken;

            var token = await RequestTokenAsync(scope, cancellationToken).ConfigureAwait(false);
            var lifetime = TimeSpan.FromSeconds(token.ExpiresIn is > 0 ? token.ExpiresIn.Value : 3600);
            // Renew early, but never spend more than half a short-lived token's life on the margin.
            var margin = _options.TokenRefreshMargin < lifetime / 2 ? _options.TokenRefreshMargin : lifetime / 2;
            _tokens[scope] = new CachedToken(token.AccessToken!, now + lifetime - margin);
            return token.AccessToken!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Reads one page of the enriched statement (traditional pagination, page numbers from 0).</summary>
    public Task<InterStatementPage> GetStatementPageAsync(DateOnly from, DateOnly to, int page, int pageSize, CancellationToken cancellationToken)
    {
        var uri = string.Create(CultureInfo.InvariantCulture,
            $"banking/v2/extrato/completo?dataInicio={from:yyyy-MM-dd}&dataFim={to:yyyy-MM-dd}&pagina={page}&tamanhoPagina={pageSize}");
        return GetAsync(uri, InterApiJsonContext.Default.InterStatementPage, cancellationToken);
    }

    /// <summary>Reads the current balance (no <c>dataSaldo</c>) or the closing balance of <paramref name="date"/>.</summary>
    public Task<InterBalance> GetBalanceAsync(DateOnly? date, CancellationToken cancellationToken)
    {
        var uri = date is { } day
            ? string.Create(CultureInfo.InvariantCulture, $"banking/v2/saldo?dataSaldo={day:yyyy-MM-dd}")
            : "banking/v2/saldo";
        return GetAsync(uri, InterApiJsonContext.Default.InterBalance, cancellationToken);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _clientCertificate?.Dispose();
        _tokenLock.Dispose();
    }

    private async Task<T> GetAsync<T>(string relativeUri, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        Touch();
        // A 401 on a cached token means Inter revoked it early: drop it and try once with a fresh one.
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetAccessTokenAsync(StatementReadScope, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation(AccountHeader, _settings.AccountNumber);

            using var response = await SendAsync(request, isTokenCall: false, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                await InvalidateTokenAsync(StatementReadScope, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await EnsureSuccessAsync(response, isTokenCall: false, cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync(response, typeInfo, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<InterTokenResponse> RequestTokenAsync(string scope, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "oauth/v2/token")
        {
            Content = new FormUrlEncodedContent(
            [
                new("client_id", _settings.ClientId),
                new("client_secret", _settings.ClientSecret),
                new("grant_type", "client_credentials"),
                new("scope", scope),
            ]),
        };

        using var response = await SendAsync(request, isTokenCall: true, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, isTokenCall: true, cancellationToken).ConfigureAwait(false);
        var token = await ReadJsonAsync(response, InterApiJsonContext.Default.InterTokenResponse, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InterApiException(IntegrationErrorKind.Upstream, response.StatusCode, "Inter returned a token response without an access token.");

        return token;
    }

    private async Task InvalidateTokenAsync(string scope, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _tokens.Remove(scope);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool isTokenCall, CancellationToken cancellationToken)
    {
        var what = isTokenCall ? "token request" : "request";
        try
        {
            // ResponseContentRead: the body is downloaded inside the resilience pipeline, so it is covered
            // by the attempt timeout and retried like any other transient failure.
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new InterApiException(IntegrationErrorKind.Authentication, null,
                $"The TLS handshake with Inter failed during the {what}. Check that the certificate belongs to this integration and environment and has not expired.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InterApiException(IntegrationErrorKind.Network, null, $"Inter could not be reached during the {what} ({ex.HttpRequestError}).", ex);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new InterApiException(IntegrationErrorKind.Network, null, $"The Inter {what} timed out.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, bool isTokenCall, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var status = (int)response.StatusCode;
        var reason = await ReadErrorReasonAsync(response, cancellationToken).ConfigureAwait(false);
        var what = isTokenCall ? "token request" : "request";
        var message = reason is null
            ? $"Inter rejected the {what} with HTTP {status}."
            : $"Inter rejected the {what} with HTTP {status}: {reason}";
        throw new InterApiException(InterApiException.KindFor(response.StatusCode, isTokenCall), response.StatusCode, message);
    }

    private static async Task<string?> ReadErrorReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            var error = JsonSerializer.Deserialize(body, InterApiJsonContext.Default.InterErrorResponse);
            if (error is null)
                return null;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(error.Title))
                parts.Add(error.Title.Trim());
            if (!string.IsNullOrWhiteSpace(error.Detail) && !string.Equals(error.Detail.Trim(), error.Title?.Trim(), StringComparison.Ordinal))
                parts.Add(error.Detail.Trim());
            foreach (var violation in error.Violacoes ?? [])
            {
                if (!string.IsNullOrWhiteSpace(violation.Razao))
                    parts.Add(string.IsNullOrWhiteSpace(violation.Propriedade) ? violation.Razao.Trim() : $"{violation.Propriedade.Trim()}: {violation.Razao.Trim()}");
            }

            var reason = string.Join(" ", parts);
            if (reason.Length == 0)
                return null;
            return reason.Length <= MaxErrorDetailLength ? reason : reason[..MaxErrorDetailLength] + "…";
        }
        catch (JsonException)
        {
            return null; // Not Inter's JSON error shape (a gateway page, for example): the status says enough.
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
                ?? throw new InterApiException(IntegrationErrorKind.Upstream, response.StatusCode, "Inter returned an empty response body.");
        }
        catch (JsonException ex)
        {
            throw new InterApiException(IntegrationErrorKind.Upstream, response.StatusCode, "Inter returned a response that is not the documented JSON shape.", ex);
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastUsedUtcTicks, _timeProvider.GetUtcNow().UtcTicks);

    private sealed record CachedToken(string AccessToken, DateTimeOffset RenewAfterUtc);
}
