using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy.Support;
using Microsoft.Extensions.Time.Testing;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy;

/// <summary>
/// Card lines at the issuer's instant (#1476) and card purchases that post long after their date (#1475). Every line is
/// synthetic. A card purchase stays PENDING at Pluggy until its bill closes, then turns POSTED under the same id with its
/// <c>date</c> and <c>createdAt</c> unchanged and <c>updatedAt</c> at the closing; the shared plugin sends it on the read
/// after the closing, however old its date, and reports the pending lines it skipped.
/// </summary>
public sealed class PluggyCardLineInstantsTests
{
    private const string CardAccountKey = "br:077:card:1234";

    /// <summary>The fixtures' clock is 2026-10-14; this window sends from 2026-10-05 (seven days of overlap).</summary>
    private static readonly DateTimeOffset WindowStart = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A plain card purchase made on 2026-09-20 at 15:42:10 in Brazil, first stored by Pluggy the day after.</summary>
    private static JsonNode Purchase(string status, string? updatedAt, string id = "00000000-0000-4000-8000-000000000d71") => JsonNode.Parse($$"""
        {
          "id": "{{id}}",
          "accountId": "00000000-0000-4000-8000-0000000000b2",
          "date": "2026-09-20T03:00:00.000Z",
          "description": "Loja Exemplo",
          "descriptionRaw": "LOJA EXEMPLO",
          "amount": 41.37,
          "currencyCode": "BRL",
          "type": "DEBIT",
          "status": "{{status}}",
          "createdAt": "2026-09-21T16:16:57.488Z",
          "updatedAt": {{(updatedAt is null ? "null" : $"\"{updatedAt}\"")}},
          "creditCardMetadata": {
            "cardNumber": "1234",
            "paymentType": "SINGLE",
            "transactionDateTime": "2026-09-20T18:42:10.000Z"
          }
        }
        """)!;

