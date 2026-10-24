using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy.Api;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy.Support;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy;

/// <summary>
/// Installments (parcelas) on card lines (#1417): Pluggy's <c>creditCardMetadata</c> installment fields become
/// <c>card.installment</c> and the other <c>card</c> keys, each installment is dated from its bill, and the idempotency
/// key does not move. Every line of <c>transactions-card-installments.json</c> is synthetic, in the shape Pluggy sends
/// for k &gt;= 2: <c>date</c> is the bill post date at 03:00Z while <c>transactionDateTime</c> and <c>purchaseDate</c>
/// are the purchase instant.
/// </summary>
public sealed class PluggyCardInstallmentTests
{
    private const string CardAccountKey = "br:077:card:1234";

    private static readonly OpenFinanceAccount Card1234 = new("card", OpenFinanceAccountKind.CreditCard, null, "077", "1234", "BRL", null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonArray InstallmentLines() =>
        JsonNode.Parse(Fixtures.Text("transactions-card-installments.json"))!["results"]!.AsArray();

    private static async Task<IntegrationSyncResult> SyncAsync(JsonArray lines, DateTimeOffset? windowStart = null)
    {
        var server = FakePluggyServer.WithFixtures();
        server.CardLines = lines;
        using var host = new PluggyTestHost(server);
        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: windowStart), Ct);
        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, IntegrationImportEvent>> CardEventsAsync(JsonArray lines) =>
        CardEvents(await SyncAsync(lines));

    private static IReadOnlyDictionary<string, IntegrationImportEvent> CardEvents(IntegrationSyncResult result) =>
        result.ImportEvents
            .Where(e => e.ExternalAccountId == CardAccountKey)
            .ToDictionary(e => e.ExternalId[^3..], StringComparer.Ordinal);

    private static JsonNode Card(IntegrationImportEvent e) => JsonNode.Parse(e.ProviderData!)!["card"]!;

