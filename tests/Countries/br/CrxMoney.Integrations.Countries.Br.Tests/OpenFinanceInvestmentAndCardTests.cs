using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br.OpenFinance;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrxMoney.Integrations.Countries.Br.Tests;

public sealed class OpenFinanceInvestmentAndCardTests
{
    private static readonly OpenFinanceAccount Checking = new(
        "a-checking", OpenFinanceAccountKind.Deposit, BrazilianBankAccount.Create("077", "0001", "12345678-9"), "077", null, "BRL", 100m);

    private static readonly OpenFinanceAccount Card = new("a-card", OpenFinanceAccountKind.CreditCard, null, "077", "1234", "BRL", null);

    private static readonly DateTimeOffset Day = new(2026, 9, 10, 15, 0, 5, TimeSpan.Zero);

    private static readonly JsonElement Raw = JsonDocument.Parse("""{"id":"raw"}""").RootElement;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OpenFinanceTransaction Line(string id, OpenFinanceDirection direction, decimal amount, string type = "PIX", string? detail = null,
        bool pending = false, DateTimeOffset? time = null, string? bill = null) =>
        new(id, time ?? Day, direction, amount, type, detail, null, null, null, pending, bill, Raw);

    private static OpenFinanceInvestment Position(string key, string sourceId = "pos", string subtype = "CDB") =>
        new(sourceId, "077", "number", key, "FIXED_INCOME", subtype, "CDB Exemplo", "BRL", 1000m, Day) { SourcePayload = Raw };

    private static OpenFinanceInvestmentMovement Movement(string id, OpenFinanceInvestmentOperation operation, decimal gross,
        OpenFinanceInvestmentExpenses? expenses = null, string? description = null, DateTimeOffset? time = null) =>
        new(id, time ?? Day, operation, gross, expenses ?? OpenFinanceInvestmentExpenses.None, description, Raw);

    private static Task<IntegrationSyncResult> Sync(OpenFinanceSnapshot snapshot, DateTimeOffset? windowStart = null) =>
        new TestPlugin(new SnapshotSource(snapshot)).SyncAsync(new IntegrationSyncRequest(42, "Any", windowStart, null), Ct);

    private static JsonNode Diagnostics(IntegrationSyncResult result) => JsonNode.Parse(result.ProviderDiagnosticsJson!)!;

    private static JsonNode Data(IntegrationImportEvent e) => JsonNode.Parse(e.ProviderData!)!;

    [Theory]
    [InlineData(true, 160, 160)]  // the issuer's figure is the used limit: 160 = 100 posted + 60 pending
    [InlineData(false, 100, 160)] // the issuer's figure is the settled balance already
    public async Task A_card_balance_is_split_into_settled_and_pending(bool includesPending, int owed, int expectedWithPending)
    {
        var card = Card with { Card = new OpenFinanceCardBalance(owed, includesPending, CreditLimit: 1000m), BalanceAt = Day };
        var lines = new[]
        {
            Line("p", OpenFinanceDirection.Debit, 100m, "CARD"),
            Line("q", OpenFinanceDirection.Debit, 70m, "CARD", pending: true),
            Line("r", OpenFinanceDirection.Credit, 10m, "CARD", pending: true), // a pending refund
        };

        var result = await Sync(new OpenFinanceSnapshot([new OpenFinanceAccountTransactions(card, lines)], null));

        var row = Diagnostics(result)["accounts"]![0]!;
        Assert.Equal(100m, row["balance"]!.GetValue<decimal>());
        Assert.Equal(60m, row["pendingAmount"]!.GetValue<decimal>());
        Assert.Equal(2, row["pendingLines"]!.GetValue<int>());
        Assert.Equal(expectedWithPending, row["balanceWithPending"]!.GetValue<decimal>());
        Assert.Equal("liability", row["balanceSide"]!.GetValue<string>());
        Assert.Equal(1000m, row["creditLimit"]!.GetValue<decimal>());
        Assert.Null(row["minimumPayment"]);
    }

