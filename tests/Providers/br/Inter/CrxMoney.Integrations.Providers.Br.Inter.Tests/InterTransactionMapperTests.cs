using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.Inter.Api;
using CrxMoney.Integrations.Providers.Br.Inter.Mapping;
using CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests;

public sealed class InterTransactionMapperTests
{
    private const long ConnectionId = 42;
    private const string Account = "12345678";

    private static IntegrationImportEvent Map(string id) =>
        InterTransactionMapper.Map(Fixtures.Transaction(id), ConnectionId, Account, InterEnvironment.Sandbox);

    private static JsonNode Data(IntegrationImportEvent importEvent) => JsonNode.Parse(importEvent.ProviderData!)!;

    [Fact]
    public void Fixtures_cover_both_operation_types_and_every_documented_transaction_type()
    {
        var transactions = Fixtures.AllTransactions();

        Assert.Equal(["C", "D"], transactions.Select(t => t.TipoOperacao).Distinct().Order());
        Assert.Equal(
            ["BOLETO_COBRANCA", "CAMBIO", "ESTORNO", "INVESTIMENTO", "OUTROS", "PAGAMENTO", "PIX", "TRANSFERENCIA"],
            transactions.Select(t => t.TipoTransacao).Distinct().Order());
    }

    [Fact]
    public void Credit_becomes_a_brl_deposit_on_the_account_at_midnight_in_brazil()
    {
        var importEvent = Map("SYNTH-TX-0001");

        Assert.Equal("SYNTH-TX-0001", importEvent.ExternalId);
        Assert.Equal("inter:42:SYNTH-TX-0001", importEvent.IdempotencyKey);
        Assert.Equal("deposit", importEvent.Type);
        Assert.Equal("Single", importEvent.Side);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.FromHours(-3)), importEvent.OccurredAtUtc);
        Assert.Equal(TimeSpan.FromHours(-3), importEvent.OccurredAtUtc.Offset);
        Assert.Equal(Account, importEvent.ExternalAccountId);
        Assert.Equal("BRL", importEvent.AssetCode);
        Assert.Null(importEvent.Quantity);
        Assert.Equal(1500.00m, importEvent.Amount);
        Assert.Equal(InterImportSchemas.DepositSchemaId, importEvent.ProviderDataSchemaId);
        Assert.Equal((short)1, importEvent.ProviderDataSchemaVersion);
    }

    [Fact]
    public void Debit_becomes_a_withdrawal_with_a_positive_amount()
    {
        var importEvent = Map("SYNTH-TX-0002");

        Assert.Equal("withdrawal", importEvent.Type);
        Assert.Equal(250.75m, importEvent.Amount);
        Assert.Equal(InterImportSchemas.WithdrawalSchemaId, importEvent.ProviderDataSchemaId);
        Assert.Equal("fiat.withdrawal", Data(importEvent)["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Provider_data_carries_inter_fields_counterparty_tax_id_and_pix_ids()
    {
        var data = Data(Map("SYNTH-TX-0001"));

        Assert.Equal("Inter", data["provider"]!.GetValue<string>());
        Assert.Equal("fiat.deposit", data["kind"]!.GetValue<string>());
        Assert.Equal("sandbox", data["environment"]!.GetValue<string>());
        Assert.Equal("2026-03-02", data["transactionDate"]!.GetValue<string>());
        Assert.Equal("2026-03-02 09:15:00", data["inclusionDate"]!.GetValue<string>());
        Assert.Equal("PIX", data["tipoTransacao"]!.GetValue<string>());
        Assert.Equal("C", data["tipoOperacao"]!.GetValue<string>());
        Assert.Equal("Pix recebido", data["titulo"]!.GetValue<string>());
        Assert.Equal("PIX RECEBIDO - Cliente Exemplo Ltda", data["descricao"]!.GetValue<string>());
        Assert.Equal("Cliente Exemplo Ltda", data["counterparty"]!["name"]!.GetValue<string>());
        Assert.Equal(SyntheticTaxIds.Cnpj(1), data["counterparty"]!["taxId"]!.GetValue<string>());
        Assert.Equal("CNPJ", data["counterparty"]!["taxIdKind"]!.GetValue<string>());
        Assert.Equal("BR", data["counterparty"]!["taxIdCountry"]!.GetValue<string>());
        Assert.Equal("E00000000202603020915SYNTH000001", data["pix"]!["endToEndId"]!.GetValue<string>());
        Assert.Equal("SYNTHTXID000001", data["pix"]!["txId"]!.GetValue<string>());
        Assert.Null(data["transferHint"]);
    }

    [Fact]
    public void Detalhes_is_kept_verbatim()
    {
        var transaction = Fixtures.Transaction("SYNTH-TX-0002");
        var data = Data(InterTransactionMapper.Map(transaction, ConnectionId, Account, InterEnvironment.Sandbox));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(transaction.Detalhes.GetRawText()), data["detalhes"]));
    }

    [Fact]
    public void Investment_entries_carry_a_transfer_hint()
    {
        var data = Data(Map("SYNTH-TX-0006"));

        Assert.Equal("investment", data["transferHint"]!.GetValue<string>());
        Assert.Null(data["counterparty"]);
    }

    [Fact]
    public void Numeric_valor_and_document_number_are_accepted()
    {
        var importEvent = Map("SYNTH-TX-0005");

        Assert.Equal(980.5m, importEvent.Amount);
        Assert.Equal("0000123", Data(Map("SYNTH-TX-0004"))["numeroDocumento"]!.GetValue<string>());
    }

    [Fact]
    public void Entries_without_details_still_map()
    {
        var data = Data(Map("SYNTH-TX-0009"));

        Assert.Null(data["detalhes"]);
        Assert.Null(data["counterparty"]);
        Assert.Equal("OUTROS", data["tipoTransacao"]!.GetValue<string>());
    }

    [Fact]
    public void Masked_tax_id_in_a_refund_is_not_promoted()
    {
        var data = Data(Map("SYNTH-TX-0008"));

        Assert.Null(data["counterparty"]);
    }

    [Fact]
    public void Idempotency_keys_are_deterministic_and_scoped_to_the_connection()
    {
        var first = Fixtures.AllTransactions().Select(t => InterTransactionMapper.Map(t, ConnectionId, Account, InterEnvironment.Sandbox).IdempotencyKey).ToArray();
        var again = Fixtures.AllTransactions().Select(t => InterTransactionMapper.Map(t, ConnectionId, Account, InterEnvironment.Sandbox).IdempotencyKey).ToArray();
        var otherConnection = InterTransactionMapper.Map(Fixtures.Transaction("SYNTH-TX-0001"), 43, Account, InterEnvironment.Sandbox).IdempotencyKey;

        Assert.Equal(first, again);
        Assert.Equal(first.Length, first.Distinct().Count());
        Assert.DoesNotContain(otherConnection, first);
    }

    [Theory]
    [InlineData("""{"dataTransacao":"2026-03-02","tipoOperacao":"C","valor":"1.00"}""", "idTransacao")]
    [InlineData("""{"idTransacao":"X1","tipoOperacao":"C","valor":"1.00"}""", "dataTransacao")]
    [InlineData("""{"idTransacao":"X1","dataTransacao":"02/03/2026","tipoOperacao":"C","valor":"1.00"}""", "dataTransacao")]
    [InlineData("""{"idTransacao":"X1","dataTransacao":"2026-03-02","tipoOperacao":"X","valor":"1.00"}""", "tipoOperacao")]
    [InlineData("""{"idTransacao":"X1","dataTransacao":"2026-03-02","tipoOperacao":"D","valor":"1,00"}""", "valor")]
    public void Entries_missing_required_fields_fail_instead_of_being_skipped(string json, string field)
    {
        var page = JsonSerializer.Deserialize($$"""{"transacoes":[{{json}}]}""", InterApiJsonContext.Default.InterStatementPage)!;

        var error = Assert.Throws<InterMappingException>(() =>
            InterTransactionMapper.Map(page.Transacoes![0], ConnectionId, Account, InterEnvironment.Sandbox));
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Booking_date_with_a_time_part_keeps_the_date()
    {
        Assert.Equal(new DateOnly(2026, 3, 2), InterTransactionMapper.ParseBookingDate("2026-03-02T23:30:00"));
        Assert.Equal(new DateOnly(2026, 3, 2), InterTransactionMapper.ParseBookingDate("2026-03-02 23:30:00"));
    }

    [Fact]
    public void Banking_today_follows_the_brazilian_calendar()
    {
        // 01:30 UTC on 17 October is still 16 October in São Paulo (UTC-3).
        Assert.Equal(new DateOnly(2026, 10, 16), InterTransactionMapper.BankingDate(new DateTimeOffset(2026, 10, 17, 1, 30, 0, TimeSpan.Zero)));
    }
}
