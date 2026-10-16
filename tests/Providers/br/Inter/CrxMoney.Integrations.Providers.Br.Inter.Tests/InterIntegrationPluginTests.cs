using System.Net;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests;

public sealed class InterIntegrationPluginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Plugin_identifies_as_the_inter_provider()
    {
        using var host = new InterTestHost(new FakeInterServer());

        Assert.Equal("Inter", host.Plugin.ProviderId);
        Assert.Equal(IntegrationCapability.Transfers, host.Plugin.Capabilities);
        Assert.Equal(60, host.Plugin.Defaults.SyncEveryMinutes);
        Assert.Equal(
            ["inter.banking.statement.deposit", "inter.banking.statement.withdrawal"],
            host.Plugin.GetImportSchemaManifest().Schemas.Select(s => s.SchemaId));
        Assert.All(host.Plugin.GetImportSchemaManifest().Schemas, s => JsonNode.Parse(s.JsonSchema!));
    }

    [Fact]
    public void Registering_twice_adds_the_plugin_once()
    {
        var services = new ServiceCollection().AddInterIntegration().AddInterIntegration();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Single(scope.ServiceProvider.GetServices<IIntegrationPlugin>());
    }

    [Fact]
    public async Task Sync_pages_through_the_statement_and_returns_every_entry_once()
    {
        var server = new FakeInterServer().OnToken().OnStatementFixturePages();
        using var host = new InterTestHost(server, o => o.StatementPageSize = 5);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Equal(9, result.TransfersImported);
        Assert.Equal(9, result.ImportEvents.Select(e => e.IdempotencyKey).Distinct().Count());
        Assert.Equal(["BRL"], result.NonZeroAssetCodes);
        Assert.Equal(["0", "1"], server.RequestsTo("/banking/v2/extrato/completo").Select(r => r.Query("pagina")));
        Assert.All(server.RequestsTo("/banking/v2/extrato/completo"), r => Assert.Equal("5", r.Query("tamanhoPagina")));
        Assert.Single(server.RequestsTo("/oauth/v2/token"));
        Assert.Equal([new IntegrationCheckpoint("statement", "2026-03-31")], result.Checkpoints);
    }

    [Fact]
    public async Task Re_syncing_the_same_window_yields_the_same_idempotency_keys()
    {
        var server = new FakeInterServer().OnToken().OnStatementFixturePages();
        using var host = new InterTestHost(server, o => o.StatementPageSize = 5);

        var first = await host.Plugin.SyncAsync(InterTestHost.Request(), Ct);
        var second = await host.Plugin.SyncAsync(InterTestHost.Request(checkpoints: first.Checkpoints), Ct);

        Assert.Equal(first.ImportEvents.Select(e => e.IdempotencyKey), second.ImportEvents.Select(e => e.IdempotencyKey));
    }

    [Fact]
    public async Task The_client_and_its_token_are_reused_across_syncs_of_the_same_connection()
    {
        var server = new FakeInterServer().OnToken().OnStatementFixturePages();
        using var host = new InterTestHost(server, o => o.StatementPageSize = 5);
        var bundle = TestCertificates.Bundle();

        await host.Plugin.SyncAsync(InterTestHost.Request(bundle: bundle), Ct);
        await host.Plugin.SyncAsync(InterTestHost.Request(bundle: bundle), Ct);

        Assert.Single(server.RequestsTo("/oauth/v2/token"));
    }

    [Fact]
    public async Task Long_ranges_are_read_in_windows_of_at_most_90_days()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/extrato/completo", (_, _) =>
            FakeInterServer.Json("""{"totalPaginas":1,"totalElementos":0,"ultimaPagina":true,"primeiraPagina":true,"tamanhoPagina":1000,"numeroDeElementos":0,"transacoes":[]}"""));
        using var host = new InterTestHost(server);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(
            windowStartUtc: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            windowEndUtc: new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero)), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        var windows = server.RequestsTo("/banking/v2/extrato/completo")
            .Select(r => (From: DateOnly.Parse(r.Query("dataInicio")!), To: DateOnly.Parse(r.Query("dataFim")!)))
            .ToArray();
        Assert.Equal(new DateOnly(2026, 1, 1), windows[0].From);
        Assert.Equal(new DateOnly(2026, 9, 30), windows[^1].To);
        Assert.All(windows, w => Assert.InRange(w.To.DayNumber - w.From.DayNumber + 1, 1, 90));
        Assert.Empty(result.ImportEvents);
        Assert.Empty(result.NonZeroAssetCodes);
    }

    [Fact]
    public async Task A_window_over_the_10000_entry_pagination_limit_is_split()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/extrato/completo", (request, _) =>
        {
            var days = DateOnly.Parse(request.Query("dataFim")!).DayNumber - DateOnly.Parse(request.Query("dataInicio")!).DayNumber + 1;
            var total = days > 8 ? 12_000 : 0;
            return FakeInterServer.Json($$"""{"totalPaginas":1,"totalElementos":{{total}},"ultimaPagina":true,"transacoes":[]}""");
        });
        using var host = new InterTestHost(server);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        var read = server.RequestsTo("/banking/v2/extrato/completo")
            .Select(r => (From: DateOnly.Parse(r.Query("dataInicio")!), To: DateOnly.Parse(r.Query("dataFim")!)))
            .Where(w => w.To.DayNumber - w.From.DayNumber + 1 <= 8)
            .OrderBy(w => w.From)
            .ToArray();
        Assert.Equal(new DateOnly(2026, 3, 1), read[0].From);
        Assert.Equal(new DateOnly(2026, 3, 31), read[^1].To);
        for (var i = 1; i < read.Length; i++)
            Assert.Equal(read[i - 1].To.AddDays(1), read[i].From);
    }

    [Fact]
    public async Task Connection_test_gets_a_token_and_reads_the_balance_without_importing()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/saldo", (_, _) => FakeInterServer.Json(Fixtures.Text("saldo.json")));
        using var host = new InterTestHost(server);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(connectionId: 0), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Empty(result.ImportEvents);
        Assert.Empty(server.RequestsTo("/banking/v2/extrato/completo"));
        Assert.Single(server.RequestsTo("/banking/v2/saldo"));
        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        Assert.Equal("token+saldo", diagnostics["check"]!.GetValue<string>());
        Assert.True(diagnostics["balanceRead"]!.GetValue<bool>());
        Assert.NotNull(diagnostics["certificateNotAfterUtc"]);
        Assert.DoesNotContain("12345.67", result.ProviderDiagnosticsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_credentials_fail_the_connection_test_as_authentication()
    {
        var server = new FakeInterServer().On("/oauth/v2/token", (_, _) => FakeInterServer.Status(HttpStatusCode.Unauthorized));
        using var host = new InterTestHost(server);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(connectionId: 0), Ct);

        Assert.Equal(IntegrationSyncStatus.Failed, result.Status);
        Assert.Equal(IntegrationErrorKind.Authentication, result.ErrorKind);
        Assert.DoesNotContain("synthetic-client-secret", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_scope_is_a_permission_failure()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/extrato/completo", (_, _) =>
            FakeInterServer.Json(Fixtures.Text("error-403.json"), HttpStatusCode.Forbidden));
        using var host = new InterTestHost(server);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(), Ct);

        Assert.Equal(IntegrationErrorKind.Permission, result.ErrorKind);
    }

    [Fact]
    public async Task Persistent_throttling_is_reported_as_rate_limited_after_the_retries()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/extrato/completo", (_, _) =>
        {
            var limited = FakeInterServer.Status(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return limited;
        });
        using var host = new InterTestHost(server, o => o.MaxRetryAttempts = 2);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(), Ct);

        Assert.Equal(IntegrationErrorKind.RateLimited, result.ErrorKind);
        Assert.Equal(3, server.RequestsTo("/banking/v2/extrato/completo").Count);
    }

    [Fact]
    public async Task Transient_upstream_errors_are_retried()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/extrato/completo", (request, call) =>
            call == 0 ? FakeInterServer.Status(HttpStatusCode.ServiceUnavailable) : FakeInterServer.Json(Fixtures.Text($"extrato-completo-page-{request.Query("pagina")}.json")));
        using var host = new InterTestHost(server, o => o.StatementPageSize = 5);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Equal(9, result.ImportEvents.Count);
    }

    [Fact]
    public async Task An_unusable_entry_fails_the_sync_instead_of_being_dropped()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/extrato/completo", (_, _) =>
            FakeInterServer.Json("""{"totalPaginas":1,"ultimaPagina":true,"transacoes":[{"idTransacao":"SYNTH-X","dataTransacao":"2026-03-02","tipoOperacao":"?","valor":"1.00"}]}"""));
        using var host = new InterTestHost(server);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(), Ct);

        Assert.Equal(IntegrationErrorKind.Upstream, result.ErrorKind);
        Assert.Contains("SYNTH-X", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "synthetic-client-secret", "client id")]
    [InlineData("synthetic-client-id", null, "client secret")]
    public async Task Missing_credentials_are_configuration_failures(string? clientId, string? clientSecret, string expected)
    {
        var server = new FakeInterServer();
        using var host = new InterTestHost(server);

        var result = await host.Plugin.SyncAsync(InterTestHost.Request() with { ApiKey = clientId, ApiSecret = clientSecret }, Ct);

        Assert.Equal(IntegrationErrorKind.Configuration, result.ErrorKind);
        Assert.Contains(expected, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_bundle_that_is_not_ours_is_a_configuration_failure()
    {
        using var host = new InterTestHost(new FakeInterServer());

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(bundle: """{"format":"something-else","version":1}"""), Ct);

        Assert.Equal(IntegrationErrorKind.Configuration, result.ErrorKind);
    }

    [Fact]
    public async Task An_expired_certificate_fails_before_calling_inter()
    {
        var server = new FakeInterServer().OnToken();
        using var host = new InterTestHost(server);
        var bundle = TestCertificates.Bundle(notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        var result = await host.Plugin.SyncAsync(InterTestHost.Request(bundle: bundle), Ct);

        Assert.Equal(IntegrationErrorKind.Configuration, result.ErrorKind);
        Assert.Contains("expired", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Sandbox_and_production_use_their_own_hosts()
    {
        var server = new FakeInterServer().OnToken().On("/banking/v2/saldo", (_, _) => FakeInterServer.Json(Fixtures.Text("saldo.json")));
        using var host = new InterTestHost(server);
        var (certificatePem, keyPem) = TestCertificates.PemPair();
        var production = InterCredentialBundle.CreateFromPem(certificatePem, keyPem, null, InterEnvironment.Production, "1234567-8");

        await host.Plugin.SyncAsync(InterTestHost.Request(connectionId: 0), Ct);
        await host.Plugin.SyncAsync(InterTestHost.Request(connectionId: 0, bundle: production), Ct);

        Assert.Equal(
            ["cdpj-sandbox.partners.uatinter.co", "cdpj.partners.bancointer.com.br"],
            server.RequestsTo("/banking/v2/saldo").Select(r => r.Uri.Host));
    }
}