    [Fact]
    public void Investment_keys_do_not_depend_on_the_aggregator()
    {
        var positionA = Position("000123-4", sourceId: "aggregator-a-position");
        var positionB = Position("0001234", sourceId: "B:pos:9");
        var movementA = Movement("a-move-1", OpenFinanceInvestmentOperation.Application, 500m);
        var movementB = Movement("b-zz", OpenFinanceInvestmentOperation.Application, 500m, description: "described differently");

        var eventsA = OpenFinanceInvestmentMapper.Map(positionA, movementA, 0, "A");
        var eventsB = OpenFinanceInvestmentMapper.Map(positionB, movementB, 0, "B");

        Assert.Equal("br:077:inv:number:0001234", positionA.ExternalAccountId);
        Assert.Equal(eventsA.Select(e => e.IdempotencyKey), eventsB.Select(e => e.IdempotencyKey));
        Assert.NotEqual(eventsA[0].ExternalId, eventsB[0].ExternalId);
    }

    [Fact]
    public void Investment_keys_never_collide_with_account_line_keys_or_with_each_other()
    {
        var position = Position("1");
        var redemption = Movement("m", OpenFinanceInvestmentOperation.Redemption, 1000m, new OpenFinanceInvestmentExpenses(10m, 2m, 1m));
        var sameAsApplication = Movement("n", OpenFinanceInvestmentOperation.Application, 1000m);
        var line = Line("l", OpenFinanceDirection.Debit, 1000m);

        var keys = OpenFinanceInvestmentMapper.Map(position, redemption, 0, "A")
            .Concat(OpenFinanceInvestmentMapper.Map(position, sameAsApplication, 0, "A"))
            .Select(e => e.IdempotencyKey)
            .Append(OpenFinanceTransactionMapper.Map(Checking, line, 0, "A").IdempotencyKey)
            .ToList();

        Assert.Equal(6, keys.Count); // redemption + IR + IOF + fee, application, the account line
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [InlineData(OpenFinanceInvestmentOperation.Application, 1000, 2, "deposit:1002|withdrawal:2", 1002)]
    [InlineData(OpenFinanceInvestmentOperation.Redemption, 1000, 15, "withdrawal:985|withdrawal:15", 985)]
    [InlineData(OpenFinanceInvestmentOperation.Amortization, 1000, 0, "withdrawal:1000", 1000)]
    [InlineData(OpenFinanceInvestmentOperation.TransferIn, 300, 0, "deposit:300", null)]
    [InlineData(OpenFinanceInvestmentOperation.TransferOut, 300, 0, "withdrawal:300", null)]
    public void A_movement_maps_to_its_cash_event_and_its_expenses(OpenFinanceInvestmentOperation operation, int gross, int fees, string expected, int? cash)
    {
        var movement = Movement("m", operation, gross, new OpenFinanceInvestmentExpenses(0m, 0m, fees));

        var events = OpenFinanceInvestmentMapper.Map(Position("1"), movement, 0, "A");

        Assert.Equal(expected, string.Join('|', events.Select(e => $"{e.Type}:{e.Amount:0.##}")));
        Assert.Equal(cash, OpenFinanceInvestmentMapper.CashAmount(movement) is { } amount ? (int)amount : null);
        Assert.All(events, e => Assert.Equal("br:077:inv:number:1", e.ExternalAccountId));
        Assert.All(events, e => Assert.Equal(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.FromHours(-3)), e.OccurredAtUtc));
    }

    [Fact]
    public void Income_paid_out_is_booked_on_the_position_and_paid_to_cash()
    {
        var movement = Movement("d", OpenFinanceInvestmentOperation.Income, 50m, new OpenFinanceInvestmentExpenses(7.5m, 0m, 0m)) with { PaidOut = true };

        var events = OpenFinanceInvestmentMapper.Map(Position("1"), movement, 0, "A");

        Assert.Equal(["deposit:50", "withdrawal:7.5", "withdrawal:42.5"], events.Select(e => $"{e.Type}:{e.Amount:0.##}"));
        Assert.Equal(["d", "d#incomeTax", "d#payout"], events.Select(e => e.ExternalId));
        Assert.Equal(events[2].IdempotencyKey, OpenFinanceInvestmentMapper.CashEventKey(Position("1"), movement, 0));
        Assert.Equal("investment", Data(events[2])["transferHint"]!.GetValue<string>());
        Assert.Null(Data(events[0])["transferHint"]);
    }