    private static async Task<IntegrationSyncResult> SyncAsync(JsonArray lines, DateTimeOffset? windowStart)
    {
        var server = FakePluggyServer.WithFixtures();
        server.CardLines = lines;
        using var host = new PluggyTestHost(server);
        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: windowStart), Ct);
        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        return result;
    }

    private static List<IntegrationImportEvent> CardEvents(IntegrationSyncResult result) =>
        [.. result.ImportEvents.Where(e => e.ExternalAccountId == CardAccountKey)];

    private static JsonNode Diagnostics(IntegrationSyncResult result) => JsonNode.Parse(result.ProviderDiagnosticsJson!)!;

    private static JsonNode CardRow(IntegrationSyncResult result) =>
        Diagnostics(result)["accounts"]!.AsArray().Single(a => a!["externalAccountId"]!.GetValue<string>() == CardAccountKey)!;

    [Fact]
    public async Task A_card_line_is_dated_at_the_issuers_instant_with_brazils_offset_and_a_deposit_line_at_midnight()
    {
        var result = await SyncAsync(new JsonArray(Purchase("POSTED", "2026-09-22T16:21:37.586Z")), windowStart: null);

        var card = Assert.Single(CardEvents(result));
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 15, 42, 10, TimeSpan.FromHours(-3)), card.OccurredAtUtc);
        Assert.Equal(TimeSpan.FromHours(-3), card.OccurredAtUtc.Offset);
        // The key is the one of before #1476: the instant to the second, never the event's date.
        Assert.Equal(OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2026-09-20T18:42:10Z|D|41.37", 0), card.IdempotencyKey);

        // Deposit lines are out of #1476: still at midnight in Brazil of their booking date.
        var deposits = result.ImportEvents.Where(e => e.ExternalAccountId.StartsWith("br:077:0", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(deposits);
        Assert.All(deposits, e => Assert.Equal(TimeSpan.Zero, e.OccurredAtUtc.TimeOfDay));
    }

    [Fact]
    public async Task A_purchase_pending_at_the_full_read_and_posted_at_the_bills_closing_is_sent_on_the_next_incremental_read()
    {
        // The full read while the bill is open: the purchase is pending, skipped and reported.
        var full = await SyncAsync(new JsonArray(Purchase("PENDING", "2026-09-21T16:16:57.488Z")), windowStart: null);
        Assert.Empty(CardEvents(full));
        Assert.Equal(1, CardRow(full)["pendingSkipped"]!.GetValue<int>());
        Assert.Equal("2026-09-20", CardRow(full)["pendingOldestDate"]!.GetValue<string>());
        Assert.Equal("2026-09-20", Diagnostics(full)["pendingOldestDate"]!.GetValue<string>());

        // The bill closes on 2026-10-13: Pluggy posts it under the same id, updatedAt at the closing. The incremental
        // read sends from 2026-10-05, long after its date, and still sends it, with the key a full read gives it.
        var incremental = await SyncAsync(new JsonArray(Purchase("POSTED", "2026-10-13T16:21:37.586Z")), WindowStart);
        var sent = Assert.Single(CardEvents(incremental));
        var afterwards = Assert.Single(CardEvents(await SyncAsync(new JsonArray(Purchase("POSTED", "2026-10-13T16:21:37.586Z")), windowStart: null)));
        Assert.Equal(afterwards.IdempotencyKey, sent.IdempotencyKey);
        Assert.Equal(afterwards.OccurredAtUtc, sent.OccurredAtUtc);

        // The run details say why and from when.
        var diagnostics = Diagnostics(incremental);
        Assert.Equal("2026-10-05", diagnostics["sentFrom"]!.GetValue<string>());
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero), DateTimeOffset.Parse(diagnostics["sentChangedFromUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, diagnostics["sentByChange"]!.GetValue<int>());
        Assert.Equal(1, CardRow(incremental)["sentByChange"]!.GetValue<int>());
        Assert.Equal(0, CardRow(incremental)["pendingSkipped"]!.GetValue<int>());
        Assert.Null(CardRow(incremental)["pendingOldestDate"]);
    }

    [Fact]
    public async Task A_sync_seventy_days_after_the_last_success_still_reads_and_sends_a_purchase_that_was_pending_then()
    {
        // The last success started its window on 2026-10-12, the purchase of 2026-09-20 pending then; the next sync comes
        // 70 days later (a consent lapse), after the bill closed on 2026-11-05. The card is read from 62 days before the
        // window's read date (2026-08-03), not before today (2026-10-12 would miss the line).
        var server = FakePluggyServer.WithFixtures();
        server.CardLines = new JsonArray(Purchase("POSTED", "2026-11-05T16:21:37.586Z"));
        using var host = new PluggyTestHost(server, new FakeTimeProvider(new DateTimeOffset(2026, 12, 21, 12, 0, 0, TimeSpan.Zero)));

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: WindowStart), Ct);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Single(CardEvents(result));
        Assert.Equal("2026-08-03", Diagnostics(result)["sourceDiagnostics"]!["cardTransactionsDateFrom"]!.GetValue<string>());
        Assert.Equal(1, Diagnostics(result)["sentByChange"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_posted_line_dated_and_changed_before_the_window_is_not_sent_again()
    {
        var result = await SyncAsync(new JsonArray(Purchase("POSTED", "2026-09-22T16:21:37.586Z")), WindowStart);

        Assert.Empty(CardEvents(result));
        Assert.Equal(0, Diagnostics(result)["sentByChange"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_line_without_created_or_updated_instants_is_sent_by_its_dates_only()
    {
        var line = Purchase("POSTED", updatedAt: null);
        line.AsObject().Remove("createdAt");

        Assert.Empty(CardEvents(await SyncAsync(new JsonArray(line), WindowStart)));
    }

    [Fact]
    public async Task A_line_stored_by_the_source_inside_the_window_is_sent_whatever_its_date()
    {
        // createdAt alone (no updatedAt) in the window: a line the host cannot have seen before.
        var line = Purchase("POSTED", updatedAt: null);
        line["createdAt"] = "2026-10-06T10:00:00.000Z";

        var result = await SyncAsync(new JsonArray(line), WindowStart);

        Assert.Single(CardEvents(result));
        Assert.Equal(1, Diagnostics(result)["sentByChange"]!.GetValue<int>());
    }
}
