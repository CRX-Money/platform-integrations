using System.Net;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br.OpenFinance;
using CrxMoney.Integrations.Providers.Br.Pluggy.Api;
using CrxMoney.Integrations.Providers.Br.Pluggy.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace CrxMoney.Integrations.Providers.Br.Pluggy.Tests;

public sealed class PluggyIntegrationPluginTests
{
    private const string BankAccountKey = "br:077:0001:123456789";
    private const string CardAccountKey = "br:077:card:1234";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonNode Data(IntegrationImportEvent e) => JsonNode.Parse(e.ProviderData!)!;

    [Fact]
    public void Plugin_identifies_as_pluggy_and_declares_the_open_finance_schemas()
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());

        Assert.Equal("Pluggy", host.Plugin.ProviderId);
        Assert.Equal(IntegrationCapability.Transfers, host.Plugin.Capabilities);
        Assert.Equal(
            [OpenFinanceImportSchemas.DepositSchemaId, OpenFinanceImportSchemas.WithdrawalSchemaId],
            host.Plugin.GetImportSchemaManifest().Schemas.Select(s => s.SchemaId));
    }

    [Fact]
    public async Task Sync_reads_both_accounts_through_every_cursor_page()
    {
        var server = FakePluggyServer.WithFixtures();
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Equal(9, result.ImportEvents.Count(e => e.ExternalAccountId == BankAccountKey));
        Assert.Equal(2, result.ImportEvents.Count(e => e.ExternalAccountId == CardAccountKey));
        Assert.Equal(result.ImportEvents.Count, result.ImportEvents.Select(e => e.IdempotencyKey).Distinct().Count());
        Assert.Equal([null, "synthetic-cursor-0001"], server.RequestsTo("/v2/transactions")
            .Where(r => r.Query("accountId") == FakePluggyServer.BankAccountId).Select(r => r.Query("cursor")));
        Assert.Single(server.RequestsTo("/auth"));
        Assert.All(server.Requests.Where(r => r.Path != "/auth"), r => Assert.Equal("synthetic-api-key-0", r.Headers["X-API-KEY"]));
        Assert.Equal([new IntegrationCheckpoint("transactions", "2026-10-10")], result.Checkpoints);

        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        Assert.Equal(1, diagnostics["pendingSkipped"]!.GetValue<int>());
        Assert.True(diagnostics["accounts"]![0]!["balanceMatchesNet"]!.GetValue<bool>());
        Assert.Equal("UPDATED", diagnostics["sourceDiagnostics"]!["itemStatus"]!.GetValue<string>());
        Assert.NotNull(diagnostics["credentialExpiresAtUtc"]);
    }

    [Fact]
    public async Task Checking_lines_map_with_counterparty_tax_ids_and_sweep_hints()
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var events = (await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct)).ImportEvents.ToDictionary(e => e.ExternalId);

        var pixIn = events["00000000-0000-4000-8000-000000000c01"];
        Assert.Equal("deposit", pixIn.Type);
        Assert.Equal(1500m, pixIn.Amount);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(-3)), pixIn.OccurredAtUtc);
        Assert.Equal(SyntheticTaxIds.Cnpj(1), Data(pixIn)["counterparty"]!["taxId"]!.GetValue<string>());
        Assert.Equal("00000001", Data(pixIn)["counterparty"]!["ispb"]!.GetValue<string>());
        Assert.Equal("Pluggy", Data(pixIn)["provider"]!.GetValue<string>());

        var pixOut = events["00000000-0000-4000-8000-000000000c02"];
        Assert.Equal("withdrawal", pixOut.Type);
        Assert.Equal(250.75m, pixOut.Amount);
        Assert.Equal("Fulano de Tal", Data(pixOut)["counterparty"]!["name"]!.GetValue<string>());
        Assert.Equal("CPF", Data(pixOut)["counterparty"]!["taxIdKind"]!.GetValue<string>());

        // Payee without a name: the merchant enrichment fills it in.
        Assert.Equal("Concessionaria Exemplo S.A.", Data(events["00000000-0000-4000-8000-000000000c03"])["counterparty"]!["name"]!.GetValue<string>());

        Assert.Equal("investment", Data(events["00000000-0000-4000-8000-000000000c04"])["transferHint"]!.GetValue<string>());
        Assert.Equal("investment", Data(events["00000000-0000-4000-8000-000000000c05"])["transferHint"]!.GetValue<string>());
        Assert.Null(Data(events["00000000-0000-4000-8000-000000000c06"])["transferHint"]);
        Assert.Equal("card_bill_payment", Data(events["00000000-0000-4000-8000-000000000c09"])["transferHint"]!.GetValue<string>());
        Assert.NotEqual(events["00000000-0000-4000-8000-000000000c07"].IdempotencyKey, events["00000000-0000-4000-8000-000000000c08"].IdempotencyKey);
    }

    [Fact]
    public async Task Card_lines_normalise_pluggys_card_sign_and_name_the_merchant()
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var events = (await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct)).ImportEvents.ToDictionary(e => e.ExternalId);

        var purchase = events["00000000-0000-4000-8000-000000000d01"];
        Assert.Equal("withdrawal", purchase.Type);
        Assert.Equal(89.9m, purchase.Amount);
        Assert.Equal(CardAccountKey, purchase.ExternalAccountId);
        Assert.Equal(SyntheticTaxIds.Cnpj(3), Data(purchase)["counterparty"]!["taxId"]!.GetValue<string>());
        Assert.Equal("1234", Data(purchase)["card"]!["last4"]!.GetValue<string>());

        var payment = events["00000000-0000-4000-8000-000000000d02"];
        Assert.Equal("deposit", payment.Type);
        Assert.Equal(300m, payment.Amount);
        Assert.Equal("card_bill_payment", Data(payment)["transferHint"]!.GetValue<string>());
        Assert.False(events.ContainsKey("00000000-0000-4000-8000-000000000d03")); // pending
    }

    [Fact]
    public async Task Re_syncing_yields_the_same_keys()
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var first = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);
        var second = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(first.ImportEvents.Select(e => e.IdempotencyKey).Order(), second.ImportEvents.Select(e => e.IdempotencyKey).Order());
    }

    [Fact]
    public async Task Incremental_syncs_send_only_recent_lines()
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero)), Ct);

        Assert.Equal(["00000000-0000-4000-8000-000000000c09", "00000000-0000-4000-8000-000000000d02"], result.ImportEvents.Select(e => e.ExternalId).Order());
    }

    [Fact]
    public async Task Connection_test_reads_the_item_only()
    {
        var server = FakePluggyServer.WithFixtures();
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(connectionId: 0), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Empty(result.ImportEvents);
        Assert.Empty(server.RequestsTo("/v2/transactions"));
        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        Assert.Contains("UPDATED", diagnostics["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("2027-10-13T09:00:00+00:00", diagnostics["credentialExpiresAtUtc"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("LOGIN_ERROR", IntegrationErrorKind.Authentication)]
    [InlineData("WAITING_USER_INPUT", IntegrationErrorKind.Configuration)]
    [InlineData("OUTDATED", IntegrationErrorKind.Upstream)]
    public async Task Unusable_item_states_fail_fast(string status, IntegrationErrorKind expected)
    {
        var server = FakePluggyServer.WithFixtures()
            .On($"/items/{FakePluggyServer.ItemId}", (_, _) => FakePluggyServer.Json(Fixtures.Text("item.json").Replace("\"UPDATED\"", $"\"{status}\"", StringComparison.Ordinal)));
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(expected, result.ErrorKind);
        Assert.Empty(server.RequestsTo("/accounts"));
    }

    [Fact]
    public async Task An_expired_consent_fails_as_authentication()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2027, 11, 1, 0, 0, 0, TimeSpan.Zero));
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures(), time);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(IntegrationErrorKind.Authentication, result.ErrorKind);
        Assert.Contains("2027-10-13", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-item")]
    public async Task A_missing_item_id_is_a_configuration_failure(string? itemId)
    {
        var server = FakePluggyServer.WithFixtures();
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(itemId: itemId), Ct);

        Assert.Equal(IntegrationErrorKind.Configuration, result.ErrorKind);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task An_item_of_another_application_is_a_configuration_failure()
    {
        var server = FakePluggyServer.WithFixtures().On($"/items/{FakePluggyServer.ItemId}", (_, _) =>
            FakePluggyServer.Json("""{"message":"Item not found","code":404}""", HttpStatusCode.NotFound));
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(IntegrationErrorKind.Configuration, result.ErrorKind);
        Assert.Contains("belongs to this Pluggy application", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_client_credentials_are_an_authentication_failure_without_the_secret()
    {
        var server = FakePluggyServer.WithFixtures().On("/auth", (_, _) =>
            FakePluggyServer.Json("""{"message":"Invalid credentials","code":401}""", HttpStatusCode.Unauthorized));
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(connectionId: 0), Ct);

        Assert.Equal(IntegrationErrorKind.Authentication, result.ErrorKind);
        Assert.DoesNotContain("synthetic-client-secret", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_api_key_is_renewed_once()
    {
        var server = FakePluggyServer.WithFixtures();
        server.On($"/items/{FakePluggyServer.ItemId}", (request, _) => request.Headers["X-API-KEY"] == "synthetic-api-key-0"
            ? FakePluggyServer.Status(HttpStatusCode.Unauthorized)
            : FakePluggyServer.Json(Fixtures.Text("item.json")));
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(connectionId: 0), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Equal(2, server.RequestsTo("/auth").Count);
    }

    [Fact]
    public async Task A_card_with_no_deposit_account_in_the_item_cannot_be_keyed()
    {
        var server = FakePluggyServer.WithFixtures().On("/accounts", (_, _) =>
        {
            var accounts = JsonNode.Parse(Fixtures.Text("accounts.json"))!;
            accounts["results"]!.AsArray().RemoveAt(0);
            return FakePluggyServer.Json(accounts.ToJsonString());
        });
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(IntegrationErrorKind.Upstream, result.ErrorKind);
    }

    [Theory]
    [InlineData("abc123", "v2/transactions?accountId=acc-1&cursor=abc123")]
    [InlineData("a+b/c=", "v2/transactions?accountId=acc-1&cursor=a%2Bb%2Fc%3D")]
    [InlineData("https://evil.example/v2/transactions?accountId=acc-1&cursor=x", "v2/transactions?accountId=acc-1&cursor=x")]
    [InlineData("/v2/transactions?accountId=acc-1&cursor=y", "v2/transactions?accountId=acc-1&cursor=y")]
    public void Next_cursor_stays_on_the_pluggy_host(string next, string expected)
    {
        Assert.Equal(expected, PluggyApiClient.NextPath("acc-1", next));
    }
}
