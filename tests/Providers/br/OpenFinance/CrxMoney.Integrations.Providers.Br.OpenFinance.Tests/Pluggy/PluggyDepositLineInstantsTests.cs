using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy.Support;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy;

/// <summary>
/// Deposit lines keep the issuer's time when the source gives one (#1477). A statement's day is what a person reconciles
/// against; a time is extra, never a different day: a deposit line is at its instant when that instant carries a time
/// and falls on its statement day in Brazil, else at midnight in Brazil of that day. Card lines keep their own rule
/// (#1476) and no key changes. Every line is synthetic.
/// </summary>
public sealed class PluggyDepositLineInstantsTests
{
    private const string BankAccountKey = "br:077:0001:123456789";
    private const string CardAccountKey = "br:077:card:1234";

    private static readonly TimeSpan Brazil = TimeSpan.FromHours(-3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A posted Pluggy checking line whose <c>date</c> is <paramref name="date"/>, as Pluggy writes it.</summary>
    private static JsonNode BankLine(string suffix, string date, string amount, string type = "CREDIT") => JsonNode.Parse($$"""
        {
          "id": "00000000-0000-4000-8000-000000000{{suffix}}",
          "accountId": "00000000-0000-4000-8000-0000000000b1",
          "date": "{{date}}",
          "description": "Synthetic Pix",
          "descriptionRaw": "SYNTHETIC PIX",
          "amount": {{amount}},
          "currencyCode": "BRL",
          "type": "{{type}}",
          "status": "POSTED",
          "operationType": "PIX"
        }
        """)!;

    /// <summary>A posted Pluggy card purchase whose <c>date</c> is a plain day and whose metadata carries the instant.</summary>
    private static JsonNode CardLine(string suffix, string date, string transactionDateTime, string amount) => JsonNode.Parse($$"""
        {
          "id": "00000000-0000-4000-8000-000000000{{suffix}}",
          "accountId": "00000000-0000-4000-8000-0000000000b2",
          "date": "{{date}}",
          "description": "Synthetic Store",
          "descriptionRaw": "SYNTHETIC STORE",
          "amount": {{amount}},
          "currencyCode": "BRL",
          "type": "DEBIT",
          "status": "POSTED",
          "creditCardMetadata": {
            "cardNumber": "1234",
            "paymentType": "SINGLE",
            "transactionDateTime": "{{transactionDateTime}}"
          }
        }
        """)!;

    private static async Task<Dictionary<string, IntegrationImportEvent>> SyncAsync(JsonArray? bankLines = null, JsonArray? cardLines = null)
    {
        var server = FakePluggyServer.WithFixtures();
        server.BankLines = bankLines;
        server.CardLines = cardLines;
        using var host = new PluggyTestHost(server);
        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);
        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        return result.ImportEvents.ToDictionary(e => e.ExternalId);
    }

