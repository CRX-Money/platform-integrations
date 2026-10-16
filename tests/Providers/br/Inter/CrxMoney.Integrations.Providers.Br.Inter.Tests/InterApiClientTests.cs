using System.Net;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.Inter.Api;
using CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests;

public sealed class InterApiClientTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    private InterApiClient Client(FakeInterServer server) => new(
        new HttpClient(server) { BaseAddress = new Uri("https://cdpj-sandbox.partners.uatinter.co/") },
        new InterConnectionSettings("synthetic-client-id", "synthetic client&secret", InterEnvironment.Sandbox, "12345678",
            new InterCredentialBundle.CertificateDocument()),
        new InterIntegrationOptions(),
        _time);

    [Fact]
    public async Task Token_request_is_a_form_post_with_client_credentials_and_scope()
    {
        var server = new FakeInterServer().OnToken();
        using var client = Client(server);

        var token = await client.GetAccessTokenAsync(InterApiClient.StatementReadScope, TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal("synthetic-token-0", token);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/oauth/v2/token", request.Path);
        Assert.Equal("synthetic-client-id", request.Form("client_id"));
        Assert.Equal("synthetic client&secret", request.Form("client_secret")); // encoded on the wire, decoded here
        Assert.Equal("client_credentials", request.Form("grant_type"));
        Assert.Equal("extrato.read", request.Form("scope"));
    }

    [Fact]
    public async Task Token_is_reused_until_shortly_before_it_expires()
    {
        var server = new FakeInterServer().OnToken(expiresIn: 3600);
        using var client = Client(server);
        var ct = TestContext.Current.CancellationToken;

        var first = await client.GetAccessTokenAsync(InterApiClient.StatementReadScope, ct);
        _time.Advance(TimeSpan.FromMinutes(58)); // 60 s before the default refresh margin
        var reused = await client.GetAccessTokenAsync(InterApiClient.StatementReadScope, ct);
        _time.Advance(TimeSpan.FromSeconds(61));
        var renewed = await client.GetAccessTokenAsync(InterApiClient.StatementReadScope, ct);

        Assert.Equal(first, reused);
        Assert.NotEqual(first, renewed);
        Assert.Equal(2, server.RequestsTo("/oauth/v2/token").Count);
    }

    [Fact]
    public async Task A_short_lived_token_is_still_reused_for_part_of_its_life()
    {
        var server = new FakeInterServer().OnToken(expiresIn: 30);
        using var client = Client(server);
        var ct = TestContext.Current.CancellationToken;

        await client.GetAccessTokenAsync(InterApiClient.StatementReadScope, ct);
        _time.Advance(TimeSpan.FromSeconds(10));
        await client.GetAccessTokenAsync(InterApiClient.StatementReadScope, ct);
        _time.Advance(TimeSpan.FromSeconds(6));
        await client.GetAccessTokenAsync(InterApiClient.StatementReadScope, ct);

        Assert.Equal(2, server.RequestsTo("/oauth/v2/token").Count);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_token_request()
    {
        var release = new TaskCompletionSource();
        var server = new FakeInterServer().On("/oauth/v2/token", (_, call) =>
        {
            release.Task.Wait(TimeSpan.FromSeconds(5));
            return FakeInterServer.Json($$"""{"access_token":"synthetic-token-{{call}}","expires_in":3600}""");
        });
        using var client = Client(server);
        var ct = TestContext.Current.CancellationToken;

        var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(() => client.GetAccessTokenAsync(InterApiClient.StatementReadScope, ct), ct)).ToArray();
        release.SetResult();
        var tokens = await Task.WhenAll(callers);

        Assert.Single(server.RequestsTo("/oauth/v2/token"));
        Assert.All(tokens, t => Assert.Equal("synthetic-token-0", t));
    }

    [Fact]
    public async Task Banking_calls_send_the_bearer_token_and_the_account_header()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/saldo", (_, _) => FakeInterServer.Json(Fixtures.Text("saldo.json")));
        using var client = Client(server);

        var balance = await client.GetBalanceAsync(null, TestContext.Current.CancellationToken);

        var request = Assert.Single(server.RequestsTo("/banking/v2/saldo"));
        Assert.Equal("Bearer synthetic-token-0", request.Headers["Authorization"]);
        Assert.Equal("12345678", request.Headers["x-conta-corrente"]);
        Assert.Equal(12345.67m, balance.Disponivel);
    }

    [Fact]
    public async Task Statement_query_uses_the_documented_parameters()
    {
        var server = new FakeInterServer().OnToken().OnStatementFixturePages();
        using var client = Client(server);

        var page = await client.GetStatementPageAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 1, 5, TestContext.Current.CancellationToken);

        var request = Assert.Single(server.RequestsTo("/banking/v2/extrato/completo"));
        Assert.Equal("2026-03-01", request.Query("dataInicio"));
        Assert.Equal("2026-03-31", request.Query("dataFim"));
        Assert.Equal("1", request.Query("pagina"));
        Assert.Equal("5", request.Query("tamanhoPagina"));
        Assert.Equal(4, page.Transacoes!.Count);
    }

    [Fact]
    public async Task A_401_drops_the_cached_token_and_retries_once_with_a_new_one()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/saldo", (_, call) =>
            call == 0 ? FakeInterServer.Status(HttpStatusCode.Unauthorized) : FakeInterServer.Json(Fixtures.Text("saldo.json")));
        using var client = Client(server);

        await client.GetBalanceAsync(null, TestContext.Current.CancellationToken);

        var calls = server.RequestsTo("/banking/v2/saldo");
        Assert.Equal(["Bearer synthetic-token-0", "Bearer synthetic-token-1"], calls.Select(c => c.Headers["Authorization"]));
    }

    [Fact]
    public async Task A_second_401_is_an_authentication_failure()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/saldo", (_, _) => FakeInterServer.Status(HttpStatusCode.Unauthorized));
        using var client = Client(server);

        var error = await Assert.ThrowsAsync<InterApiException>(() => client.GetBalanceAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(IntegrationErrorKind.Authentication, error.Kind);
        Assert.Equal(2, server.RequestsTo("/banking/v2/saldo").Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, IntegrationErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Unauthorized, IntegrationErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, IntegrationErrorKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, IntegrationErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, IntegrationErrorKind.Upstream)]
    public async Task Token_failures_map_to_error_kinds(HttpStatusCode status, IntegrationErrorKind expected)
    {
        var server = new FakeInterServer().On("/oauth/v2/token", (_, _) => FakeInterServer.Status(status));
        using var client = Client(server);

        var error = await Assert.ThrowsAsync<InterApiException>(() =>
            client.GetAccessTokenAsync(InterApiClient.StatementReadScope, TestContext.Current.CancellationToken));

        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public async Task Inter_error_body_is_summarised_without_echoing_submitted_values()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/saldo", (_, _) =>
            FakeInterServer.Json(Fixtures.Text("error-403.json"), HttpStatusCode.Forbidden));
        using var client = Client(server);

        var error = await Assert.ThrowsAsync<InterApiException>(() => client.GetBalanceAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(IntegrationErrorKind.Permission, error.Kind);
        Assert.Contains("HTTP 403", error.Message, StringComparison.Ordinal);
        Assert.Contains("Escopo insuficiente", error.Message, StringComparison.Ordinal);
        Assert.Contains("scope: escopo extrato.read nao concedido", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_is_not_the_documented_json_is_an_upstream_failure()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/saldo", (_, _) => FakeInterServer.Json("<html>gateway</html>"));
        using var client = Client(server);

        var error = await Assert.ThrowsAsync<InterApiException>(() => client.GetBalanceAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(IntegrationErrorKind.Upstream, error.Kind);
    }
}
