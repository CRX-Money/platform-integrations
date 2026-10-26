using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy.Api;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy.Support;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy;

/// <summary>
/// Lines made in a currency other than the account's (#1440): Pluggy's <c>amount</c> is in the line's
/// <c>currencyCode</c> and <c>amountInAccountCurrency</c> is what the account was charged or credited. The event carries
/// the account's amount and asset, the original goes in <c>foreign</c>, and the key keeps the original amount, as
/// before, so no key changes. Every line is synthetic.
/// </summary>
public sealed class PluggyForeignCurrencyTests
{
    private const string CardAccountKey = "br:077:card:1234";
    private const string BankAccountKey = "br:077:0001:123456789";
    private const string BillId = "00000000-0000-4000-8000-0000000000e1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OpenFinanceAccount BrlCard = new("card", OpenFinanceAccountKind.CreditCard, null, "077", "1234", "BRL", null);

    /// <summary>A Pluggy card line; <paramref name="extra"/> adds <c>currencyCode</c>, <c>amountInAccountCurrency</c> and the like.</summary>
    private static JsonObject CardLine(string suffix, string time, string amount, string extra, string status = "POSTED", string type = "DEBIT",
        string paymentType = "\"paymentType\": \"SINGLE\"") =>
        JsonNode.Parse($$"""
            {
              "id": "00000000-0000-4000-8000-000000000{{suffix}}",
              "accountId": "00000000-0000-4000-8000-0000000000b2",
              "date": "{{time[..10]}}T03:00:00.000Z",
              "description": "Loja Exterior Teste",
              "descriptionRaw": "LOJA EXTERIOR TESTE",
              "amount": {{amount}},
              {{extra}}
              "type": "{{type}}",
              "status": "{{status}}",
              "operationType": null,
              "paymentData": null,
              "merchant": null,
              "creditCardMetadata": {
                "cardNumber": "1234",
                "billId": "{{BillId}}",
                {{paymentType}},
                "transactionDateTime": "{{time}}"
              }
            }
            """)!.AsObject();

    /// <summary>
    /// f01: USD 10.00 charged BRL 55.20. f02: GBP 7.50 without the account's amount. f03: a BRL line (lower case) that
    /// also carries an <c>amountInAccountCurrency</c>, which is ignored. f04: a pending USD 5.00 charged BRL 27.60.
    /// </summary>
    private static JsonArray Lines() =>
    [
        CardLine("f01", "2026-09-20T14:30:00.000Z", "10.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 55.20,"),
        CardLine("f02", "2026-09-21T09:15:00.000Z", "7.50", "\"currencyCode\": \"GBP\","),
        CardLine("f03", "2026-09-22T18:45:00.000Z", "33.10", "\"currencyCode\": \"brl\", \"amountInAccountCurrency\": 99.99,"),
        CardLine("f04", "2026-10-12T11:00:00.000Z", "5.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 27.60,", status: "PENDING"),
    ];

