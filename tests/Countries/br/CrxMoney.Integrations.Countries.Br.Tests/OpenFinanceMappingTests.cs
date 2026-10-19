using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br.OpenFinance;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrxMoney.Integrations.Countries.Br.Tests;

public sealed class OpenFinanceMappingTests
{
    private static readonly OpenFinanceAccount Checking = new(
        "aggregator-a-account", OpenFinanceAccountKind.Deposit, BrazilianBankAccount.Create("077", "0001", "12345678-9"), "077", null, "BRL", 639.36m);

    private static readonly OpenFinanceAccount Card = new(
        "aggregator-a-card", OpenFinanceAccountKind.CreditCard, null, "077", "1234", "BRL", null);

    private static readonly DateTimeOffset Noon = new(2026, 9, 10, 15, 0, 5, TimeSpan.Zero);

    private static OpenFinanceTransaction Line(
        string id,
        OpenFinanceDirection direction,
        decimal amount,
        DateTimeOffset? time = null,
        string type = "PIX",
        string? detail = null,
        string? description = null,
        OpenFinanceParty? counterparty = null,
        bool pending = false) =>
        new(id, time ?? Noon, direction, amount, type, detail, description, counterparty, null, pending, null, JsonDocument.Parse($$"""{"id":"{{id}}"}""").RootElement);

    private static OpenFinanceParty Party(int n) => new("Empresa Exemplo Ltda", BrazilianTaxId.TryParse(SyntheticTaxIds.Cnpj(n)), "00000001", null, null);

