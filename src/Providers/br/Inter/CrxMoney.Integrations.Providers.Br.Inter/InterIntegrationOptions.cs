namespace CrxMoney.Integrations.Providers.Br.Inter;

/// <summary>
/// Tuning for the Inter plugin. The defaults follow Inter's published limits and rarely need changing;
/// hosts bind them from configuration through <see cref="InterServiceCollectionExtensions.AddInterIntegration"/>.
/// </summary>
public sealed class InterIntegrationOptions
{
    /// <summary>Base address of the production API.</summary>
    public Uri ProductionBaseAddress { get; set; } = new("https://cdpj.partners.bancointer.com.br/");

    /// <summary>Base address of the sandbox API (fictitious data).</summary>
    public Uri SandboxBaseAddress { get; set; } = new("https://cdpj-sandbox.partners.uatinter.co/");

    /// <summary>Banking calls (statement, balance) allowed per <see cref="RateLimitWindow"/> per integration. Inter allows 10 per minute.</summary>
    public int BankingCallsPerWindow { get; set; } = 10;

    /// <summary>Token calls allowed per <see cref="RateLimitWindow"/> per integration. Inter allows 5 per minute.</summary>
    public int TokenCallsPerWindow { get; set; } = 5;

    /// <summary>The window the call limits apply to.</summary>
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Statement entries requested per page (<c>tamanhoPagina</c>, at most 10,000).</summary>
    public int StatementPageSize { get; set; } = 1000;

    /// <summary>Longest statement window per request in calendar days, both ends included. Inter allows 90.</summary>
    public int StatementWindowDays { get; set; } = 90;

    /// <summary>
    /// Days re-read before the last synced date on every incremental sync, so entries Inter books late
    /// are still picked up. Re-reading is safe: idempotency keys prevent duplicates.
    /// </summary>
    public int StatementOverlapDays { get; set; } = 3;

    /// <summary>A token is renewed this long before Inter says it expires.</summary>
    public TimeSpan TokenRefreshMargin { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Timeout of one HTTP attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout of one call including its retries.</summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Retries after a transient failure (HTTP 408, 429, 5xx, network errors, attempt timeouts).</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base delay of the exponential back-off when the response carries no <c>Retry-After</c>.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for any single retry delay, including one requested through <c>Retry-After</c>.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Clients (connections) unused for this long are disposed, releasing their certificate and sockets.</summary>
    public TimeSpan IdleClientLifetime { get; set; } = TimeSpan.FromHours(2);

    internal Uri BaseAddressFor(InterEnvironment environment) =>
        environment == InterEnvironment.Production ? ProductionBaseAddress : SandboxBaseAddress;

    internal void Validate()
    {
        if (BankingCallsPerWindow < 1 || TokenCallsPerWindow < 1)
            throw new InvalidOperationException("Inter call limits must be at least 1 per window.");
        if (RateLimitWindow <= TimeSpan.Zero)
            throw new InvalidOperationException("The Inter rate-limit window must be positive.");
        if (StatementPageSize is < 1 or > 10_000)
            throw new InvalidOperationException("The Inter statement page size must be between 1 and 10,000.");
        if (StatementWindowDays is < 1 or > 90)
            throw new InvalidOperationException("The Inter statement window must be between 1 and 90 days.");
        if (StatementOverlapDays < 0)
            throw new InvalidOperationException("The Inter statement overlap cannot be negative.");
        if (MaxRetryAttempts < 0)
            throw new InvalidOperationException("Inter retry attempts cannot be negative.");
        if (AttemptTimeout <= TimeSpan.Zero || TotalRequestTimeout < AttemptTimeout)
            throw new InvalidOperationException("Inter timeouts must be positive, and the total timeout must cover one attempt.");
    }
}