    private static async Task<IntegrationSyncResult> SyncAsync(JsonArray cardLines, JsonArray? bankLines = null)
    {
        var server = FakePluggyServer.WithFixtures();
        server.CardLines = cardLines;
        server.BankLines = bankLines;
        using var host = new PluggyTestHost(server);
        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);
        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        return result;
    }

    private static IntegrationImportEvent Event(IntegrationSyncResult result, string suffix) =>
        result.ImportEvents.Single(e => e.ExternalId.EndsWith(suffix, StringComparison.Ordinal));

    private static JsonNode Data(IntegrationImportEvent e) => JsonNode.Parse(e.ProviderData!)!;

    private static JsonNode Diagnostics(IntegrationSyncResult result) => JsonNode.Parse(result.ProviderDiagnosticsJson!)!;

    [Fact]
    public async Task A_foreign_card_purchase_is_the_amount_the_card_was_charged_with_the_original_beside_it()
    {
        var e = Event(await SyncAsync(Lines()), "f01");

        Assert.Equal(55.20m, e.Amount);
        Assert.Equal("BRL", e.AssetCode);
        Assert.Equal(CardAccountKey, e.ExternalAccountId);
        var foreign = Data(e)["foreign"]!;
        Assert.Equal(["currencyCode", "amount"], foreign.AsObject().Select(p => p.Key));
        Assert.Equal("USD", foreign["currencyCode"]!.GetValue<string>());
        Assert.Equal(10.00m, foreign["amount"]!.GetValue<decimal>());
        // The raw line is still kept verbatim, original amount and all.
        Assert.Equal(55.20m, Data(e)["sourcePayload"]!["amountInAccountCurrency"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task A_foreign_lines_key_keeps_the_original_amount()
    {
        var result = await SyncAsync(Lines());

        // What preview.10 hashed: the converted amount can be filled in or revised later without moving the key.
        Assert.Equal(
            OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2026-09-20T14:30:00Z|D|10.00", 0),
            Event(result, "f01").IdempotencyKey);
        Assert.NotEqual(
            OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2026-09-20T14:30:00Z|D|55.20", 0),
            Event(result, "f01").IdempotencyKey);
    }

    [Fact]
    public async Task A_converted_amount_filled_in_or_revised_later_arrives_under_the_same_key()
    {
        var first = Event(await SyncAsync([CardLine("f01", "2026-09-20T14:30:00.000Z", "10.00", "\"currencyCode\": \"USD\",")]), "f01");
        var filled = Event(await SyncAsync([CardLine("f01", "2026-09-20T14:30:00.000Z", "10.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 55.20,")]), "f01");
        var revised = Event(await SyncAsync([CardLine("f01", "2026-09-20T14:30:00.000Z", "10.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 54.90,")]), "f01");

        Assert.Equal([10.00m, 55.20m, 54.90m], new[] { first.Amount, filled.Amount, revised.Amount });
        Assert.Equal(first.IdempotencyKey, filled.IdempotencyKey);
        Assert.Equal(first.IdempotencyKey, revised.IdempotencyKey);
    }

    [Fact]
    public async Task A_card_refund_abroad_is_a_credit_in_the_accounts_currency()
    {
        var result = await SyncAsync([CardLine("f09", "2026-09-26T16:20:00.000Z", "-4.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": -22.08,", type: "CREDIT")]);

        var e = Event(result, "f09");
        Assert.Equal(OpenFinanceTransactionMapper.DepositType, e.Type);
        Assert.Equal(22.08m, e.Amount);
        Assert.Equal("BRL", e.AssetCode);
        Assert.Equal(4.00m, Data(e)["foreign"]!["amount"]!.GetValue<decimal>());
        Assert.Equal(
            OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2026-09-26T16:20:00Z|C|4.00", 0),
            e.IdempotencyKey);
    }

    [Fact]
    public async Task A_zero_amount_in_the_accounts_currency_counts_as_missing()
    {
        var result = await SyncAsync([CardLine("f10", "2026-09-27T08:00:00.000Z", "6.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 0,")]);

        var e = Event(result, "f10");
        Assert.Equal(6.00m, e.Amount); // never a 0 event
        Assert.True(Data(e)["foreign"]!["accountAmountMissing"]!.GetValue<bool>());
        Assert.Equal(1, Diagnostics(result)["foreignLinesWithoutAccountAmount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Two_identical_foreign_purchases_in_one_second_are_numbered_by_their_original_amount()
    {
        var result = await SyncAsync(
        [
            CardLine("f11", "2026-09-28T13:00:00.000Z", "3.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 16.50,"),
            CardLine("f12", "2026-09-28T13:00:00.000Z", "3.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 16.62,"),
        ]);

        const string identity = $"{CardAccountKey}|2026-09-28T13:00:00Z|D|3.00";
        Assert.Equal(16.50m, Event(result, "f11").Amount);
        Assert.Equal(16.62m, Event(result, "f12").Amount);
        Assert.Equal(OpenFinanceTransactionMapper.IdempotencyKey(identity, 0), Event(result, "f11").IdempotencyKey);
        Assert.Equal(OpenFinanceTransactionMapper.IdempotencyKey(identity, 1), Event(result, "f12").IdempotencyKey);
    }

    [Fact]
    public async Task Amounts_with_more_than_two_decimals_are_kept_and_the_identity_rounds_as_before()
    {
        var result = await SyncAsync([CardLine("f13", "2026-09-29T10:30:00.000Z", "9.999", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 55.1234,")]);

        var e = Event(result, "f13");
        Assert.Equal(55.1234m, e.Amount);
        Assert.Equal(9.999m, Data(e)["foreign"]!["amount"]!.GetValue<decimal>());
        Assert.Equal(OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2026-09-29T10:30:00Z|D|10.00", 0), e.IdempotencyKey);
    }

    [Fact]
    public async Task A_foreign_installment_leaves_out_the_purchase_total()
    {
        var result = await SyncAsync([CardLine("f14", "2026-09-30T15:00:00.000Z", "10.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 55.20,",
            paymentType: "\"paymentType\": \"INSTALLMENT\", \"installmentNumber\": 1, \"totalInstallments\": 3, \"totalAmount\": 30.00")]);

        var installment = Data(Event(result, "f14"))["card"]!["installment"]!;
        Assert.Equal(3, installment["count"]!.GetValue<int>());
        Assert.Null(installment["totalAmount"]); // its currency is not documented
    }

    [Fact]
    public async Task Pending_foreign_lines_without_the_accounts_amount_are_counted()
    {
        var result = await SyncAsync(
        [
            CardLine("f15", "2026-10-12T11:00:00.000Z", "2.00", "\"currencyCode\": \"GBP\",", status: "PENDING"),
            CardLine("f16", "2026-10-12T12:00:00.000Z", "5.00", "\"currencyCode\": \"USD\", \"amountInAccountCurrency\": 27.60,", status: "PENDING"),
        ]);

        var diagnostics = Diagnostics(result);
        Assert.Equal(1, diagnostics["pendingForeignLinesWithoutAccountAmount"]!.GetValue<int>());
        Assert.Equal(0, diagnostics["foreignLines"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_line_in_the_accounts_currency_is_unchanged_and_has_no_foreign_block()
    {
        var result = await SyncAsync(Lines());

        var brl = Event(result, "f03");
        Assert.Equal(33.10m, brl.Amount);
        Assert.Null(Data(brl)["foreign"]);
        Assert.Equal(
            OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2026-09-22T18:45:00Z|D|33.10", 0),
            brl.IdempotencyKey);
        // The fixture's BRL lines (no amountInAccountCurrency) carry none either.
        Assert.All(result.ImportEvents.Where(e => e.ExternalAccountId == BankAccountKey), e => Assert.Null(Data(e)["foreign"]));
    }

    [Fact]
    public async Task A_foreign_line_without_the_accounts_amount_keeps_its_amount_and_is_counted()
    {
        var result = await SyncAsync(Lines());

        var e = Event(result, "f02");
        Assert.Equal(7.50m, e.Amount); // no rate is guessed
        var foreign = Data(e)["foreign"]!;
        Assert.Equal("GBP", foreign["currencyCode"]!.GetValue<string>());
        Assert.Equal(7.50m, foreign["amount"]!.GetValue<decimal>());
        Assert.True(foreign["accountAmountMissing"]!.GetValue<bool>());
        Assert.Equal(
            OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2026-09-21T09:15:00Z|D|7.50", 0),
            e.IdempotencyKey);

        // Sent lines only: the pending f04 is not counted.
        var diagnostics = Diagnostics(result);
        Assert.Equal(2, diagnostics["foreignLines"]!.GetValue<int>());
        Assert.Equal(1, diagnostics["foreignLinesWithoutAccountAmount"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_sync_without_foreign_lines_reports_zero()
    {
        var result = await SyncAsync(cardLines: [CardLine("f05", "2026-09-23T10:00:00.000Z", "12.00", "\"currencyCode\": \"BRL\",")]);

        Assert.Equal(0, Diagnostics(result)["foreignLines"]!.GetValue<int>());
        Assert.Equal(0, Diagnostics(result)["foreignLinesWithoutAccountAmount"]!.GetValue<int>());
        Assert.Equal(0, Diagnostics(result)["pendingForeignLinesWithoutAccountAmount"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_pending_foreign_purchase_counts_in_the_card_balance_by_the_accounts_amount()
    {
        var result = await SyncAsync(Lines());

        var card = Diagnostics(result)["accounts"]!.AsArray().Single(a => a!["externalAccountId"]!.GetValue<string>() == CardAccountKey)!;
        Assert.Equal(27.60m, card["pendingAmount"]!.GetValue<decimal>());
        Assert.Equal(1, card["pendingLines"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_foreign_line_on_a_deposit_account_follows_the_same_rule()
    {
        var bankLine = JsonNode.Parse("""
            {
              "id": "00000000-0000-4000-8000-000000000c90",
              "accountId": "00000000-0000-4000-8000-0000000000b1",
              "date": "2026-09-25T12:00:00.000Z",
              "description": "Transferencia exterior teste",
              "descriptionRaw": "TRANSFERENCIA EXTERIOR TESTE",
              "amount": 200,
              "currencyCode": "EUR",
              "amountInAccountCurrency": 1234.56,
              "type": "CREDIT",
              "status": "POSTED",
              "operationType": "TRANSFERENCIA",
              "paymentData": null,
              "merchant": null,
              "creditCardMetadata": null
            }
            """)!;

        var result = await SyncAsync(Lines(), bankLines: [bankLine]);

        var e = Event(result, "c90");
        Assert.Equal(BankAccountKey, e.ExternalAccountId);
        Assert.Equal(1234.56m, e.Amount);
        Assert.Equal("BRL", e.AssetCode);
        Assert.Equal("EUR", Data(e)["foreign"]!["currencyCode"]!.GetValue<string>());
        Assert.Equal(200m, Data(e)["foreign"]!["amount"]!.GetValue<decimal>());
        Assert.Equal(
            OpenFinanceTransactionMapper.IdempotencyKey($"{BankAccountKey}|2026-09-25T12:00:00Z|C|200.00", 0),
            e.IdempotencyKey);
        Assert.Equal(3, Diagnostics(result)["foreignLines"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null, null, 10.00, null, false)]          // no currency: the account's
    [InlineData(null, 55.20, 10.00, null, false)]         // amountInAccountCurrency without currencyCode: unchanged
    [InlineData("BRL", 99.99, 10.00, null, false)]        // the account's currency: amountInAccountCurrency ignored
    [InlineData(" brl ", null, 10.00, null, false)]       // compared without case or spaces
    [InlineData("R$", 55.20, 10.00, null, false)]         // not an ISO code: the account's currency
    [InlineData("986", 55.20, 10.00, null, false)]        // a numeric ISO code is not read
    [InlineData("USDT", 55.20, 10.00, null, false)]       // four letters: not an ISO code
    [InlineData("usd", -55.20, 55.20, "USD", false)]      // negative amounts are made positive, the code upper case
    [InlineData("USD", null, 10.00, "USD", true)]         // missing: the original amount, flagged
    [InlineData("USD", 0.0, 10.00, "USD", true)]          // zero counts as missing: never a 0 event
    public void The_amount_in_the_accounts_currency_is_chosen_by_the_lines_currency(string? currency, double? inAccount, double expected, string? foreignCurrency, bool missing)
    {
        var transaction = new PluggyTransaction("t", null, null, null, -10.00m, "DEBIT", "POSTED", null, null, null, null, null,
            CurrencyCode: currency, AmountInAccountCurrency: inAccount is { } value ? (decimal)value : null);

        var (amount, foreign) = PluggyOpenFinanceSource.AccountCurrencyAmount(BrlCard, transaction, 10.00m);

        Assert.Equal((decimal)expected, amount);
        Assert.Equal(foreignCurrency, foreign?.CurrencyCode);
        if (foreign is not null)
        {
            Assert.Equal(10.00m, foreign.Amount);
            Assert.Equal(missing, foreign.AccountAmountMissing);
        }
    }

    [Fact]
    public void The_transaction_schemas_declare_the_foreign_block()
    {
        foreach (var schemaId in new[] { OpenFinanceImportSchemas.DepositSchemaId, OpenFinanceImportSchemas.WithdrawalSchemaId })
        {
            var schema = JsonNode.Parse(OpenFinanceImportSchemas.Manifest.Schemas.Single(s => s.SchemaId == schemaId).JsonSchema!)!;
            var foreign = schema["properties"]!["foreign"]!;
            Assert.Equal(["currencyCode", "amount"], foreign["required"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.NotNull(foreign["properties"]!["accountAmountMissing"]);
        }
    }

    [Fact]
    public void The_shared_mapper_writes_foreign_from_the_model_for_any_aggregator()
    {
        var transaction = new OpenFinanceTransaction("t", new DateTimeOffset(2026, 9, 20, 14, 30, 0, TimeSpan.Zero), OpenFinanceDirection.Debit, 55.20m,
            "CARD", null, "LOJA EXTERIOR TESTE", null, null, false, null, JsonDocument.Parse("{}").RootElement)
        {
            Foreign = new OpenFinanceForeignAmount("USD", 10.00m),
        };

        var e = OpenFinanceTransactionMapper.Map(BrlCard, transaction, 0, "Test");

        Assert.Equal(55.20m, e.Amount);
        Assert.Equal("USD", Data(e)["foreign"]!["currencyCode"]!.GetValue<string>());
        Assert.Null(Data(e)["foreign"]!["accountAmountMissing"]);
        Assert.Equal(OpenFinanceTransactionMapper.IdempotencyKey($"{CardAccountKey}|2026-09-20T14:30:00Z|D|10.00", 0), e.IdempotencyKey);
    }
}
