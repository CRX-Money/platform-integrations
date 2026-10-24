using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy.Support;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy;

/// <summary>
/// A card's bills (#1386): read from <c>GET /bills?accountId=</c>, reported on the card's diagnostics row so the host
/// can set its ledger against the invoice, and never the reason a sync fails. Every bill is synthetic.
/// </summary>
public sealed class PluggyCardBillTests
{
    private const string CardAccountKey = "br:077:card:1234";
    private const string BankAccountKey = "br:077:0001:123456789";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonNode Diagnostics(IntegrationSyncResult result) => JsonNode.Parse(result.ProviderDiagnosticsJson!)!;

    private static JsonNode Row(IntegrationSyncResult result, string externalAccountId) =>
        Diagnostics(result)["accounts"]!.AsArray().Single(a => a!["externalAccountId"]!.GetValue<string>() == externalAccountId)!;

    private static async Task<IntegrationSyncResult> SyncAsync(FakePluggyServer server, DateTimeOffset? windowStart = null)
    {
        using var host = new PluggyTestHost(server);
        return await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: windowStart), Ct);
    }

    [Fact]
    public async Task A_cards_row_carries_its_bills_oldest_due_date_first()
    {
        var result = await SyncAsync(FakePluggyServer.WithFixtures());

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        var bills = Row(result, CardAccountKey)["bills"]!.AsArray();
        // The bill without an id is skipped.
        Assert.Equal(2, bills.Count);

        var older = bills[0]!;
        Assert.Equal("00000000-0000-4000-8000-0000000000e0", older["billId"]!.GetValue<string>());
        Assert.Equal("2026-09-01", older["closeDate"]!.GetValue<string>());
        Assert.Equal("2026-09-10", older["dueDate"]!.GetValue<string>());
        Assert.Equal(171.13m, older["totalAmount"]!.GetValue<decimal>());
        Assert.Null(older["minimumPayment"]);
        Assert.Equal("BRL", older["currencyCode"]!.GetValue<string>());
        Assert.Equal(0, older["postedLinesRead"]!.GetValue<int>());

        var current = bills[1]!;
        Assert.Equal("00000000-0000-4000-8000-0000000000e1", current["billId"]!.GetValue<string>());
        Assert.Null(current["closeDate"]);
        Assert.Equal("2026-10-10", current["dueDate"]!.GetValue<string>());
        Assert.Equal(89.90m, current["totalAmount"]!.GetValue<decimal>());
        Assert.Equal(13.49m, current["minimumPayment"]!.GetValue<decimal>());
        // The purchase and the payment received name the bill; the pending purchase names none.
        Assert.Equal(2, current["postedLinesRead"]!.GetValue<int>());

        Assert.Null(Row(result, BankAccountKey)["bills"]);
        var source = Diagnostics(result)["sourceDiagnostics"]!;
        Assert.Equal(0, source["cardsWithoutBills"]!.GetValue<int>());
        Assert.Equal(1, source["billPages"]!.GetValue<int>());
    }

    [Fact]
    public async Task Bills_are_read_for_cards_only_and_whatever_the_window()
    {
        var server = FakePluggyServer.WithFixtures();

        await SyncAsync(server, windowStart: new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));

        var request = Assert.Single(server.RequestsTo("/bills"));
        Assert.Equal(FakePluggyServer.CardAccountId, request.Query("accountId"));
        Assert.Equal("1", request.Query("page"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    // A 5xx is retried by the HTTP pipeline before it fails; the test host's fake clock would hold the backoff forever.
    public async Task A_bills_read_that_fails_leaves_the_card_without_bills_and_the_sync_succeeds(HttpStatusCode status)
    {
        var server = FakePluggyServer.WithFixtures().On("/bills", (_, _) => FakePluggyServer.Status(status));

        var result = await SyncAsync(server);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.NotEmpty(result.ImportEvents);
        Assert.Null(Row(result, CardAccountKey)["bills"]);
        Assert.Equal(1, Diagnostics(result)["sourceDiagnostics"]!["cardsWithoutBills"]!.GetValue<int>());
    }

    [Fact]
    public void A_bill_reads_amounts_as_numbers_or_strings_and_dates_as_calendar_dates()
    {
        using var document = JsonDocument.Parse("""
            {"id":" bill-1 ","dueDate":"2026-11-05T00:00:00.000Z","totalAmount":"-250.40","minimumPaymentAmount":37.56,"closingDate":"2026-10-27"}
            """);

        var bill = PluggyOpenFinanceSource.NormalizeBill(document.RootElement)!;

        Assert.Equal("bill-1", bill.Id);
        Assert.Equal(new DateOnly(2026, 11, 5), bill.DueDate);
        Assert.Equal(250.40m, bill.TotalAmount);
        Assert.Equal(37.56m, bill.MinimumPayment);
        Assert.Equal(new DateOnly(2026, 10, 27), bill.CloseDate);
        Assert.Null(bill.CurrencyCode);
    }
}
