using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br.OpenFinance;
using Polly.Timeout;

namespace CrxMoney.Integrations.Providers.Br.Pluggy.Api;

/// <summary>A Pluggy developer application's credentials. <see cref="ToString"/> never prints the secret.</summary>
internal sealed record PluggyCredentials(string ClientId, string ClientSecret)
{
    public override string ToString() => "PluggyCredentials { ClientId = *** }";

    /// <summary>Cache key that does not keep the secret in clear text.</summary>
    public string CacheKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{ClientId}\n{ClientSecret}")));
}

/// <summary>
/// API keys minted by <c>POST /auth</c> are valid for two hours; one is kept per credential pair and
/// renewed early, so a sync never authenticates per request.
/// </summary>
internal sealed class PluggyApiKeyCache(TimeProvider timeProvider)
{
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromMinutes(110);
    private readonly ConcurrentDictionary<string, (string ApiKey, DateTimeOffset RenewAfter)> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<string> GetAsync(PluggyCredentials credentials, Func<CancellationToken, Task<string>> mint, CancellationToken cancellationToken)
    {
        if (_keys.TryGetValue(credentials.CacheKey, out var cached) && cached.RenewAfter > timeProvider.GetUtcNow())
            return cached.ApiKey;

        // One lock per credential pair: a slow authentication never holds up other workspaces.
        var gate = _gates.GetOrAdd(credentials.CacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys.TryGetValue(credentials.CacheKey, out cached) && cached.RenewAfter > timeProvider.GetUtcNow())
                return cached.ApiKey;

            var apiKey = await mint(cancellationToken).ConfigureAwait(false);
            _keys[credentials.CacheKey] = (apiKey, timeProvider.GetUtcNow() + KeyLifetime);
            return apiKey;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(PluggyCredentials credentials) => _keys.TryRemove(credentials.CacheKey, out _);
}

/// <summary>Typed client for the Pluggy API. Failures become <see cref="OpenFinanceSourceException"/> with safe messages.</summary>
internal sealed class PluggyApiClient(HttpClient httpClient, PluggyApiKeyCache apiKeys)
{
    private const int MaxPages = 10_000;

    public Task<PluggyItem> GetItemAsync(PluggyCredentials credentials, Guid itemId, CancellationToken cancellationToken) =>
        GetAsync(credentials, $"items/{itemId:D}", PluggyJsonContext.Default.PluggyItem, "item", cancellationToken);

    public async Task<IReadOnlyList<PluggyAccount>> GetAccountsAsync(PluggyCredentials credentials, Guid itemId, CancellationToken cancellationToken)
    {
        var page = await GetAsync(credentials, $"accounts?itemId={itemId:D}", PluggyJsonContext.Default.PluggyAccountsPage, "accounts", cancellationToken).ConfigureAwait(false);
        return page.Results ?? [];
    }

    /// <summary>
    /// Walks <c>GET /v2/transactions</c> by cursor. The endpoint takes no date filter, so this is every line
    /// Pluggy holds; stopping early would need Pluggy's page order to be documented as date-descending.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetAllTransactionsAsync(PluggyCredentials credentials, string accountId, CancellationToken cancellationToken)
    {
        var lines = new List<JsonElement>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var path = $"v2/transactions?accountId={Uri.EscapeDataString(accountId)}";
        for (var pages = 0; ; pages++)
        {
            if (pages == MaxPages)
                throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy returned more than {MaxPages} transaction pages for one account.");

            var page = await GetAsync(credentials, path, PluggyJsonContext.Default.PluggyTransactionsPage, "transactions", cancellationToken).ConfigureAwait(false);
            foreach (var line in page.Results ?? [])
                lines.Add(line.Clone());

            if (string.IsNullOrWhiteSpace(page.Next))
                return lines;
            if (!seenCursors.Add(page.Next))
                throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, "Pluggy returned the same transactions cursor twice.");

            path = NextPath(accountId, page.Next);
        }
    }

    /// <summary>
    /// <c>next</c> is a cursor token; tolerate a full or relative URL as well, keeping only path and query
    /// so a response can never redirect the API key to another host.
    /// </summary>
    internal static string NextPath(string accountId, string next)
    {
        if (Uri.TryCreate(next, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http")
            return absolute.PathAndQuery.TrimStart('/');
        if (next.StartsWith('/'))
            return next.TrimStart('/');
        return $"v2/transactions?accountId={Uri.EscapeDataString(accountId)}&cursor={Uri.EscapeDataString(next)}";
    }

    private async Task<T> GetAsync<T>(PluggyCredentials credentials, string path, JsonTypeInfo<T> typeInfo, string what, CancellationToken cancellationToken)
        where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            var apiKey = await apiKeys.GetAsync(credentials, ct => MintApiKeyAsync(credentials, ct), cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);
            using var response = await SendAsync(request, what, cancellationToken).ConfigureAwait(false);

            // An expired or revoked API key: mint a new one once. 403 is a permission (product) problem, not a key problem.
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                apiKeys.Invalidate(credentials);
                continue;
            }

            await EnsureSuccessAsync(response, what, isAuth: false, cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, typeInfo, what, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> MintApiKeyAsync(PluggyCredentials credentials, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "auth")
        {
            Content = JsonContent.Create(new PluggyAuthRequest(credentials.ClientId, credentials.ClientSecret), PluggyJsonContext.Default.PluggyAuthRequest),
        };
        using var response = await SendAsync(request, "authentication", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "authentication", isAuth: true, cancellationToken).ConfigureAwait(false);
        var auth = await ReadAsync(response, PluggyJsonContext.Default.PluggyAuthResponse, "authentication", cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(auth.ApiKey)
            ? throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, "Pluggy returned no API key.")
            : auth.ApiKey;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        try
        {
            // ResponseContentRead keeps the body download inside the resilience pipeline's timeout and retries.
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenFinanceSourceException(IntegrationErrorKind.Network, $"Pluggy could not be reached for the {what} request ({ex.HttpRequestError}).", ex);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new OpenFinanceSourceException(IntegrationErrorKind.Network, $"The Pluggy {what} request timed out.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, bool isAuth, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => IntegrationErrorKind.Authentication,
            HttpStatusCode.Forbidden when isAuth => IntegrationErrorKind.Authentication,
            HttpStatusCode.Forbidden => IntegrationErrorKind.Permission,
            HttpStatusCode.BadRequest when isAuth => IntegrationErrorKind.Authentication,
            HttpStatusCode.NotFound => IntegrationErrorKind.Configuration,
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => IntegrationErrorKind.Validation,
            HttpStatusCode.TooManyRequests => IntegrationErrorKind.RateLimited,
            HttpStatusCode.RequestTimeout => IntegrationErrorKind.Network,
            _ => IntegrationErrorKind.Upstream,
        };
        var reason = await ReadReasonAsync(response, cancellationToken).ConfigureAwait(false);
        var hint = response.StatusCode == HttpStatusCode.NotFound && what == "item"
            ? " Check that the item id belongs to this Pluggy application."
            : string.Empty;
        throw new OpenFinanceSourceException(kind,
            $"Pluggy rejected the {what} request with HTTP {(int)response.StatusCode}{(reason is null ? "." : $": {reason}")}{hint}");
    }

    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var message = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize(body, PluggyJsonContext.Default.PluggyErrorResponse)?.Message?.Trim();
            return string.IsNullOrEmpty(message) ? null : message.Length <= 200 ? message : message[..200] + "…";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, string what, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false)
                ?? throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy returned an empty {what} response.");
        }
        catch (JsonException ex)
        {
            throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy returned a {what} response in an unexpected shape.", ex);
        }
    }
}
