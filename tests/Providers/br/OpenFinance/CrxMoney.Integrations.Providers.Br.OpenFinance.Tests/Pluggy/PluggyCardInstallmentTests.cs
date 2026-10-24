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
/// <c>card.installment</c> and the other <c>card</c> keys, each installment is dated at its bill, and the idempotency
/// key does not move. Every line of <c>transactions-card-installments.json</c> is synthetic.
/// </summary>
public sealed class PluggyCardInstallmentTests
{
    private const string CardAccountKey = "br:077:card:1234";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonArray InstallmentLines() =>
        JsonNode.Parse(Fixtures.Text("transactions-card-installments.json"))!["results"]!.AsArray();

    private static async Task<IReadOnlyDictionary<string, IntegrationImportEvent>> CardEventsAsync(JsonArray lines)
    {
        var server = FakePluggyServer.WithFixtures();
        server.CardLines = lines;
        using var host = new PluggyTestHost(server);
        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);
        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        return result.ImportEvents
            .Where(e => e.ExternalAccountId == CardAccountKey)
            .ToDictionary(e => e.ExternalId[^3..], StringComparer.Ordinal);
    }

    private static JsonNode Card(IntegrationImportEvent e) => JsonNode.Parse(e.ProviderData!)!["card"]!;

    private static string Date(IntegrationImportEvent e) =>
        BrazilBankingCalendar.DateOf(e.OccurredAtUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Text(JsonNode? node) => node?.GetValue<string>();

    [Fact]
    public async Task An_open_finance_installment_carries_its_installment_and_is_dated_at_its_bill()
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
        Assert.Equal(1, installment["number"]!.GetValue<int>());
        Assert.Equal(6, installment["count"]!.GetValue<int>());
        Assert.Equal("2025-10-25", Text(installment["purchaseDate"]));
        Assert.Null(installment["totalAmount"]); // Open Finance reports no total
        Assert.Equal("metadata", Text(installment["source"]));

        // Dated at the bill post date; transactionTime and transactionDate still say when the bank dated the line.
        Assert.Equal("2025-10-26", Date(e));
        var data = JsonNode.Parse(e.ProviderData!)!;
        Assert.Equal("2025-10-25", Text(data["transactionDate"]));
        Assert.Equal("LOJA EXEMPLO*DECOR01/06", Text(data["description"]));
        Assert.NotNull(data["sourcePayload"]);
        Assert.Equal(32.69m, e.Amount); // the installment, never the purchase
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
        Assert.Equal(eventDate, Date(e));
    }

    [Fact]
    public async Task An_installment_on_no_bill_yet_or_posted_on_its_purchase_date_keeps_its_date()
    {
        var events = await CardEventsAsync(InstallmentLines());

        // billPostDate 0001-01-01: Open Finance's placeholder for a line on no bill yet; not reported, not used.
        var noBill = events["f08"];
        Assert.Null(Card(noBill)["billPostDate"]);
        Assert.Equal(3, Card(noBill)["installment"]!["number"]!.GetValue<int>());
        Assert.Equal("2026-08-12", Text(Card(noBill)["installment"]!["purchaseDate"]));
        Assert.Equal("2026-10-12", Date(noBill));

        // billPostDate equal to the purchase date (later Itaú bills): the line keeps its date.
        var onPurchaseDate = events["f09"];
        Assert.Equal("2025-12-24", Text(Card(onPurchaseDate)["billPostDate"]));
        Assert.Equal("2025-12-24", Text(Card(onPurchaseDate)["installment"]!["purchaseDate"]));
        Assert.Equal("2025-12-24", Date(onPurchaseDate));
    }

    [Fact]
    public async Task Redating_an_installment_does_not_change_its_idempotency_key()
    {
        var dated = InstallmentLines();
        var undated = InstallmentLines();
        foreach (var name in new[] { "billPostDate", "paymentType", "installmentNumber", "totalInstallments", "purchaseDate" })
            undated[0]!["creditCardMetadata"]!.AsObject().Remove(name);
        undated[0]!["descriptionRaw"] = "LOJA EXEMPLO*DECOR";

        var withBill = (await CardEventsAsync(dated))["f01"];
        var withoutBill = (await CardEventsAsync(undated))["f01"];

        Assert.Equal("2025-10-26", Date(withBill));
        Assert.Equal("2025-10-25", Date(withoutBill));
        Assert.Null(Card(withoutBill)["installment"]);
        // Same line, new date: the key is the one the transaction instant gives, as before #1417.
        Assert.Equal(withoutBill.IdempotencyKey, withBill.IdempotencyKey);
        Assert.Equal(OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2025-10-25T03:00:00Z|D|32.69", 0), withBill.IdempotencyKey);
    }

    [Theory]
    [InlineData("INSTALLMENT", 1, 6, null, true)]
    [InlineData("A_PRAZO", 2, 6, null, true)]          // Open Finance's own name
    [InlineData(null, 4, 10, null, true)]              // a fee without payment type
    [InlineData("SINGLE", 1, 6, "LOJA 01/06", false)]  // the source's SINGLE wins over its numbers and the description
    [InlineData("INSTALLMENT", 1, 1, "LOJA 01/06", false)] // one installment is a single purchase; the metadata decides
    [InlineData("INSTALLMENT", 7, 6, null, false)]     // number past the count
    [InlineData(null, null, null, "LOJA EXEMPLO 01/02", true)]
    [InlineData(null, null, null, "LOJA EXEMPLO*DECOR06/06", true)]
    [InlineData(null, null, null, "LOJA EXEMPLO 123/12", false)] // a digit before the suffix: not an installment
    [InlineData(null, null, null, "DROGARIA EXEMPLO 15/10", false)] // number past the count
    [InlineData(null, null, null, "LOJA EXEMPLO 00/02", false)]
    [InlineData(null, null, null, "LOJA EXEMPLO", false)]
    public void The_shared_mapper_decides_what_an_installment_is(string? paymentType, int? number, int? count, string? description, bool expected)
    {
        var card = new OpenFinanceAccount("card", OpenFinanceAccountKind.CreditCard, null, "077", "1234", "BRL", null);
        var line = new OpenFinanceTransaction("t", new DateTimeOffset(2026, 9, 10, 15, 0, 0, TimeSpan.Zero), OpenFinanceDirection.Debit, 10m,
            "CARD", null, description, null, null, false, null, JsonDocument.Parse("{}").RootElement)
        {
            CardDetails = new OpenFinanceCardLineDetails { PaymentType = paymentType, InstallmentNumber = number, InstallmentCount = count },
        };

        Assert.Equal(expected, OpenFinanceTransactionMapper.Installment(card, line) is not null);

        // A deposit account's line is never an installment, whatever it says.
        var checking = new OpenFinanceAccount("k", OpenFinanceAccountKind.Deposit, BrazilianBankAccount.Create("077", "0001", "12345678-9"), "077", null, "BRL", 0m);
        Assert.Null(OpenFinanceTransactionMapper.Installment(checking, line));
    }

    [Theory]
    [InlineData("2025-10-25", "2025-10-25")]
    [InlineData("2025-10-25T00:00:00.000Z", "2025-10-25")]   // a date written as a date-time
    [InlineData("2025-10-25T03:00:00.000Z", "2025-10-25")]   // midnight in São Paulo
    [InlineData("2025-10-25T01:30:00.000Z", "2025-10-24")]   // an instant: Brazil's calendar
    [InlineData("2025-10-25T23:30:00-03:00", "2025-10-25")]
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