    [Fact]
    public void A_credit_becomes_a_brl_deposit_on_the_bank_account_dated_in_brazil()
    {
        var importEvent = OpenFinanceTransactionMapper.Map(Checking, Line("a-1", OpenFinanceDirection.Credit, 1500m, counterparty: Party(1)), 0, "AggregatorA");

        Assert.Equal("a-1", importEvent.ExternalId);
        Assert.StartsWith("brof:", importEvent.IdempotencyKey, StringComparison.Ordinal);
        Assert.Equal("deposit", importEvent.Type);
        Assert.Equal("br:077:0001:123456789", importEvent.ExternalAccountId);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.FromHours(-3)), importEvent.OccurredAtUtc);
        Assert.Equal("BRL", importEvent.AssetCode);
        Assert.Equal(1500m, importEvent.Amount);
        Assert.Equal(OpenFinanceImportSchemas.DepositSchemaId, importEvent.ProviderDataSchemaId);

        var data = JsonNode.Parse(importEvent.ProviderData!)!;
        Assert.Equal("AggregatorA", data["provider"]!.GetValue<string>());
        Assert.Equal("openfinance", data["channel"]!.GetValue<string>());
        Assert.Equal(SyntheticTaxIds.Cnpj(1), data["counterparty"]!["taxId"]!.GetValue<string>());
        Assert.Equal("CNPJ", data["counterparty"]!["taxIdKind"]!.GetValue<string>());
        Assert.Equal("a-1", data["sourcePayload"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void The_idempotency_key_does_not_depend_on_the_aggregator()
    {
        var fromA = Line("aggregator-a-0001", OpenFinanceDirection.Debit, 250.75m, counterparty: Party(2));
        var fromB = Line("aggregator-b-zz-99", OpenFinanceDirection.Debit, 250.75m, counterparty: Party(2)) with { Description = "described differently" };
        var accountSeenByB = Checking with { SourceAccountId = "aggregator-b-account" };

        var eventA = OpenFinanceTransactionMapper.Map(Checking, fromA, 0, "AggregatorA");
        var eventB = OpenFinanceTransactionMapper.Map(accountSeenByB, fromB, 0, "AggregatorB");

        Assert.Equal(eventA.IdempotencyKey, eventB.IdempotencyKey);
        Assert.Equal(eventA.ExternalAccountId, eventB.ExternalAccountId);
        Assert.NotEqual(eventA.ExternalId, eventB.ExternalId);
    }

    [Fact]
    public void Any_bank_side_difference_changes_the_key()
    {
        var baseline = OpenFinanceTransactionMapper.LineIdentity(Checking, Line("x", OpenFinanceDirection.Debit, 10m, counterparty: Party(3)));

        Assert.NotEqual(baseline, OpenFinanceTransactionMapper.LineIdentity(Checking, Line("x", OpenFinanceDirection.Credit, 10m, counterparty: Party(3))));
        Assert.NotEqual(baseline, OpenFinanceTransactionMapper.LineIdentity(Checking, Line("x", OpenFinanceDirection.Debit, 10.01m, counterparty: Party(3))));
        Assert.NotEqual(baseline, OpenFinanceTransactionMapper.LineIdentity(Checking, Line("x", OpenFinanceDirection.Debit, 10m, Noon.AddSeconds(1), counterparty: Party(3))));
        Assert.NotEqual(baseline, OpenFinanceTransactionMapper.LineIdentity(Card, Line("x", OpenFinanceDirection.Debit, 10m, counterparty: Party(3))));
    }

    [Fact]
    public void Counterparty_enrichment_arriving_later_does_not_change_the_key()
    {
        var bare = OpenFinanceTransactionMapper.LineIdentity(Checking, Line("x", OpenFinanceDirection.Debit, 10m));
        var enriched = OpenFinanceTransactionMapper.LineIdentity(Checking, Line("x", OpenFinanceDirection.Debit, 10m, counterparty: Party(4)));

        Assert.Equal(bare, enriched);
    }

    [Fact]
    public void The_holder_on_the_other_side_is_not_a_counterparty()
    {
        var holder = BrazilianTaxId.TryParse(SyntheticTaxIds.Cnpj(9));
        var account = Checking with { HolderTaxId = holder };
        var sweep = Line("s", OpenFinanceDirection.Debit, 5000m, type: "OUTROS", detail: "APL AUTOMATICA CDB",
            counterparty: new OpenFinanceParty(null, holder, null, null, null));

        var data = JsonNode.Parse(OpenFinanceTransactionMapper.Map(account, sweep, 0, "A").ProviderData!)!;

        Assert.True(data["counterpartyIsHolder"]!.GetValue<bool>());
        Assert.Null(data["counterparty"]);
        Assert.Equal("investment", data["transferHint"]!.GetValue<string>());
    }

    [Fact]
    public void Identical_lines_get_distinct_keys_whatever_order_they_arrive_in()
    {
        var first = Line("b", OpenFinanceDirection.Debit, 10m, counterparty: Party(5));
        var second = Line("a", OpenFinanceDirection.Debit, 10m, counterparty: Party(5));

        var forward = OpenFinanceTransactionMapper.AssignOrdinals(Checking, [first, second])
            .Select(x => OpenFinanceTransactionMapper.Map(Checking, x.Transaction, x.Ordinal, "A").IdempotencyKey).Order().ToArray();
        var backward = OpenFinanceTransactionMapper.AssignOrdinals(Checking, [second, first])
            .Select(x => OpenFinanceTransactionMapper.Map(Checking, x.Transaction, x.Ordinal, "B").IdempotencyKey).Order().ToArray();

        Assert.Equal(2, forward.Distinct().Count());
        Assert.Equal(forward, backward);
    }

    [Theory]
    [InlineData("OUTROS", "APLICACAO AUTOMATICA CDB", OpenFinanceDirection.Debit, "investment")]
    [InlineData("OUTROS", "Resgate automático CDB", OpenFinanceDirection.Credit, "investment")]
    [InlineData("RESGATE_APLIC_FINANCEIRA", null, OpenFinanceDirection.Credit, "investment")]
    [InlineData("BOLETO", "PAGAMENTO FATURA CARTAO", OpenFinanceDirection.Debit, "card_bill_payment")]
    [InlineData("PIX", "PIX RECEBIDO", OpenFinanceDirection.Credit, null)]
    [InlineData("OUTROS", "DARF", OpenFinanceDirection.Debit, null)]
    [InlineData("OUTROS", "COMPRA APLICATIVO EXEMPLO", OpenFinanceDirection.Debit, null)]
    [InlineData("OUTROS", "RESIDENCIAL EXEMPLO", OpenFinanceDirection.Debit, null)]
    public void Transfer_hints_recognise_sweeps_and_card_bill_payments(string type, string? detail, OpenFinanceDirection direction, string? expected)
    {
        Assert.Equal(expected, OpenFinanceTransactionMapper.TransferHint(Checking, Line("h", direction, 1m, type: type, detail: detail)));
    }

    [Fact]
    public void A_card_payment_received_is_the_other_side_of_the_bill_payment()
    {
        Assert.Equal("card_bill_payment", OpenFinanceTransactionMapper.TransferHint(Card, Line("c", OpenFinanceDirection.Credit, 300m, type: "CARD", description: "PAGAMENTO RECEBIDO")));
        Assert.Null(OpenFinanceTransactionMapper.TransferHint(Card, Line("c", OpenFinanceDirection.Debit, 30m, type: "CARD", description: "LOJA EXEMPLO")));
    }

    [Fact]
    public void Accounts_without_their_bank_identity_cannot_be_keyed()
    {
        var noBank = Checking with { BankAccount = null };
        var noLast4 = Card with { CardLast4 = null };

        Assert.Throws<OpenFinanceIdentityException>(() => noBank.ExternalAccountId);
        Assert.Throws<OpenFinanceIdentityException>(() => noLast4.ExternalAccountId);
    }

    [Fact]
    public async Task Switching_the_aggregator_reproduces_the_same_accounts_and_keys()
    {
        // The same two bank lines, as two different aggregators would deliver them.
        var linesA = new[] { Line("a-1", OpenFinanceDirection.Credit, 1000m, counterparty: Party(6)), Line("a-2", OpenFinanceDirection.Debit, 360.64m, Noon.AddHours(1)) };
        var linesB = new[] { Line("B:77f", OpenFinanceDirection.Debit, 360.64m, Noon.AddHours(1)), Line("B:01c", OpenFinanceDirection.Credit, 1000m, counterparty: Party(6)) };
        var request = new IntegrationSyncRequest(42, "Any", null, null);

        var resultA = await new TestPlugin(new FakeSource("AggregatorA", Checking, linesA)).SyncAsync(request, TestContext.Current.CancellationToken);
        var resultB = await new TestPlugin(new FakeSource("AggregatorB", Checking with { SourceAccountId = "other" }, linesB)).SyncAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationSyncStatus.Succeeded, resultA.Status);
        Assert.Equal(resultA.ImportEvents.Select(e => (e.IdempotencyKey, e.ExternalAccountId)).Order(), resultB.ImportEvents.Select(e => (e.IdempotencyKey, e.ExternalAccountId)).Order());
    }

    [Fact]
    public async Task Pending_lines_are_skipped_and_the_window_trims_what_is_sent()
    {
        var old = Line("old", OpenFinanceDirection.Credit, 1m, new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));
        var recent = Line("recent", OpenFinanceDirection.Credit, 2m);
        var pending = Line("pending", OpenFinanceDirection.Debit, 3m, pending: true);
        var source = new FakeSource("A", Checking with { Balance = 3m, BalanceAt = Noon }, [old, recent, pending]);
        var plugin = new TestPlugin(source);

        var result = await plugin.SyncAsync(new IntegrationSyncRequest(42, "Any", Noon.AddDays(-1), Noon), TestContext.Current.CancellationToken);

        // The source is asked from seven days before the window's first Brazilian date (9 Sep).
        Assert.Equal(new DateOnly(2026, 9, 2), source.ReadFrom);
        Assert.Equal(["recent"], result.ImportEvents.Select(e => e.ExternalId));
        Assert.Equal([new IntegrationCheckpoint(OpenFinanceIntegrationPlugin.CheckpointResource, "2026-09-10")], result.Checkpoints);
        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        Assert.Equal(1, diagnostics["pendingSkipped"]!.GetValue<int>());
        Assert.Equal("2026-09-02", diagnostics["sentFrom"]!.GetValue<string>());
        var account = diagnostics["accounts"]![0]!;
        Assert.Equal("br:077:0001:123456789", account["externalAccountId"]!.GetValue<string>());
        Assert.Equal(3, account["lines"]!.GetValue<int>());
        Assert.Equal(1, account["sent"]!.GetValue<int>());
        Assert.Equal(3m, account["balance"]!.GetValue<decimal>());
        Assert.Equal("2026-09-10T15:00:05+00:00", account["balanceAtUtc"]!.GetValue<string>());
        Assert.Null(account["balanceMatchesNet"]);
    }

    [Fact]
    public async Task A_full_history_request_reads_everything_and_card_balances_are_not_reported()
    {
        var source = new FakeSource("A", Card with { Balance = 89.9m }, [Line("p-1", OpenFinanceDirection.Debit, 89.9m, type: "CARD")]);

        var result = await new TestPlugin(source).SyncAsync(new IntegrationSyncRequest(42, "Any", null, null), TestContext.Current.CancellationToken);

        Assert.True(source.WasRead);
        Assert.Null(source.ReadFrom);
        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        Assert.Null(diagnostics["sentFrom"]);
        Assert.Null(diagnostics["accounts"]![0]!["balance"]);
    }

    [Fact]
    public async Task The_checkpoint_never_moves_back_when_a_window_reads_older_lines_only()
    {
        var source = new FakeSource("A", Checking, [Line("a-1", OpenFinanceDirection.Credit, 1m)]);
        var request = new IntegrationSyncRequest(42, "Any", Noon, Noon,
            PreviousCheckpoints: [new IntegrationCheckpoint(OpenFinanceIntegrationPlugin.CheckpointResource, "2026-09-12")]);

        var result = await new TestPlugin(source).SyncAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal([new IntegrationCheckpoint(OpenFinanceIntegrationPlugin.CheckpointResource, "2026-09-12")], result.Checkpoints);
    }

    [Fact]
    public async Task Source_failures_become_contract_failures()
    {
        var plugin = new TestPlugin(new FakeSource("A", Checking, []) { Failure = new OpenFinanceSourceException(IntegrationErrorKind.Authentication, "consent expired") });

        var result = await plugin.SyncAsync(new IntegrationSyncRequest(42, "Any", null, null), TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationSyncStatus.Failed, result.Status);
        Assert.Equal(IntegrationErrorKind.Authentication, result.ErrorKind);
        Assert.Equal("consent expired", result.ErrorMessage);
    }

    [Fact]
    public async Task Connection_test_reports_the_source_check_and_consent_expiry()
    {
        var plugin = new TestPlugin(new FakeSource("A", Checking, []));

        var result = await plugin.SyncAsync(new IntegrationSyncRequest(0, "Any", null, null), TestContext.Current.CancellationToken);

        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Equal("connected", diagnostics["summary"]!.GetValue<string>());
        Assert.NotNull(diagnostics["credentialExpiresAtUtc"]);
    }

    private sealed class TestPlugin(IOpenFinanceSource source) : OpenFinanceIntegrationPlugin(source, TimeProvider.System, NullLogger.Instance)
    {
        public override string ProviderId => "Test";

        public override string DisplayName => "Test";
    }

    private sealed class FakeSource(string name, OpenFinanceAccount account, IReadOnlyList<OpenFinanceTransaction> lines) : IOpenFinanceSource
    {
        public OpenFinanceSourceException? Failure { get; init; }

        public string SourceName => name;

        public Task<OpenFinanceConnectionCheck> CheckConnectionAsync(IntegrationSyncRequest request, CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(new OpenFinanceConnectionCheck("connected", Noon.AddYears(1))) : Task.FromException<OpenFinanceConnectionCheck>(Failure);

        public bool WasRead { get; private set; }

        public DateOnly? ReadFrom { get; private set; }

        public Task<OpenFinanceSnapshot> ReadAsync(IntegrationSyncRequest request, DateOnly? readFrom, CancellationToken cancellationToken)
        {
            WasRead = true;
            ReadFrom = readFrom;
            return Failure is null
                ? Task.FromResult(new OpenFinanceSnapshot([new OpenFinanceAccountTransactions(account, lines)], Noon.AddYears(1)))
                : Task.FromException<OpenFinanceSnapshot>(Failure);
        }
    }
}
