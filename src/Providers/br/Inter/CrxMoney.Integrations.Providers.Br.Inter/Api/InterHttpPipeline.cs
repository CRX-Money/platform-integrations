using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;

namespace CrxMoney.Integrations.Providers.Br.Inter.Api;

/// <summary>
/// Builds the per-connection handler chain:
/// <c>ResilienceHandler (total timeout, retry honouring Retry-After, attempt timeout)</c> →
/// <c>InterThrottlingHandler</c> → the mTLS <c>SocketsHttpHandler</c>.
/// Throttling sits inside the retry loop so every attempt, retries included, counts against Inter's limits.
/// </summary>
internal static class InterHttpPipeline
{
    public static HttpMessageHandler Create(
        HttpMessageHandler transport,
        InterIntegrationOptions options,
        InterThrottles throttles,
        TimeProvider timeProvider)
    {
        var throttling = new InterThrottlingHandler(throttles) { InnerHandler = transport };
        return new ResilienceHandler(BuildResiliencePipeline(options, timeProvider)) { InnerHandler = throttling };
    }

    internal static ResiliencePipeline<HttpResponseMessage> BuildResiliencePipeline(InterIntegrationOptions options, TimeProvider timeProvider)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = timeProvider }
            .AddTimeout(new HttpTimeoutStrategyOptions { Timeout = options.TotalRequestTimeout });

        if (options.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryBaseDelay,
                MaxDelay = options.MaxRetryDelay,
                ShouldRetryAfterHeader = false, // replaced by the generator below, which also caps the delay
                ShouldHandle = args => ValueTask.FromResult(IsRetryable(args.Outcome)),
                DelayGenerator = args => ValueTask.FromResult(
                    RetryDelayFor(args.Outcome.Result, args.AttemptNumber, options, timeProvider.GetUtcNow())),
            });
        }

        return builder
            .AddTimeout(new HttpTimeoutStrategyOptions { Timeout = options.AttemptTimeout })
            .Build();
    }

    /// <summary>
    /// The delay before retry number <paramref name="attemptNumber"/> (0-based): Inter's <c>Retry-After</c>
    /// when present; for a 429 without it, a share of the rate-limit window that grows per attempt;
    /// otherwise <c>null</c>, meaning the exponential back-off. Always capped at <see cref="InterIntegrationOptions.MaxRetryDelay"/>.
    /// </summary>
    internal static TimeSpan? RetryDelayFor(HttpResponseMessage? response, int attemptNumber, InterIntegrationOptions options, DateTimeOffset now)
    {
        if (response is null)
            return null;

        TimeSpan? delay = null;
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            delay = delta;
        else if (retryAfter?.Date is { } date)
            delay = date - now;
        else if (response.StatusCode == HttpStatusCode.TooManyRequests)
            delay = options.RateLimitWindow / 2 * (attemptNumber + 1);

        if (delay is null)
            return null;

        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        return delay > options.MaxRetryDelay ? options.MaxRetryDelay : delay;
    }

    private static bool IsRetryable(Outcome<HttpResponseMessage> outcome)
    {
        // A refused client certificate or broken TLS setup will not heal on retry.
        if (outcome.Exception is HttpRequestException { InnerException: AuthenticationException })
            return false;

        return HttpClientResiliencePredicates.IsTransient(outcome);
    }
}

/// <summary>The two call budgets of one Inter integration (client id + environment).</summary>
internal sealed record InterThrottles(InterCallThrottle Token, InterCallThrottle Banking);

/// <summary>Waits for the right budget before each attempt goes on the wire.</summary>
internal sealed class InterThrottlingHandler(InterThrottles throttles) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var throttle = request.RequestUri?.AbsolutePath.StartsWith("/oauth/", StringComparison.Ordinal) == true
            ? throttles.Token
            : throttles.Banking;

        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