    [Theory]
    [InlineData("IOF S/ RESGATE", 0, 0, "iof")]
    [InlineData("IRRF", 0, 0, "incomeTax")]
    [InlineData("TAXA", 0, 0, "tax")]
    [InlineData(null, 3, 0, "incomeTax")]
    [InlineData("IR", 0, 2, "iof")]
    public void Tax_lines_say_which_tax_they_are(string? description, int incomeTax, int iof, string expected)
    {
        var movement = Movement("t", OpenFinanceInvestmentOperation.Tax, 3m, new OpenFinanceInvestmentExpenses(incomeTax, iof, 0m), description);

        var events = OpenFinanceInvestmentMapper.Map(Position("1"), movement, 0, "A");

        Assert.Equal(expected, OpenFinanceInvestmentMapper.TaxKind(movement));
        Assert.Single(events); // a tax line's own expenses are not booked twice
        Assert.Equal(expected, Data(events[0])["taxKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_sweep_with_candidates_on_two_positions_stays_unpaired()
    {
        var sweep = Line("s", OpenFinanceDirection.Debit, 1000m, "OUTROS", "APLICACAO CDB");
        var snapshot = new OpenFinanceSnapshot([new OpenFinanceAccountTransactions(Checking, [sweep])], null)
        {
            Investments =
            [
                new OpenFinanceInvestmentMovements(Position("1", "p1"), [Movement("m1", OpenFinanceInvestmentOperation.Application, 1000m)]),
                new OpenFinanceInvestmentMovements(Position("2", "p2"), [Movement("m2", OpenFinanceInvestmentOperation.Application, 1000m)]),
            ],
        };

        var result = await Sync(snapshot);

        var line = Data(result.ImportEvents.Single(e => e.ExternalId == "s"));
        Assert.Null(line["pair"]);
        Assert.Null(line["investment"]!["externalAccountId"]);
        Assert.Equal("CDB", line["investment"]!["product"]!.GetValue<string>());
        var counts = Diagnostics(result)["transferPairs"]!["investment"]!;
        Assert.Equal(1, counts["ambiguous"]!.GetValue<int>());
        Assert.Equal(2, counts["positionSideUnpaired"]!.GetValue<int>());
    }

    [Fact]
    public async Task Identical_sweeps_into_one_position_pair_one_to_one_and_the_nearest_date_wins()
    {
        var position = Position("1");
        var sweeps = new[]
        {
            Line("s1", OpenFinanceDirection.Debit, 1000m, "OUTROS", "APL CDB"),
            Line("s2", OpenFinanceDirection.Debit, 1000m, "OUTROS", "APL CDB", time: Day.AddDays(2)),
        };
        var movements = new[]
        {
            Movement("m-late", OpenFinanceInvestmentOperation.Application, 1000m, time: Day.AddDays(2)),
            Movement("m-early", OpenFinanceInvestmentOperation.Application, 1000m, time: Day.AddDays(-1)),
        };
        var snapshot = new OpenFinanceSnapshot([new OpenFinanceAccountTransactions(Checking, sweeps)], null)
        {
            Investments = [new OpenFinanceInvestmentMovements(position, movements)],
        };

        var result = await Sync(snapshot);
        var events = result.ImportEvents.ToDictionary(e => e.ExternalId);

        Assert.Equal(events["m-early"].IdempotencyKey, Data(events["s1"])["pair"]!["idempotencyKey"]!.GetValue<string>());
        Assert.Equal(events["m-late"].IdempotencyKey, Data(events["s2"])["pair"]!["idempotencyKey"]!.GetValue<string>());
        Assert.Equal(events["s2"].IdempotencyKey, Data(events["m-late"])["pair"]!["idempotencyKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bill_payment_pairs_with_the_card_payment_booked_up_to_three_days_later()
    {
        var debit = Line("c", OpenFinanceDirection.Debit, 300m, "BOLETO", "PAGAMENTO FATURA");
        var tooLate = Line("k1", OpenFinanceDirection.Credit, 300m, "CARD", "PAGAMENTO RECEBIDO", time: Day.AddDays(4), bill: "b-1");
        var inTime = Line("k2", OpenFinanceDirection.Credit, 300m, "CARD", "PAGAMENTO RECEBIDO", time: Day.AddDays(3), bill: "b-2");
        var snapshot = new OpenFinanceSnapshot(
            [new OpenFinanceAccountTransactions(Checking, [debit]), new OpenFinanceAccountTransactions(Card, [tooLate, inTime])], null);

        var result = await Sync(snapshot);

        var paid = Data(result.ImportEvents.Single(e => e.ExternalId == "c"));
        Assert.Equal("b-2", paid["card"]!["billId"]!.GetValue<string>());
        Assert.Equal("br:077:card:1234", paid["card"]!["externalAccountId"]!.GetValue<string>());
        Assert.Equal(1, Diagnostics(result)["transferPairs"]!["cardBillPayment"]!["cardSideUnpaired"]!.GetValue<int>());
    }

    [Fact]
    public async Task Investment_movements_follow_the_sync_window()
    {
        var position = Position("1");
        var snapshot = new OpenFinanceSnapshot([new OpenFinanceAccountTransactions(Checking, [])], null)
        {
            Investments =
            [
                new OpenFinanceInvestmentMovements(position,
                [
                    Movement("old", OpenFinanceInvestmentOperation.Application, 10m, time: Day.AddDays(-30)),
                    Movement("new", OpenFinanceInvestmentOperation.Application, 20m, time: Day),
                ]),
            ],
        };

        var windowed = await Sync(snapshot, Day.AddDays(-1));
        var full = await Sync(snapshot);

        Assert.Equal(["new"], windowed.ImportEvents.Select(e => e.ExternalId));
        Assert.Equal(full.ImportEvents.Single(e => e.ExternalId == "new").IdempotencyKey, windowed.ImportEvents.Single().IdempotencyKey);
        var row = Diagnostics(windowed)["accounts"]!.AsArray().Single(a => a!["kind"]!.GetValue<string>() == "investment")!;
        Assert.Equal((2, 1), (row["lines"]!.GetValue<int>(), row["sent"]!.GetValue<int>()));
        Assert.Equal(1000m, row["balance"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Two_positions_with_one_identity_fail_instead_of_sharing_keys()
    {
        var snapshot = new OpenFinanceSnapshot([], null)
        {
            Investments = [new OpenFinanceInvestmentMovements(Position("1", "p1"), []), new OpenFinanceInvestmentMovements(Position("1", "p2"), [])],
        };

        var result = await Sync(snapshot);

        Assert.Equal(IntegrationErrorKind.Upstream, result.ErrorKind);
    }

    [Theory]
    [InlineData("77", "number", "000123-4", "br:077:inv:number:0001234")]
    [InlineData("077", "ISIN", "brexmpacnor0", "br:077:inv:isin:BREXMPACNOR0")]
    [InlineData("1", "code", "12.345.678/0001-00", "br:001:inv:code:12345678000100")]
    public void Investment_account_ids_are_normalised(string compe, string basis, string key, string expected)
    {
        Assert.Equal(expected, BrazilianBankAccount.InvestmentExternalAccountId(compe, basis, key));
    }

    [Fact]
    public void The_manifest_declares_valid_json_schemas_for_investments()
    {
        var schemas = OpenFinanceImportSchemas.Manifest.Schemas;

        Assert.Equal(4, schemas.Count);
        Assert.All(schemas, s => Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(s.JsonSchema!).RootElement.ValueKind));
        var investment = JsonNode.Parse(schemas.Single(s => s.SchemaId == OpenFinanceImportSchemas.InvestmentWithdrawalSchemaId).JsonSchema!)!;
        Assert.Equal("investment.withdrawal", investment["properties"]!["kind"]!["const"]!.GetValue<string>());
        var transaction = JsonNode.Parse(schemas.Single(s => s.SchemaId == OpenFinanceImportSchemas.DepositSchemaId).JsonSchema!)!;
        Assert.NotNull(transaction["properties"]!["pair"]!["properties"]!["idempotencyKey"]);
        Assert.NotNull(transaction["properties"]!["card"]!["properties"]!["issuerCompe"]);
    }

    private sealed class TestPlugin(IOpenFinanceSource source) : OpenFinanceIntegrationPlugin(source, TimeProvider.System, NullLogger.Instance)
    {
        public override string ProviderId => "Test";

        public override string DisplayName => "Test";
    }

    private sealed class SnapshotSource(OpenFinanceSnapshot snapshot) : IOpenFinanceSource
    {
        public string SourceName => "A";

        public Task<OpenFinanceConnectionCheck> CheckConnectionAsync(IntegrationSyncRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OpenFinanceConnectionCheck("connected", null));

        public Task<OpenFinanceSnapshot> ReadAsync(IntegrationSyncRequest request, DateOnly? readFrom, CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }
}