    private static string Date(IntegrationImportEvent e) =>
        BrazilBankingCalendar.DateOf(e.OccurredAtUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Text(JsonNode? node) => node?.GetValue<string>();

    /// <summary>A posted card debit booked on 2026-03-02 in Brazil.</summary>
    private static OpenFinanceTransaction CardLine(OpenFinanceCardLineDetails details, string? description, string? billId = null) =>
        new("t", new DateTimeOffset(2026, 3, 2, 19, 10, 0, TimeSpan.Zero), OpenFinanceDirection.Debit, 10m,
            "CARD", null, description, null, null, false, billId, JsonDocument.Parse("{}").RootElement)
        {
            CardDetails = details,
        };

    [Fact]
    public async Task An_open_finance_installment_carries_its_installment_and_is_dated_at_its_bill_post_date()
    {
        var e = (await CardEventsAsync(InstallmentLines()))["f01"];
        var card = Card(e);

        // The earlier keys are untouched and come first.
        Assert.Equal(["last4", "billId", "issuerCompe", "paymentType", "billPostDate", "billForecastDate", "payeeMcc", "installment"],
            card.AsObject().Select(p => p.Key));
        Assert.Equal("1234", Text(card["last4"]));
        Assert.Equal("00000000-0000-4000-8000-0000000000e7", Text(card["billId"]));
        Assert.Equal("077", Text(card["issuerCompe"]));
        Assert.Equal("INSTALLMENT", Text(card["paymentType"]));
        Assert.Equal("2025-10-26", Text(card["billPostDate"]));
        Assert.Equal("2025-11-10", Text(card["billForecastDate"]));
        Assert.Equal("5712", Text(card["payeeMcc"]));

        var installment = card["installment"]!;
        Assert.Equal(["number", "count", "purchaseDate", "source", "dateSource"], installment.AsObject().Select(p => p.Key));
        Assert.Equal(1, installment["number"]!.GetValue<int>());
        Assert.Equal(6, installment["count"]!.GetValue<int>());
        Assert.Equal("2025-10-25", Text(installment["purchaseDate"]));
        Assert.Null(installment["totalAmount"]); // Open Finance reports no total
        Assert.Equal("metadata", Text(installment["source"]));
        Assert.Equal("billPostDate", Text(installment["dateSource"]));

        // Dated at the bill post date; transactionTime and transactionDate still say when the bank dated the line.
        Assert.Equal("2025-10-26", Date(e));
        var data = JsonNode.Parse(e.ProviderData!)!;
        Assert.Equal("2025-10-25", Text(data["transactionDate"]));
        Assert.Equal("LOJA EXEMPLO*DECOR01/06", Text(data["description"]));
        Assert.NotNull(data["sourcePayload"]);
        Assert.Equal(32.69m, e.Amount); // the installment, never the purchase
    }

    [Fact]
    public async Task The_installments_of_one_purchase_share_one_line_identity_and_differ_only_by_ordinal()
    {
        var events = await CardEventsAsync(InstallmentLines());

        // 01/06 and 02/06: same purchase instant and amount, so one line identity; the ordinal follows Pluggy's ids.
        const string identity = $"{CardAccountKey}|2025-10-25T20:22:37Z|D|32.69";
        Assert.Equal(OpenFinanceTransactionMapper.IdempotencyKey(identity, 0), events["f01"].IdempotencyKey);
        Assert.Equal(OpenFinanceTransactionMapper.IdempotencyKey(identity, 1), events["f10"].IdempotencyKey);
        Assert.Equal("2025-10-28", Date(events["f10"]));
        Assert.Equal("billPostDate", Text(Card(events["f10"])["installment"]!["dateSource"]));
    }

    [Fact]
    public async Task A_direct_connector_installment_also_carries_the_purchase_total()
    {
        var e = (await CardEventsAsync(InstallmentLines()))["f02"];
        var card = Card(e);
        var installment = card["installment"]!;

        Assert.Equal(2, installment["number"]!.GetValue<int>());
        Assert.Equal(3, installment["count"]!.GetValue<int>());
        Assert.Equal("2026-08-14", Text(installment["purchaseDate"]));
        Assert.Equal(600m, installment["totalAmount"]!.GetValue<decimal>());
        Assert.Equal("metadata", Text(installment["source"]));
        Assert.Equal("0742", Text(card["payeeMcc"])); // a string MCC, padded to four digits
        Assert.Equal("2026-09-14", Text(card["billPostDate"])); // a date sent as a date-time keeps its date
        Assert.Equal("2026-09-14", Date(e));
        Assert.Equal(200m, e.Amount);
    }

    [Fact]
    public async Task A_single_purchase_keeps_its_date_and_has_no_installment()
    {
        var e = (await CardEventsAsync(InstallmentLines()))["f03"];
        var card = Card(e);

        Assert.Equal("SINGLE", Text(card["paymentType"]));
        Assert.Equal("2026-09-05", Text(card["billPostDate"]));
        Assert.Equal("5411", Text(card["payeeMcc"]));
        Assert.Null(card["installment"]);
        Assert.Equal("2026-09-03", Date(e));
    }

    [Fact]
    public async Task A_fee_in_installments_is_an_installment_with_or_without_metadata()
    {
        var events = await CardEventsAsync(InstallmentLines());

        // TARIFA with installmentNumber/totalInstallments and no payment type: the metadata decides.
        var withMetadata = events["f04"];
        var installment = Card(withMetadata)["installment"]!;
        Assert.Equal(4, installment["number"]!.GetValue<int>());
        Assert.Equal(10, installment["count"]!.GetValue<int>());
        Assert.Equal("2025-12-01", Text(installment["purchaseDate"])); // no purchaseDate: the line's own date
        Assert.Equal("metadata", Text(installment["source"]));
        Assert.Null(Card(withMetadata)["paymentType"]);
        Assert.Equal("2026-03-28", Date(withMetadata));

        // The same fee without the metadata: "ANUIDADE DIFERENCI05/10" is read from the description.
        var fromDescription = events["f07"];
        installment = Card(fromDescription)["installment"]!;
        Assert.Equal(5, installment["number"]!.GetValue<int>());
        Assert.Equal(10, installment["count"]!.GetValue<int>());
        Assert.Equal("description", Text(installment["source"]));
        Assert.Equal("2026-04-28", Date(fromDescription));
    }

    [Theory]
    [InlineData("f05", 1, 2, "2026-07-18", "2026-07-20")]    // "TINTAS EXEMPLO 01/02": spaced
    [InlineData("f06", 10, 12, "2025-12-24", "2026-10-02")]  // "EC *EXEMPLOINFORMAT10/12": compact
    public async Task Without_metadata_the_description_suffix_gives_the_installment(string id, int number, int count, string purchaseDate, string eventDate)
    {
        var e = (await CardEventsAsync(InstallmentLines()))[id];
        var installment = Card(e)["installment"]!;

        Assert.Equal(number, installment["number"]!.GetValue<int>());
        Assert.Equal(count, installment["count"]!.GetValue<int>());
        Assert.Equal(purchaseDate, Text(installment["purchaseDate"]));
        Assert.Equal("description", Text(installment["source"]));
        Assert.Equal("billPostDate", Text(installment["dateSource"]));
        Assert.Equal(eventDate, Date(e));
    }

    [Theory]
    // The tail of a long series: billPostDate equals the purchase date, so the bill dates the line.
    [InlineData("f09", "bill", "2026-09-01")]               // bill e0, read with its closing date
    [InlineData("f11", "bill", "2026-10-10")]               // bill e1 has no closing date: its due date
    [InlineData("f12", "billForecastDate", "2026-08-01")]   // a bill the read did not list: billForecastDate's month
    [InlineData("f08", "purchase", "2026-08-12")]           // on no bill yet (placeholder billPostDate, no bill, no forecast)
    public async Task An_installment_without_a_later_bill_post_date_is_dated_from_its_bill(string id, string dateSource, string eventDate)
    {
        var e = (await CardEventsAsync(InstallmentLines()))[id];
        var installment = Card(e)["installment"]!;

        Assert.Equal(dateSource, Text(installment["dateSource"]));
        Assert.Equal(eventDate, Date(e));
        Assert.True(string.CompareOrdinal(eventDate, Text(installment["purchaseDate"])) >= 0); // never before the purchase
    }

    [Fact]
    public async Task The_placeholder_bill_post_date_is_not_reported()
    {
        var e = (await CardEventsAsync(InstallmentLines()))["f08"];

        Assert.Null(Card(e)["billPostDate"]);
        Assert.Equal("2026-08-12", Text(Card(e)["installment"]!["purchaseDate"]));
    }

    [Fact]
    public async Task Redating_an_installment_does_not_change_its_idempotency_key()
    {
        var dated = InstallmentLines();
        var undated = InstallmentLines();
        foreach (var name in new[] { "billPostDate", "billForecastDate", "paymentType", "installmentNumber", "totalInstallments", "purchaseDate" })
            undated[0]!["creditCardMetadata"]!.AsObject().Remove(name);
        undated[0]!["descriptionRaw"] = "LOJA EXEMPLO*DECOR";

        var withBill = (await CardEventsAsync(dated))["f01"];
        var withoutBill = (await CardEventsAsync(undated))["f01"];

        Assert.Equal("2025-10-26", Date(withBill));
        Assert.Equal("2025-10-25", Date(withoutBill));
        Assert.Null(Card(withoutBill)["installment"]);
        // Same line, same set of lines read, new date: the key is the one the transaction instant gives, as before #1417.
        Assert.Equal(withoutBill.IdempotencyKey, withBill.IdempotencyKey);
        Assert.Equal(OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2025-10-25T20:22:37Z|D|32.69", 0), withBill.IdempotencyKey);
    }

    /// <summary>
    /// Pins today's behaviour, a known gap: story #1424. A k &gt;= 2 installment of an older purchase arrives with
    /// Pluggy's <c>date</c> inside an incremental read, but the plugin windows lines by the purchase instant
    /// (<c>transactionDateTime</c>), so it is read and not sent. Sending it would not be safe either: the series shares
    /// one line identity, so its keys depend on which installments a read holds. Only a full read sends it.
    /// </summary>
    [Fact]
    public async Task An_installment_of_an_older_purchase_is_not_sent_on_an_incremental_read()
    {
        var line = InstallmentLines().Single(l => Text(l!["id"])!.EndsWith("f10", StringComparison.Ordinal))!.DeepClone();
        line["date"] = "2026-10-08T03:00:00.000Z";
        line["creditCardMetadata"]!["billPostDate"] = "2026-10-08";

        var incremental = await SyncAsync(new JsonArray(line.DeepClone()), windowStart: new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));

        var row = JsonNode.Parse(incremental.ProviderDiagnosticsJson!)!["accounts"]!.AsArray()
            .Single(a => Text(a!["externalAccountId"]) == CardAccountKey)!;
        Assert.Equal(1, row["posted"]!.GetValue<int>()); // read...
        Assert.Equal(0, row["sent"]!.GetValue<int>());   // ...but not sent (#1424)
        Assert.Empty(CardEvents(incremental));

        var full = CardEvents(await SyncAsync(new JsonArray(line.DeepClone())));
        Assert.Equal("2026-10-08", Date(full["f10"]));
    }

    [Theory]
    [InlineData("INSTALLMENT", 1, 6, null, true)]
    [InlineData("A_PRAZO", 2, 6, null, true)]          // Open Finance's own name
    [InlineData(null, 4, 10, null, true)]              // no payment type (Pluggy sends none on some card lines)
    [InlineData("SINGLE", 1, 6, "LOJA 01/06", false)]  // the source's SINGLE wins over its numbers and the description
    [InlineData("SINGLE", null, null, "LOJA 01/06", false)] // and over the description alone
    [InlineData("A_VISTA", null, null, "LOJA 01/06", false)]
    [InlineData("INSTALLMENT", 1, 1, "LOJA 01/06", false)] // one installment is a single purchase; the metadata decides
    [InlineData("INSTALLMENT", 7, 6, null, false)]     // number past the count
    [InlineData(null, null, null, "LOJA EXEMPLO 01/02", true)]
    [InlineData(null, null, null, "LOJA EXEMPLO*DECOR06/06", true)]
    [InlineData(null, null, null, "LOJA EXEMPLO 123/12", false)] // a digit before the suffix: not an installment
    [InlineData(null, null, null, "DROGARIA EXEMPLO 15/10", false)] // number past the count
    [InlineData(null, null, null, "LOJA EXEMPLO 00/02", false)]
    [InlineData(null, null, null, "LOJA EXEMPLO ١٢/١٣", false)] // Arabic-Indic digits are not [0-9]
    [InlineData(null, null, null, "LOJA EXEMPLO", false)]
    public void The_shared_mapper_decides_what_an_installment_is(string? paymentType, int? number, int? count, string? description, bool expected)
    {
        var line = CardLine(new OpenFinanceCardLineDetails { PaymentType = paymentType, InstallmentNumber = number, InstallmentCount = count }, description);

        Assert.Equal(expected, OpenFinanceTransactionMapper.Installment(Card1234, line) is not null);

        // A deposit account's line is never an installment, whatever it says.
        var checking = new OpenFinanceAccount("k", OpenFinanceAccountKind.Deposit, BrazilianBankAccount.Create("077", "0001", "12345678-9"), "077", null, "BRL", 0m);
        Assert.Null(OpenFinanceTransactionMapper.Installment(checking, line));
    }

    [Theory]
    // Booked on 2026-03-02 (the purchase), installment 5 of 6, on bill "bill-1".
    [InlineData("2026-03-02", "2026-04-02", null, null, null, "2026-04-02")]                 // billPostDate later than the purchase
    [InlineData(null, "2026-02-27", null, null, null, "2026-03-02")]                         // bad data: never dated back before the purchase
    [InlineData("0001-01-01", "2026-04-02", null, null, null, "2026-04-02")]                 // placeholder purchaseDate: the booking date is the purchase
    [InlineData("0001-01-01", "2026-02-27", null, null, null, "2026-03-02")]
    [InlineData(null, "2026-03-02", "2026-03-01", "2026-03-10", null, "2026-03-02")]         // a bill closing before the purchase, no forecast: keeps its date
    [InlineData(null, "2026-03-02", "2026-03-01", "2026-03-10", "2026-04-10", "2026-04-01")] // ... then billForecastDate's month
    [InlineData(null, "2026-03-02", "2026-03-28", "2026-04-10", null, "2026-03-28")]         // the bill's closing date
    [InlineData(null, "2026-03-02", null, "2026-04-10", null, "2026-04-10")]                 // a bill without closing date: its due date
    [InlineData(null, "0001-01-01", null, null, "2026-03-10", "2026-03-02")]                 // forecast month starts before the purchase: keeps its date
    public void An_installment_is_never_dated_before_its_purchase(string? purchaseDate, string? billPostDate, string? billClose, string? billDue, string? forecast, string expected)
    {
        static DateOnly? D(string? value) => value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var line = CardLine(new OpenFinanceCardLineDetails
        {
            PaymentType = "INSTALLMENT",
            InstallmentNumber = 5,
            InstallmentCount = 6,
            PurchaseDate = D(purchaseDate),
            BillPostDate = D(billPostDate),
            BillForecastDate = D(forecast),
        }, "LOJA EXEMPLO 05/06", billId: "bill-1");
        var bill = billClose is null && billDue is null ? null : new OpenFinanceCardBill("bill-1", D(billDue), 100m, CloseDate: D(billClose));

        Assert.Equal(D(expected), OpenFinanceTransactionMapper.EventDate(Card1234, line, bill));
    }

    [Fact]
    public void A_bill_dates_a_line_only_when_the_line_names_it()
    {
        var line = CardLine(new OpenFinanceCardLineDetails { InstallmentNumber = 5, InstallmentCount = 6 }, null, billId: "bill-2");
        var other = new OpenFinanceCardBill("bill-1", new DateOnly(2026, 4, 10), 100m, CloseDate: new DateOnly(2026, 4, 1));

        Assert.Equal(new DateOnly(2026, 3, 2), OpenFinanceTransactionMapper.EventDate(Card1234, line, other));
    }

    [Theory]
    [InlineData("2025-10-25", "2025-10-25")]
    [InlineData("2025-10-25T00:00:00.000Z", "2025-10-25")]   // a date written as a date-time
    [InlineData("2025-10-25T03:00:00.000Z", "2025-10-25")]   // midnight in São Paulo
    [InlineData("2025-10-25T01:30:00.000Z", "2025-10-24")]   // an instant: Brazil's calendar
    [InlineData("2025-10-25T23:30:00-03:00", "2025-10-25")]
    [InlineData("0001-01-01", null)]                          // Open Finance's placeholder
    [InlineData("0001-01-01T00:00:00.000Z", null)]
    [InlineData("0001-01-01T03:00:00.000Z", null)]
    [InlineData("not a date", null)]
    [InlineData(null, null)]
    public void A_purchase_date_is_read_as_a_date_or_as_an_instant_in_brazil(string? value, string? expected)
    {
        Assert.Equal(expected, PluggyOpenFinanceSource.CalendarDate(value)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("5411", "5411")]
    [InlineData("742", "0742")]
    [InlineData("\"0742\"", "0742")]
    [InlineData("\" 5812 \"", "5812")]
    [InlineData("12345", null)]
    [InlineData("\"ABCD\"", null)]
    [InlineData("null", null)]
    public void A_payee_mcc_is_four_digits_from_a_number_or_a_string(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, PluggyOpenFinanceSource.MerchantCategoryCode(document.RootElement));
    }

    [Fact]
    public void A_card_line_without_any_of_the_fields_has_no_details()
    {
        Assert.Null(PluggyOpenFinanceSource.NormalizeCardDetails(null));
        Assert.Null(PluggyOpenFinanceSource.NormalizeCardDetails(new PluggyCardMetadata("1234", "bill", DateTimeOffset.UnixEpoch)));
    }
}