    /// <summary>The recipe of every version up to preview.13, written out here so the test does not lean on the mapper.</summary>
    private static string Preview13Key(string lineIdentity, int ordinal = 0) =>
        "brof:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{lineIdentity}|{ordinal}")))[..32];

    [Fact]
    public async Task A_deposit_line_with_a_time_on_its_statement_day_is_dated_at_that_time_in_brazil()
    {
        // 2026-09-08T17:45:12Z is 14:45:12 on 2026-09-08 in Brazil: same day, so the time is kept.
        var events = await SyncAsync(bankLines: [BankLine("f01", "2026-09-08T17:45:12.000Z", "321.00")]);

        var line = events["00000000-0000-4000-8000-000000000f01"];
        Assert.Equal("deposit", line.Type);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 14, 45, 12, Brazil), line.OccurredAtUtc);
        Assert.Equal(Brazil, line.OccurredAtUtc.Offset);
    }

    [Fact]
    public async Task A_deposit_line_with_a_plain_day_stays_at_midnight_in_brazil()
    {
        var events = await SyncAsync(bankLines:
        [
            // Pluggy's plain day: midnight in Brazil written in UTC.
            BankLine("f02", "2026-09-09T03:00:00.000Z", "12.34"),
            // Midnight UTC is a plain day too, never 21:00 of the day before; the line keeps its booking date (as before).
            BankLine("f03", "2026-09-10T00:00:00.000Z", "56.78", type: "DEBIT"),
        ]);

        Assert.Equal(new DateTimeOffset(2026, 9, 9, 0, 0, 0, Brazil), events["00000000-0000-4000-8000-000000000f02"].OccurredAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 0, 0, 0, Brazil), events["00000000-0000-4000-8000-000000000f03"].OccurredAtUtc);
    }

    [Fact]
    public void A_time_whose_brazil_day_differs_from_the_statement_date_stays_at_midnight_of_the_statement_date()
    {
        var statementDate = new DateOnly(2026, 9, 8);
        var midnight = new DateTimeOffset(2026, 9, 8, 0, 0, 0, Brazil);

        // 01:10 on 2026-09-09 in Brazil: the next day.
        Assert.Equal(midnight, OpenFinanceTransactionMapper.DepositEventTime(statementDate, new DateTimeOffset(2026, 9, 9, 4, 10, 0, TimeSpan.Zero)));
        // 23:30 on 2026-09-07 in Brazil, though its UTC day is 2026-09-08: the day before.
        Assert.Equal(midnight, OpenFinanceTransactionMapper.DepositEventTime(statementDate, new DateTimeOffset(2026, 9, 8, 2, 30, 0, TimeSpan.Zero)));
        // On the statement day the time is kept, whatever offset the instant was written in.
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 22, 30, 0, Brazil),
            OpenFinanceTransactionMapper.DepositEventTime(statementDate, new DateTimeOffset(2026, 9, 9, 1, 30, 0, TimeSpan.Zero)));
        // A plain day is midnight of the statement date.
        Assert.Equal(midnight, OpenFinanceTransactionMapper.DepositEventTime(statementDate, BrazilBankingCalendar.StartOfDay(statementDate)));
    }

    [Fact]
    public async Task A_card_line_is_unchanged_at_the_issuers_instant()
    {
        // The card's instant (18:05:40 Brazil) is kept as in #1476; the plain-day rule of deposit lines does not apply.
        var events = await SyncAsync(cardLines: [CardLine("f04", "2026-09-12T03:00:00.000Z", "2026-09-12T21:05:40.000Z", "45.60")]);

        var card = events["00000000-0000-4000-8000-000000000f04"];
        Assert.Equal(CardAccountKey, card.ExternalAccountId);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 18, 5, 40, Brazil), card.OccurredAtUtc);
        Assert.Equal(Preview13Key($"{CardAccountKey}|2026-09-12T21:05:40Z|D|45.60"), card.IdempotencyKey);
    }

    [Fact]
    public async Task The_key_of_a_deposit_line_is_identical_before_and_after()
    {
        var events = await SyncAsync(bankLines:
        [
            BankLine("f05", "2026-09-08T17:45:12.000Z", "321.00"),
            BankLine("f06", "2026-09-09T03:00:00.000Z", "12.34", type: "DEBIT"),
        ]);

        // Up to preview.13 both lines were at midnight; the key hashed the instant to the second then as now.
        var timed = events["00000000-0000-4000-8000-000000000f05"];
        Assert.NotEqual(TimeSpan.Zero, timed.OccurredAtUtc.TimeOfDay);
        Assert.Equal(Preview13Key($"{BankAccountKey}|2026-09-08T17:45:12Z|C|321.00"), timed.IdempotencyKey);
        Assert.Equal(Preview13Key($"{BankAccountKey}|2026-09-09T03:00:00Z|D|12.34"), events["00000000-0000-4000-8000-000000000f06"].IdempotencyKey);
    }

    [Fact]
    public void A_host_re_dating_a_stored_deposit_line_gets_the_live_reads_instant()
    {
        // A line stored at midnight of its booking date with its instant in transactionTime: the stored day and the
        // stored instant give what a live read of the same line gives today.
        var account = new OpenFinanceAccount("synthetic-account", OpenFinanceAccountKind.Deposit,
            BrazilianBankAccount.Create("077", "0001", "12345678-9"), "077", null, "BRL", null);
        var instant = new DateTimeOffset(2026, 9, 8, 17, 45, 12, TimeSpan.Zero);
        var transaction = new OpenFinanceTransaction("f07", instant, OpenFinanceDirection.Credit, 321.00m, "PIX", null, null, null, null,
            false, null, System.Text.Json.JsonDocument.Parse("""{"id":"f07"}""").RootElement);

        var live = OpenFinanceTransactionMapper.EventTime(account, transaction);
        var stored = OpenFinanceTransactionMapper.DepositEventTime(DateOnly.Parse("2026-09-08", CultureInfo.InvariantCulture), instant);

        Assert.Equal(live, stored);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 14, 45, 12, Brazil), stored);
    }
}
