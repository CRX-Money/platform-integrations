using System.Net;
using CrxMoney.Integrations.Providers.Br.Inter.Api;
using CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests;

public sealed class InterRateLimitTests
{
    [Fact]
    public async Task Throttle_lets_the_limit_through_and_holds_the_next_call_until_the_window_moves()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var throttle = new InterCallThrottle(limit: 10, TimeSpan.FromMinutes(1), time);

        for (var i = 0; i < 10; i++)
            Assert.True(throttle.WaitAsync(CancellationToken.None).IsCompletedSuccessfully);

        var eleventh = throttle.WaitAsync(CancellationToken.None).AsTask();
        Assert.False(eleventh.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(eleventh.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await eleventh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Throttle_wait_is_cancellable()
    {
        var time = new FakeTimeProvider();
        var throttle = new InterCallThrottle(limit: 1, TimeSpan.FromMinutes(1), time);
        await throttle.WaitAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var waiting = throttle.WaitAsync(cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Token_calls_and_banking_calls_have_separate_budgets()
    {
        var time = new FakeTimeProvider();
        var throttles = new InterThrottles(
            new InterCallThrottle(1, TimeSpan.FromMinutes(1), time),
            new InterCallThrottle(2, TimeSpan.FromMinutes(1), time));
        var server = new FakeInterServer()
            .On("/oauth/v2/token", (_, _) => FakeInterServer.Status(HttpStatusCode.OK))
            .On("/banking/v2/saldo", (_, _) => FakeInterServer.Status(HttpStatusCode.OK));
        using var client = new HttpClient(new InterThrottlingHandler(throttles) { InnerHandler = server }) { BaseAddress = new Uri("https://inter.test/") };
        var ct = TestContext.Current.CancellationToken;

        (await client.PostAsync("oauth/v2/token", null, ct)).Dispose();
        (await client.GetAsync("banking/v2/saldo", ct)).Dispose();
        (await client.GetAsync("banking/v2/saldo", ct)).Dispose();
        var heldToken = client.PostAsync("oauth/v2/token", null, ct);
        var heldBanking = client.GetAsync("banking/v2/saldo", ct);

        Assert.Equal(3, server.Requests.Count);
        Assert.False(heldToken.IsCompleted);
        Assert.False(heldBanking.IsCompleted);

        time.Advance(TimeSpan.FromMinutes(1));
        (await heldToken.WaitAsync(TimeSpan.FromSeconds(5), ct)).Dispose();
        (await heldBanking.WaitAsync(TimeSpan.FromSeconds(5), ct)).Dispose();
        Assert.Equal(5, server.Requests.Count);
    }

    [Fact]
    public void Retry_after_delta_is_honoured()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));

        Assert.Equal(TimeSpan.FromSeconds(17), InterHttpPipeline.RetryDelayFor(response, 0, new InterIntegrationOptions(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Retry_after_date_is_honoured()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(42));

        Assert.Equal(TimeSpan.FromSeconds(42), InterHttpPipeline.RetryDelayFor(response, 0, new InterIntegrationOptions(), now));
    }

    [Fact]
    public void A_429_without_retry_after_waits_for_part_of_the_window_growing_per_attempt()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var options = new InterIntegrationOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), InterHttpPipeline.RetryDelayFor(response, 0, options, DateTimeOffset.UtcNow));
        Assert.Equal(TimeSpan.FromSeconds(60), InterHttpPipeline.RetryDelayFor(response, 1, options, DateTimeOffset.UtcNow));
        Assert.Equal(options.MaxRetryDelay, InterHttpPipeline.RetryDelayFor(response, 5, options, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Other_failures_use_the_exponential_backoff()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway);

        Assert.Null(InterHttpPipeline.RetryDelayFor(response, 0, new InterIntegrationOptions(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Retry_after_is_capped()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
        var options = new InterIntegrationOptions { MaxRetryDelay = TimeSpan.FromSeconds(90) };

        Assert.Equal(TimeSpan.FromSeconds(90), InterHttpPipeline.RetryDelayFor(response, 0, options, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Pipeline_retries_a_429_after_the_retry_after_delay_and_counts_each_attempt()
    {
        var server = new FakeInterServer().On("/banking/v2/saldo", (_, call) =>
        {
            if (call > 0)
                return FakeInterServer.Json(Fixtures.Text("saldo.json"));
            var limited = FakeInterServer.Status(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return limited;
        });
        var options = new InterIntegrationOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) };
        var banking = new InterCallThrottle(10, TimeSpan.FromMinutes(1), TimeProvider.System);
        var handler = InterHttpPipeline.Create(server, options, new InterThrottles(banking, banking), TimeProvider.System);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://inter.test/") };

        using var response = await client.GetAsync("banking/v2/saldo", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.RequestsTo("/banking/v2/saldo").Count);
    }

    [Fact]
    public async Task Pipeline_does_not_retry_client_errors()
    {
        var server = new FakeInterServer().On("/banking/v2/saldo", (_, _) => FakeInterServer.Status(HttpStatusCode.Forbidden));
        var options = new InterIntegrationOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) };
        var throttle = new InterCallThrottle(10, TimeSpan.FromMinutes(1), TimeProvider.System);
        using var client = new HttpClient(InterHttpPipeline.Create(server, options, new InterThrottles(throttle, throttle), TimeProvider.System))
        {
            BaseAddress = new Uri("https://inter.test/"),
        };

        using var response = await client.GetAsync("banking/v2/saldo", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(server.Requests);
    }
}
