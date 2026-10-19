using System.Net;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br.OpenFinance;
using CrxMoney.Integrations.Providers.Br.Pluggy.Api;
using CrxMoney.Integrations.Providers.Br.Pluggy.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace CrxMoney.Integrations.Providers.Br.Pluggy.Tests;

/// <summary>Card balances, investment positions and movements, and the pairs between them and the checking account.</summary>
public sealed class PluggyInvestmentAndCardTests
{
    private const string BankAccountKey = "br:077:0001:123456789";
    private const string CardAccountKey = "br:077:card:1234";
    private const string CdbKey = "br:077:inv:number:0001234";
    private const string StockKey = "br:077:inv:isin:BREXMPACNOR0";

    private static string FundKey => $"br:077:inv:code:{SyntheticTaxIds.Cnpj(21)}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonNode Data(IntegrationImportEvent e) => JsonNode.Parse(e.ProviderData!)!;

    private static JsonNode Diagnostics(IntegrationSyncResult result) => JsonNode.Parse(result.ProviderDiagnosticsJson!)!;

    private static JsonNode Row(IntegrationSyncResult result, string externalAccountId) =>
        Diagnostics(result)["accounts"]!.AsArray().Single(a => a!["externalAccountId"]!.GetValue<string>() == externalAccountId)!;

