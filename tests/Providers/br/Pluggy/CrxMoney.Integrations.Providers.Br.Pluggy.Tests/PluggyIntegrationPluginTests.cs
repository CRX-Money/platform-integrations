using System.Globalization;
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
            [OpenFinanceImportSchemas.DepositSchemaId, OpenFinanceImportSchemas.WithdrawalSchemaId,
             OpenFinanceImportSchemas.InvestmentDepositSchemaId, OpenFinanceImportSchemas.InvestmentWithdrawalSchemaId],
            host.Plugin.GetImportSchemaManifest().Schemas.Select(s => s.SchemaId));
    }

    [Fact]
    public async Task A_full_history_sync_reads_every_line_without_a_window()
    {
        var server = FakePluggyServer.WithFixtures();
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Equal(9, result.ImportEvents.Count(e => e.ExternalAccountId == BankAccountKey));
        Assert.Equal(2, result.ImportEvents.Count(e => e.ExternalAccountId == CardAccountKey));
        Assert.Equal(result.ImportEvents.Count, result.ImportEvents.Select(e => e.IdempotencyKey).Distinct().Count());
        Assert.All(server.RequestsTo("/v2/transactions"), r => Assert.Equal(["accountId"], r.QueryNames()));
        Assert.Single(server.TransactionRequests(FakePluggyServer.BankAccountId));
        Assert.Single(server.TransactionRequests(FakePluggyServer.CardAccountId));
        Assert.Single(server.RequestsTo("/auth"));
        Assert.All(server.Requests.Where(r => r.Path != "/auth"), r => Assert.Equal("synthetic-api-key-0", r.Headers["X-API-KEY"]));
        Assert.Equal([new IntegrationCheckpoint("transactions", "2026-10-10")], result.Checkpoints);

        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        Assert.Equal(1, diagnostics["pendingSkipped"]!.GetValue<int>());
        Assert.Null(diagnostics["sentFrom"]);
        Assert.Equal("UPDATED", diagnostics["sourceDiagnostics"]!["itemStatus"]!.GetValue<string>());
        Assert.Equal(2, diagnostics["sourceDiagnostics"]!["transactionPages"]!.GetValue<int>());
        Assert.Null(diagnostics["sourceDiagnostics"]!["transactionsDateFrom"]);
        Assert.NotNull(diagnostics["credentialExpiresAtUtc"]);
    }

    [Fact]
    public async Task The_provider_summary_reports_the_deposit_balance_and_the_settled_card_balance_per_account()
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero)), Ct);

        var accounts = JsonNode.Parse(result.ProviderDiagnosticsJson!)!["accounts"]!.AsArray();
        var bank = accounts.Single(a => a!["kind"]!.GetValue<string>() == "deposit")!;
        Assert.Equal(BankAccountKey, bank["externalAccountId"]!.GetValue<string>());
        Assert.Equal(639.36m, bank["balance"]!.GetValue<decimal>());
        // The balance is as of Pluggy's last refresh of the item (item.json lastUpdatedAt).
        Assert.Equal("2026-10-13T09:00:00+00:00", bank["balanceAtUtc"]!.GetValue<string>());
        Assert.Equal(1, bank["lines"]!.GetValue<int>());
        Assert.Null(bank["balanceMatchesNet"]);

        var card = accounts.Single(a => a!["kind"]!.GetValue<string>() == "creditCard")!;
        Assert.Equal(CardAccountKey, card["externalAccountId"]!.GetValue<string>());
        // Pluggy's 131.90 is the used limit: the posted balance plus the pending purchase of 42.00 (d03).
        Assert.Equal(89.9m, card["balance"]!.GetValue<decimal>());
        Assert.Equal(42m, card["pendingAmount"]!.GetValue<decimal>());
        Assert.Equal(1, card["pendingLines"]!.GetValue<int>());
        Assert.Equal(131.9m, card["balanceWithPending"]!.GetValue<decimal>());
        Assert.Equal("liability", card["balanceSide"]!.GetValue<string>());
        Assert.Equal("2026-10-13T09:00:00+00:00", card["balanceAtUtc"]!.GetValue<string>());
        Assert.Equal(5000m, card["creditLimit"]!.GetValue<decimal>());
        Assert.Equal(4868.1m, card["availableCreditLimit"]!.GetValue<decimal>());
        Assert.Equal(13.19m, card["minimumPayment"]!.GetValue<decimal>());
        Assert.Equal("2026-10-13", card["billCloseDate"]!.GetValue<string>());
        Assert.Equal("2026-10-20", card["billDueDate"]!.GetValue<string>());

        // The preview.4 keys keep their names and order; new keys only follow them.
        Assert.Equal(
            ["kind", "externalAccountId", "lines", "posted", "sent", "balance", "balanceAtUtc", "balanceSide", "institutionWebsite"],
            bank.AsObject().Select(p => p.Key));
        Assert.Equal(
            ["kind", "externalAccountId", "lines", "posted", "sent", "balance", "balanceAtUtc", "balanceSide",
             "pendingAmount", "pendingLines", "balanceWithPending", "creditLimit", "availableCreditLimit", "minimumPayment", "billCloseDate", "billDueDate",
             "institutionWebsite"],
            card.AsObject().Select(p => p.Key));
        // COMPE 077 is in the package's institution list; the card's issuer is the item's bank.
        Assert.Equal("https://inter.co/", bank["institutionWebsite"]!.GetValue<string>());
        Assert.Equal("https://inter.co/", card["institutionWebsite"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pages_are_chained_by_the_after_cursor_and_keep_the_window()
    {
        var server = FakePluggyServer.WithFixtures();
        server.TransactionPageSize = 2;
        using var host = new PluggyTestHost(server);

        // Window start 27 Sep → sent from 20 Sep → requested from 19 Sep: bank lines c05..c09, three pages.
        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero)), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        var bankPages = server.TransactionRequests(FakePluggyServer.BankAccountId);
        Assert.Equal(3, bankPages.Count);
        Assert.Null(bankPages[0].Query("after"));
        Assert.Equal(["+++/AA==", "+++/AQ=="], bankPages.Skip(1).Select(r => r.Query("after")));
        Assert.All(server.RequestsTo("/v2/transactions"), r =>
        {
            Assert.Equal("api.pluggy.ai", r.Uri.Host);
            Assert.Null(r.Query("createdAtFrom"));
            Assert.Null(r.Query("dateTo"));
        });
        Assert.All(bankPages, r => Assert.Equal("2026-09-19", r.Query("dateFrom")));
        // Cards are read from 62 days before today (14 Oct) so that every pending purchase is seen.
        Assert.All(server.TransactionRequests(FakePluggyServer.CardAccountId), r => Assert.Equal("2026-08-13", r.Query("dateFrom")));
        Assert.Equal(
            ["00000000-0000-4000-8000-000000000c05", "00000000-0000-4000-8000-000000000c06", "00000000-0000-4000-8000-000000000c07",
             "00000000-0000-4000-8000-000000000c08", "00000000-0000-4000-8000-000000000c09", "00000000-0000-4000-8000-000000000d02"],
            result.ImportEvents.Where(e => e.ExternalAccountId is BankAccountKey or CardAccountKey).Select(e => e.ExternalId).Order());
        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        Assert.Equal(5, diagnostics["sourceDiagnostics"]!["transactionPages"]!.GetValue<int>()); // 3 bank + 2 card (d01..d03)
        Assert.Equal("2026-09-19", diagnostics["sourceDiagnostics"]!["transactionsDateFrom"]!.GetValue<string>());
        Assert.Equal("2026-08-13", diagnostics["sourceDiagnostics"]!["cardTransactionsDateFrom"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_full_history_read_over_many_pages_matches_a_single_page_read()
    {
        var paged = FakePluggyServer.WithFixtures();
        paged.TransactionPageSize = 2;
        using var pagedHost = new PluggyTestHost(paged);
        using var singleHost = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var many = await pagedHost.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);
        var one = await singleHost.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(5, paged.TransactionRequests(FakePluggyServer.BankAccountId).Count);
        Assert.Equal(one.ImportEvents.Select(e => (e.ExternalId, e.IdempotencyKey)).Order(), many.ImportEvents.Select(e => (e.ExternalId, e.IdempotencyKey)).Order());
    }

    /// <summary>
    /// The guarantee behind windowed reads: whatever the window, the lines sent and their keys are exactly those a
    /// full read would send from the same date, including the two identical lines of 25 Sep told apart by ordinal.
    /// </summary>
    [Theory]
    [InlineData("2026-09-02T12:00:00Z", "2026-08-25")]
    [InlineData("2026-09-12T04:00:00Z", "2026-09-04")]
    [InlineData("2026-09-13T02:00:00Z", "2026-09-04")] // 23:00 on 12 Sep in Brazil
    [InlineData("2026-10-02T12:00:00Z", "2026-09-24")]
    [InlineData("2026-10-03T02:59:59Z", "2026-09-24")]
    [InlineData("2026-10-20T12:00:00Z", "2026-10-12")]
    public async Task A_windowed_sync_sends_what_a_full_read_sends_from_the_same_date_with_the_same_keys(string windowStart, string expectedDateFrom)
    {
        var start = DateTimeOffset.Parse(windowStart, CultureInfo.InvariantCulture);
        var sendFrom = BrazilDate(start).AddDays(-OpenFinanceIntegrationPlugin.OverlapDays);
        var server = FakePluggyServer.WithFixtures();
        using var windowedHost = new PluggyTestHost(server);
        using var fullHost = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var windowed = await windowedHost.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: start), Ct);
        var full = await fullHost.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.All(server.TransactionRequests(FakePluggyServer.BankAccountId), r => Assert.Equal(expectedDateFrom, r.Query("dateFrom")));
        var expected = full.ImportEvents
            .Where(e => BrazilDate(e.OccurredAtUtc) >= sendFrom)
            .Select(e => (e.ExternalId, e.IdempotencyKey))
            .Order();
        Assert.Equal(expected, windowed.ImportEvents.Select(e => (e.ExternalId, e.IdempotencyKey)).Order());
    }

    [Fact]
    public async Task A_window_with_no_new_lines_keeps_the_previous_checkpoint()
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());
        var request = PluggyTestHost.Request(windowStart: new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero)) with
        {
            PreviousCheckpoints = [new IntegrationCheckpoint("transactions", "2026-10-10")],
        };

        var result = await host.Plugin.SyncAsync(request, Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Empty(result.ImportEvents);
        Assert.Equal([new IntegrationCheckpoint("transactions", "2026-10-10")], result.Checkpoints);
    }

    private static DateOnly BrazilDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(TimeSpan.FromHours(-3)).DateTime);

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
        Assert.True(Data(events["00000000-0000-4000-8000-000000000c04"])["counterpartyIsHolder"]!.GetValue<bool>());
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
    public async Task Incremental_syncs_read_and_send_only_the_window()
    {
        var server = FakePluggyServer.WithFixtures();
        using var host = new PluggyTestHost(server);

        // Window start 12 Oct → sent from 5 Oct (seven days of overlap) → requested from 4 Oct (one day of margin).
        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero)), Ct);

        Assert.Equal(["00000000-0000-4000-8000-000000000c09", "00000000-0000-4000-8000-000000000d02"],
            result.ImportEvents.Where(e => e.ExternalAccountId is BankAccountKey or CardAccountKey).Select(e => e.ExternalId).Order());
        Assert.Equal(2, server.RequestsTo("/v2/transactions").Count);
        Assert.All(server.RequestsTo("/v2/transactions"), r => Assert.Equal(["accountId", "dateFrom"], r.QueryNames()));
        Assert.Equal("2026-10-04", server.TransactionRequests(FakePluggyServer.BankAccountId).Single().Query("dateFrom"));
        Assert.Equal("2026-08-13", server.TransactionRequests(FakePluggyServer.CardAccountId).Single().Query("dateFrom"));
        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        Assert.Equal("2026-10-05", diagnostics["sentFrom"]!.GetValue<string>());
        // Bank: c09 read; card: d01..d03 read (pending lookback), d02 sent.
        var accountRows = diagnostics["accounts"]!.AsArray().Where(a => a!["kind"]!.GetValue<string>() != "investment").ToList();
        Assert.Equal([1, 3], accountRows.Select(a => a!["lines"]!.GetValue<int>()));
        Assert.Equal([1, 1], accountRows.Select(a => a!["sent"]!.GetValue<int>()));
        Assert.Equal([new IntegrationCheckpoint("transactions", "2026-10-10")], result.Checkpoints);
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
    public async Task A_missing_product_is_a_permission_failure_without_re_authenticating()
    {
        var server = FakePluggyServer.WithFixtures().On("/v2/transactions", (_, _) =>
            FakePluggyServer.Json("""{"message":"Product not enabled","code":403}""", HttpStatusCode.Forbidden));
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(IntegrationErrorKind.Permission, result.ErrorKind);
        Assert.Single(server.RequestsTo("/auth"));
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
    [InlineData("?accountId=acc-1&after=abc123", "abc123")]
    [InlineData("?accountId=acc-1&after=a%2Bb%2Fc%3D", "a+b/c=")]
    [InlineData("?after=a+b/c=&accountId=acc-1", "a+b/c=")]
    [InlineData("https://evil.example/v2/transactions?accountId=other&after=x", "x")]
    [InlineData("/v2/transactions?accountId=acc-1&after=y", "y")]
    [InlineData("bare+cursor/=", "bare+cursor/=")]
    public void Only_the_after_cursor_of_next_is_followed(string next, string expected)
    {
        Assert.Equal(expected, PluggyApiClient.AfterCursor(next));
    }

    [Fact]
    public void A_next_page_without_a_cursor_is_an_upstream_failure()
    {
        var failure = Assert.Throws<OpenFinanceSourceException>(() => PluggyApiClient.AfterCursor("?accountId=acc-1"));
        Assert.Equal(IntegrationErrorKind.Upstream, failure.Kind);
    }

    [Theory]
    [InlineData(null, null, "v2/transactions?accountId=acc-1")]
    [InlineData("2026-10-04", null, "v2/transactions?accountId=acc-1&dateFrom=2026-10-04")]
    [InlineData("2026-10-04", "a+b/c=", "v2/transactions?accountId=acc-1&dateFrom=2026-10-04&after=a%2Bb%2Fc%3D")]
    public void Every_page_names_the_account_and_the_window(string? dateFrom, string? after, string expected)
    {
        DateOnly? from = dateFrom is null ? null : DateOnly.ParseExact(dateFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(expected, PluggyApiClient.TransactionsPath("acc-1", from, after));
    }

    [Fact]
    public async Task A_repeated_cursor_fails_instead_of_looping()
    {
        var server = FakePluggyServer.WithFixtures().On("/v2/transactions", (_, _) =>
            FakePluggyServer.Json("""{"results":[],"next":"?accountId=x&after=same"}"""));
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.Equal(IntegrationErrorKind.Upstream, result.ErrorKind);
        Assert.Equal(2, server.RequestsTo("/v2/transactions").Count);
    }
}