    private static async Task<IntegrationSyncResult> FullSync(FakePluggyServer? server = null)
    {
        using var host = new PluggyTestHost(server ?? FakePluggyServer.WithFixtures());
        return await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);
    }

    private static FakePluggyServer WithProducts(params string[] products) => FakePluggyServer.WithFixtures()
        .On($"/items/{FakePluggyServer.ItemId}", (_, _) =>
        {
            var item = JsonNode.Parse(Fixtures.Text("item.json"))!;
            item["products"] = new JsonArray([.. products.Select(p => JsonValue.Create(p))]);
            return FakePluggyServer.Json(item.ToJsonString());
        });

    /// <summary>
    /// The keys of account lines are those preview.4 sent, computed by hand from the documented recipe
    /// (sha256 of account|instant|C/D|amount|ordinal): card balances, pairs and investments add provider data only.
    /// </summary>
    [Theory]
    [InlineData("00000000-0000-4000-8000-000000000c01", "brof:6629338669da9b03d44236b3ecb77053")]
    [InlineData("00000000-0000-4000-8000-000000000c07", "brof:e92b42872afdb6beded35d2f42b332b2")]
    [InlineData("00000000-0000-4000-8000-000000000c08", "brof:e47e500ab7743cec6cf973118b06ec8a")]
    [InlineData("00000000-0000-4000-8000-000000000d01", "brof:a78b80c5a4290d03f08c21f88eb3ef9f")]
    [InlineData("00000000-0000-4000-8000-000000000d02", "brof:ba7a7fab5c7aee51e846fa8b915d91c5")]
    public async Task Account_line_keys_are_unchanged(string externalId, string expectedKey)
    {
        var full = await FullSync();
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());
        var windowed = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero)), Ct);

        Assert.Equal(expectedKey, full.ImportEvents.Single(e => e.ExternalId == externalId).IdempotencyKey);
        Assert.Equal(expectedKey, windowed.ImportEvents.Single(e => e.ExternalId == externalId).IdempotencyKey);
    }

    [Fact]
    public async Task Positions_are_reported_as_investment_accounts_with_their_balances()
    {
        var result = await FullSync();

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        var rows = Diagnostics(result)["accounts"]!.AsArray().Where(a => a!["kind"]!.GetValue<string>() == "investment").ToList();
        Assert.Equal([CdbKey, FundKey, StockKey], rows.Select(r => r!["externalAccountId"]!.GetValue<string>()));

        var cdb = Row(result, CdbKey);
        Assert.Equal("number", cdb["externalAccountIdBasis"]!.GetValue<string>());
        Assert.Equal("FIXED_INCOME", cdb["type"]!.GetValue<string>());
        Assert.Equal("CDB", cdb["subtype"]!.GetValue<string>());
        Assert.Equal("Banco Exemplo S.A.", cdb["issuer"]!.GetValue<string>());
        Assert.Equal("TOTAL_WITHDRAWAL", cdb["status"]!.GetValue<string>());
        Assert.Equal(0m, cdb["balance"]!.GetValue<decimal>());
        Assert.Equal("2026-10-13T03:00:00+00:00", cdb["balanceAtUtc"]!.GetValue<string>());
        Assert.Equal("asset", cdb["balanceSide"]!.GetValue<string>());
        Assert.Equal(5000m, cdb["amountInvested"]!.GetValue<decimal>());
        Assert.Equal("2028-09-05", cdb["dueDate"]!.GetValue<string>());
        Assert.Equal(2, cdb["lines"]!.GetValue<int>());
        Assert.Equal(4, cdb["sent"]!.GetValue<int>()); // application + redemption, IR and IOF
        Assert.Equal(["kind", "externalAccountId", "lines", "posted", "sent", "balance", "balanceAtUtc", "balanceSide"],
            cdb.AsObject().Select(p => p.Key).Take(8));

        var fund = Row(result, FundKey);
        Assert.Equal("code", fund["externalAccountIdBasis"]!.GetValue<string>());
        Assert.Equal(2006m, fund["balance"]!.GetValue<decimal>());
        Assert.Equal(2012m, fund["grossAmount"]!.GetValue<decimal>());
        Assert.Equal(6m, fund["profit"]!.GetValue<decimal>());
        Assert.Equal(6m, fund["incomeTax"]!.GetValue<decimal>());

        var stock = Row(result, StockKey);
        Assert.Equal("isin", stock["externalAccountIdBasis"]!.GetValue<string>());
        // The transfer without amount never reaches the model (sourceDiagnostics.investmentMovementsSkipped).
        Assert.Equal(2, stock["lines"]!.GetValue<int>());
        Assert.Equal(4, stock["sent"]!.GetValue<int>()); // purchase + fee, dividend + payout

        var source = Diagnostics(result)["sourceDiagnostics"]!;
        Assert.Equal(3, source["investments"]!.GetValue<int>());
        Assert.Equal(1, source["investmentPages"]!.GetValue<int>());
        Assert.Equal(3, source["investmentTransactionPages"]!.GetValue<int>());
        Assert.Equal(1, source["investmentMovementsSkipped"]!.GetValue<int>());
        Assert.Equal("code:1,isin:1,number:1", source["investmentIdBases"]!.GetValue<string>());
        Assert.Null(source["investmentsSkipped"]);
    }

    [Fact]
    public async Task Movements_become_events_on_their_position_that_add_up_to_the_gross_amount()
    {
        var result = await FullSync();
        var events = result.ImportEvents.ToDictionary(e => e.ExternalId);

        var application = events["00000000-0000-4000-8000-000000000a01"];
        Assert.Equal(("deposit", 5000m, CdbKey), (application.Type, application.Amount!.Value, application.ExternalAccountId));
        Assert.Equal(OpenFinanceImportSchemas.InvestmentDepositSchemaId, application.ProviderDataSchemaId);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.FromHours(-3)), application.OccurredAtUtc);
        Assert.StartsWith("brof:", application.IdempotencyKey, StringComparison.Ordinal);
        Assert.Equal("application", Data(application)["operation"]!.GetValue<string>());
        Assert.Equal("investment", Data(application)["transferHint"]!.GetValue<string>());
        Assert.Equal("CDB", Data(application)["investment"]!["subtype"]!.GetValue<string>());

        // The redemption reaches checking net of IR and IOF; the taxes are their own withdrawals.
        var redemption = events["00000000-0000-4000-8000-000000000a02"];
        var incomeTax = events["00000000-0000-4000-8000-000000000a02#incomeTax"];
        var iof = events["00000000-0000-4000-8000-000000000a02#iof"];
        Assert.Equal(("withdrawal", 5000m), (redemption.Type, redemption.Amount!.Value));
        Assert.Equal(("withdrawal", 10m), (incomeTax.Type, incomeTax.Amount!.Value));
        Assert.Equal(("withdrawal", 2.5m), (iof.Type, iof.Amount!.Value));
        Assert.Equal(5012.5m, redemption.Amount + incomeTax.Amount + iof.Amount);
        Assert.Equal(OpenFinanceImportSchemas.InvestmentWithdrawalSchemaId, incomeTax.ProviderDataSchemaId);
        Assert.Equal(redemption.IdempotencyKey, Data(incomeTax)["parentIdempotencyKey"]!.GetValue<string>());
        Assert.Equal("incomeTax", Data(incomeTax)["component"]!.GetValue<string>());
        Assert.Null(Data(incomeTax)["sourcePayload"]);
        Assert.Null(Data(incomeTax)["transferHint"]);
        Assert.Equal(5000m, Data(redemption)["cashAmount"]!.GetValue<decimal>());
        Assert.Equal(5012.5m, Data(redemption)["grossAmount"]!.GetValue<decimal>());

        // Income kept in the fund is a deposit; the two identical come-cotas lines get distinct keys.
        Assert.Equal(("deposit", 15m), (events["00000000-0000-4000-8000-000000000a04"].Type, events["00000000-0000-4000-8000-000000000a04"].Amount!.Value));
        Assert.Null(Data(events["00000000-0000-4000-8000-000000000a04"])["transferHint"]);
        var tax1 = events["00000000-0000-4000-8000-000000000a05"];
        var tax2 = events["00000000-0000-4000-8000-000000000a06"];
        Assert.Equal("incomeTax", Data(tax1)["taxKind"]!.GetValue<string>());
        Assert.NotEqual(tax1.IdempotencyKey, tax2.IdempotencyKey);

        // A purchase costs the price plus its fees; the fees are booked on the position.
        Assert.Equal(483m, events["00000000-0000-4000-8000-000000000a07"].Amount);
        Assert.Equal(3m, events["00000000-0000-4000-8000-000000000a07#fee"].Amount);

        // A dividend paid out: income on the position and a payout to cash.
        Assert.Equal(("deposit", 4m), (events["00000000-0000-4000-8000-000000000a08"].Type, events["00000000-0000-4000-8000-000000000a08"].Amount!.Value));
        var payout = events["00000000-0000-4000-8000-000000000a08#payout"];
        Assert.Equal(("withdrawal", 4m), (payout.Type, payout.Amount!.Value));
        Assert.Equal("investment", Data(payout)["transferHint"]!.GetValue<string>());

        Assert.DoesNotContain("00000000-0000-4000-8000-000000000a09", events.Keys); // TRANSFER without amount
        Assert.Equal(result.ImportEvents.Count, result.ImportEvents.Select(e => e.IdempotencyKey).Distinct().Count());
    }

    [Fact]
    public async Task Checking_sweeps_name_the_position_they_funded_and_both_sides_point_at_each_other()
    {
        var result = await FullSync();
        var events = result.ImportEvents.ToDictionary(e => e.ExternalId);

        var sweepOut = Data(events["00000000-0000-4000-8000-000000000c04"]);
        Assert.Equal("investment", sweepOut["transferHint"]!.GetValue<string>());
        Assert.Equal("CDB", sweepOut["investment"]!["product"]!.GetValue<string>());
        Assert.Equal(CdbKey, sweepOut["investment"]!["externalAccountId"]!.GetValue<string>());
        Assert.Equal("CDB", sweepOut["investment"]!["subtype"]!.GetValue<string>());
        Assert.Equal(events["00000000-0000-4000-8000-000000000a01"].IdempotencyKey, sweepOut["pair"]!["idempotencyKey"]!.GetValue<string>());
        Assert.Equal("amountAndDate", sweepOut["pair"]!["basis"]!.GetValue<string>());

        var sweepIn = Data(events["00000000-0000-4000-8000-000000000c05"]);
        Assert.Equal(events["00000000-0000-4000-8000-000000000a02"].IdempotencyKey, sweepIn["pair"]!["idempotencyKey"]!.GetValue<string>());

        var application = Data(events["00000000-0000-4000-8000-000000000a01"]);
        Assert.Equal(BankAccountKey, application["pair"]!["externalAccountId"]!.GetValue<string>());
        Assert.Equal(events["00000000-0000-4000-8000-000000000c04"].IdempotencyKey, application["pair"]!["idempotencyKey"]!.GetValue<string>());
        Assert.Null(Data(events["00000000-0000-4000-8000-000000000a03"])["pair"]); // no checking line for it

        var pairs = Diagnostics(result)["transferPairs"]!["investment"]!;
        Assert.Equal(2, pairs["lines"]!.GetValue<int>());
        Assert.Equal(2, pairs["paired"]!.GetValue<int>());
        Assert.Equal(0, pairs["ambiguous"]!.GetValue<int>());
        Assert.Equal(0, pairs["unmatched"]!.GetValue<int>());
        Assert.Equal(3, pairs["positionSideUnpaired"]!.GetValue<int>()); // fund and stock purchases, the dividend payout
    }

    [Fact]
    public async Task Both_sides_of_a_card_bill_payment_carry_the_card_and_point_at_each_other()
    {
        var result = await FullSync();
        var events = result.ImportEvents.ToDictionary(e => e.ExternalId);
        var checking = events["00000000-0000-4000-8000-000000000c09"];
        var card = events["00000000-0000-4000-8000-000000000d02"];

        var paid = Data(checking);
        Assert.Equal("card_bill_payment", paid["transferHint"]!.GetValue<string>());
        Assert.Equal("1234", paid["card"]!["last4"]!.GetValue<string>());
        Assert.Equal("077", paid["card"]!["issuerCompe"]!.GetValue<string>());
        Assert.Equal("00000000-0000-4000-8000-0000000000e1", paid["card"]!["billId"]!.GetValue<string>());
        Assert.Equal(CardAccountKey, paid["card"]!["externalAccountId"]!.GetValue<string>());
        Assert.Equal(card.IdempotencyKey, paid["pair"]!["idempotencyKey"]!.GetValue<string>());

        var received = Data(card);
        Assert.Equal("card_bill_payment", received["transferHint"]!.GetValue<string>());
        Assert.Equal("077", received["card"]!["issuerCompe"]!.GetValue<string>());
        Assert.Equal(BankAccountKey, received["pair"]!["externalAccountId"]!.GetValue<string>());
        Assert.Equal(checking.IdempotencyKey, received["pair"]!["idempotencyKey"]!.GetValue<string>());

        var pairs = Diagnostics(result)["transferPairs"]!["cardBillPayment"]!;
        Assert.Equal([1, 1, 0, 0, 0], new[] { "lines", "paired", "ambiguous", "unmatched", "cardSideUnpaired" }.Select(k => pairs[k]!.GetValue<int>()));
    }

    [Fact]
    public async Task Investment_keys_are_the_same_across_reads_and_page_sizes()
    {
        var paged = FakePluggyServer.WithFixtures();
        paged.InvestmentPageSize = 1;
        paged.TransactionPageSize = 1;
        using var pagedHost = new PluggyTestHost(paged);

        var single = await FullSync();
        var first = await pagedHost.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);
        var second = await pagedHost.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        static IEnumerable<(string, string)> Investments(IntegrationSyncResult r) =>
            r.ImportEvents.Where(e => e.ExternalAccountId.Contains(":inv:", StringComparison.Ordinal)).Select(e => (e.ExternalId, e.IdempotencyKey)).Order();
        Assert.Equal(12, Investments(single).Count());
        Assert.Equal(Investments(single), Investments(first));
        Assert.Equal(Investments(first), Investments(second));
        Assert.Equal(single.ImportEvents.Select(e => (e.ExternalId, e.IdempotencyKey)).Order(), first.ImportEvents.Select(e => (e.ExternalId, e.IdempotencyKey)).Order());

        // Page-number paging: page 1..3 of the listing, every page asks for the same item.
        var listing = paged.RequestsTo("/investments").Take(3).ToList();
        Assert.Equal(["1", "2", "3"], listing.Select(r => r.Query("page")));
        Assert.All(listing, r => Assert.Equal(FakePluggyServer.ItemId, r.Query("itemId")));
        Assert.Equal(4, paged.RequestsTo($"/investments/{FakePluggyServer.InvestmentIds[1]}/transactions").Count / 2); // four movements, one per page, two reads
    }

    [Fact]
    public async Task A_windowed_sync_sends_investment_movements_from_the_same_date_as_account_lines()
    {
        var server = FakePluggyServer.WithFixtures();
        using var host = new PluggyTestHost(server);

        // Window start 12 Oct → sent from 5 Oct.
        var windowed = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero)), Ct);
        var full = await FullSync();

        var sent = windowed.ImportEvents.Where(e => e.ExternalAccountId.Contains(":inv:", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["00000000-0000-4000-8000-000000000a04", "00000000-0000-4000-8000-000000000a05", "00000000-0000-4000-8000-000000000a06",
             "00000000-0000-4000-8000-000000000a08", "00000000-0000-4000-8000-000000000a08#payout"],
            sent.Select(e => e.ExternalId).Order());
        var fullKeys = full.ImportEvents.ToDictionary(e => e.ExternalId, e => e.IdempotencyKey);
        Assert.All(sent, e => Assert.Equal(fullKeys[e.ExternalId], e.IdempotencyKey));
        // Pluggy documents no date filter for investment transactions: they are read whole and trimmed by the plugin.
        Assert.All(server.Requests.Where(r => r.Path.StartsWith("/investments", StringComparison.Ordinal)), r =>
            Assert.DoesNotContain(r.QueryNames(), n => n.Contains("date", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, Row(windowed, CdbKey)["sent"]!.GetValue<int>()); // nothing of the CDB in the window
        Assert.Equal(2, Row(windowed, CdbKey)["lines"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_pending_purchase_before_the_window_still_counts_in_the_card_balance()
    {
        var server = FakePluggyServer.WithFixtures();
        using var host = new PluggyTestHost(server, new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero)));

        // Window start 10 Nov → bank read from 2 Nov; the card from 62 days before 12 Nov, which holds the pending d03 (13 Oct).
        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero)), Ct);

        Assert.Equal("2026-11-02", server.TransactionRequests(FakePluggyServer.BankAccountId).Single().Query("dateFrom"));
        Assert.Equal("2026-09-11", server.TransactionRequests(FakePluggyServer.CardAccountId).Single().Query("dateFrom"));
        var card = Row(result, CardAccountKey);
        Assert.Equal(0, card["sent"]!.GetValue<int>());
        Assert.Equal(42m, card["pendingAmount"]!.GetValue<decimal>());
        Assert.Equal(89.9m, card["balance"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task An_item_without_the_investments_product_reads_no_positions()
    {
        var server = WithProducts("ACCOUNTS", "CREDIT_CARDS", "TRANSACTIONS");

        var result = await FullSync(server);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.DoesNotContain(server.Requests, r => r.Path.StartsWith("/investments", StringComparison.Ordinal));
        Assert.DoesNotContain(result.ImportEvents, e => e.ExternalAccountId.Contains(":inv:", StringComparison.Ordinal));
        Assert.Equal("not an item product", Diagnostics(result)["sourceDiagnostics"]!["investmentsSkipped"]!.GetValue<string>());
        Assert.Equal(0, Diagnostics(result)["transferPairs"]!["investment"]!["paired"]!.GetValue<int>());
        Assert.Equal(2, Diagnostics(result)["transferPairs"]!["investment"]!["unmatched"]!.GetValue<int>());
    }

    [Fact]
    public async Task Positions_are_listed_without_movements_when_the_item_lacks_investment_transactions()
    {
        var server = WithProducts("ACCOUNTS", "CREDIT_CARDS", "TRANSACTIONS", "INVESTMENTS");

        var result = await FullSync(server);

        Assert.Single(server.RequestsTo("/investments"));
        Assert.DoesNotContain(server.Requests, r => r.Path.EndsWith("/transactions", StringComparison.Ordinal) && r.Path.StartsWith("/investments", StringComparison.Ordinal));
        Assert.Equal(0, Row(result, CdbKey)["lines"]!.GetValue<int>());
        Assert.Equal("movements: not an item product", Diagnostics(result)["sourceDiagnostics"]!["investmentsSkipped"]!.GetValue<string>());
    }

    [Fact]
    public async Task Investments_the_application_may_not_read_are_skipped_without_failing_the_sync()
    {
        var server = FakePluggyServer.WithFixtures().On("/investments", (_, _) =>
            FakePluggyServer.Json("""{"message":"Product not enabled","code":403}""", HttpStatusCode.Forbidden));

        var result = await FullSync(server);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Equal(11, result.ImportEvents.Count); // the bank and card lines
        Assert.Equal("not allowed for this application", Diagnostics(result)["sourceDiagnostics"]!["investmentsSkipped"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_investment_without_a_balance_fails_as_upstream()
    {
        var server = FakePluggyServer.WithFixtures().On("/investments", (_, _) =>
        {
            var page = JsonNode.Parse(Fixtures.Text("investments.json"))!;
            page["results"]![0]!["balance"] = null;
            return FakePluggyServer.Json(page.ToJsonString());
        });

        var result = await FullSync(server);

        Assert.Equal(IntegrationErrorKind.Upstream, result.ErrorKind);
    }

    [Fact]
    public void Positions_that_would_share_an_identity_fall_back_to_pluggys_id()
    {
        static PluggyInvestment Position(string id, string? number, string? isin = null) =>
            new(id, "FIXED_INCOME", "CDB", "CDB", number, null, isin, "BRL", 1m, null, null, null, null, null,
                DateTimeOffset.UnixEpoch, null, null, null, null, null, null, null);

        var identities = PluggyOpenFinanceSource.InvestmentIdentities(
            [Position("p-1", "000123-4"), Position("p-2", "0001234"), Position("p-3", null, "BREXMPACNOR0"), Position("p-4", " ")],
            "77");

        Assert.Equal(("pluggy", "p-1"), identities["p-1"]);
        Assert.Equal(("pluggy", "p-2"), identities["p-2"]);
        Assert.Equal(("isin", "BREXMPACNOR0"), identities["p-3"]);
        Assert.Equal(("pluggy", "p-4"), identities["p-4"]);
    }

    [Theory]
    [InlineData("2026-10-20", "2026-10-20")]
    [InlineData("2026-10-20T00:00:00.000Z", "2026-10-20")]
    [InlineData("20/10/2026", null)]
    [InlineData(null, null)]
    public void Card_bill_dates_are_read_in_either_documented_form(string? value, string? expected)
    {
        Assert.Equal(expected, PluggyOpenFinanceSource.DatePart(value)?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }
}
