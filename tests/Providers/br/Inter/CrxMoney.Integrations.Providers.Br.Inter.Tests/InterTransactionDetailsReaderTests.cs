using System.Text.Json;
using CrxMoney.Integrations.Providers.Br.Inter.Mapping;
using CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests;

public sealed class InterTransactionDetailsReaderTests
{
    private static JsonElement Details(string json) => JsonDocument.Parse(SyntheticTaxIds.Fill(json)).RootElement;

    [Fact]
    public void Credit_takes_the_payer_and_ignores_the_account_holder_as_payee()
    {
        var details = Details("""
            {"nomePagador":"Cliente Exemplo Ltda","cpfCnpjPagador":"{{CNPJ:1}}",
             "nomeRecebedor":"Empresa Exemplo Ltda","cpfCnpjRecebedor":"{{CNPJ:9}}"}
            """);

        var (counterparty, _) = InterTransactionDetailsReader.Read(details, isCredit: true);

        Assert.Equal("Cliente Exemplo Ltda", counterparty.Name);
        Assert.Equal(SyntheticTaxIds.Cnpj(1), counterparty.TaxId?.Value);
        Assert.Equal(BrazilianTaxId.Cnpj, counterparty.TaxId?.Kind);
    }

    [Fact]
    public void Documented_pix_sample_keys_are_recognised_for_both_directions()
    {
        // Key names from the portal's response sample for GET /banking/v2/extrato/completo; values are synthetic.
        const string sample = """
            {"txId":"SYNTHTXID","nomePagador":"Pagador Exemplo","descricaoPix":"Aluguel","cpfCnpjPagador":"{{CPF:5}}",
             "contaBancariaRecebedor":"23456789","nomeEmpresaPagador":"Banco Pagador Exemplo","tipoDetalhe":"COMPLETE",
             "endToEndId":"E00000000202206201436SYNTH","chavePixRecebedor":"+5510912345678","nomeEmpresaRecebedor":"Banco Recebedor Exemplo",
             "nomeRecebedor":"Recebedor Exemplo","agenciaRecebedor":"0089","cpfCnpjRecebedor":"{{CPF:6}}","origemMovimentacao":"CHAVE",
             "codigoSolicitacao":"00000000-0000-4000-8000-000000000099"}
            """;

        var (credit, creditPix) = InterTransactionDetailsReader.Read(Details(sample), isCredit: true);
        var (debit, debitPix) = InterTransactionDetailsReader.Read(Details(sample), isCredit: false);

        Assert.Equal(new InterCounterparty("Pagador Exemplo", new BrazilianTaxId(SyntheticTaxIds.Cpf(5), BrazilianTaxId.Cpf), "Banco Pagador Exemplo", null, null), credit);
        Assert.Equal(new InterPixIdentifiers("E00000000202206201436SYNTH", "SYNTHTXID", null), creditPix);
        Assert.Equal(new InterCounterparty("Recebedor Exemplo", new BrazilianTaxId(SyntheticTaxIds.Cpf(6), BrazilianTaxId.Cpf), "Banco Recebedor Exemplo", "0089", "23456789"), debit);
        Assert.Equal("+5510912345678", debitPix.Key);
    }

    [Fact]
    public void Debit_takes_the_payee_from_a_nested_object()
    {
        var details = Details("""
            {"pagador":{"nome":"Empresa Exemplo Ltda","cpfCnpj":"{{CNPJ:9}}"},
             "recebedor":{"nome":"Fulano de Tal","cpfCnpj":"{{CPF:1}}","banco":"Banco Exemplo","agencia":"0001","conta":"12345678-9"}}
            """);

        var (counterparty, _) = InterTransactionDetailsReader.Read(details, isCredit: false);

        Assert.Equal(new InterCounterparty("Fulano de Tal", new BrazilianTaxId(SyntheticTaxIds.Cpf(1), BrazilianTaxId.Cpf), "Banco Exemplo", "0001", "12345678-9"), counterparty);
    }

    [Fact]
    public void Fields_without_a_role_are_used_when_no_counterparty_field_exists()
    {
        var details = Details("""{"nome":"Fornecedor Exemplo","cpfCnpj":"{{CNPJ:4}}","nomePagador":"Empresa Exemplo Ltda"}""");

        var (counterparty, _) = InterTransactionDetailsReader.Read(details, isCredit: false);

        Assert.Equal("Fornecedor Exemplo", counterparty.Name);
        Assert.Equal(SyntheticTaxIds.Cnpj(4), counterparty.TaxId?.Value);
    }

    [Fact]
    public void A_counterparty_field_wins_over_a_roleless_one()
    {
        var details = Details("""{"nome":"Generic","nomeFavorecido":"Favorecido Exemplo"}""");

        var (counterparty, _) = InterTransactionDetailsReader.Read(details, isCredit: false);

        Assert.Equal("Favorecido Exemplo", counterparty.Name);
    }

    [Fact]
    public void Type_descriptors_and_bank_names_are_not_taken_as_values()
    {
        var details = Details("""
            {"tipoChavePix":"EMAIL","tipoContaRecebedor":"CORRENTE","nomeBancoRecebedor":"Banco Exemplo S.A.",
             "nomeRecebedor":"Fulano de Tal","contaRecebedor":"99999-9","chavePixRecebedor":"fulano@example.com"}
            """);

        var (counterparty, pix) = InterTransactionDetailsReader.Read(details, isCredit: false);

        Assert.Equal("Fulano de Tal", counterparty.Name);
        Assert.Equal("Banco Exemplo S.A.", counterparty.Bank);
        Assert.Equal("99999-9", counterparty.Account);
        Assert.Equal("fulano@example.com", pix.Key);
    }

    [Fact]
    public void Masked_or_invalid_tax_ids_are_not_used()
    {
        var details = Details("""{"nomePagador":"Fulano","cpfCnpjPagador":"***.456.789-**","cpf":"123.456.789-00"}""");

        var (counterparty, _) = InterTransactionDetailsReader.Read(details, isCredit: true);

        Assert.Equal("Fulano", counterparty.Name);
        Assert.Null(counterparty.TaxId);
    }

    [Fact]
    public void Reads_pix_identifiers_and_the_counterparty_key()
    {
        var details = Details("""
            {"endToEndId":"E00000000202603031140SYNTH000002","txId":"SYNTHTXID000002",
             "chavePixRecebedor":"00000000-0000-4000-8000-000000000001","chavePixPagador":"pagador@example.com"}
            """);

        var (_, pix) = InterTransactionDetailsReader.Read(details, isCredit: false);

        Assert.Equal(new InterPixIdentifiers("E00000000202603031140SYNTH000002", "SYNTHTXID000002", "00000000-0000-4000-8000-000000000001"), pix);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"texto\"")]
    [InlineData("{\"tipoDetalhe\":\"OUTROS\",\"produto\":\"CDB\"}")]
    public void Unknown_or_empty_details_yield_nothing(string json)
    {
        var (counterparty, pix) = InterTransactionDetailsReader.Read(JsonDocument.Parse(json).RootElement, isCredit: true);

        Assert.True(counterparty.IsEmpty);
        Assert.True(pix.IsEmpty);
    }

    [Theory]
    [InlineData("CPF/CNPJ Beneficiário", "cpfcnpjbeneficiario")]
    [InlineData("nome_pagador", "nomepagador")]
    [InlineData("contaDestino", "contadestino")]
    public void Normalises_keys_to_ascii_lower_case(string key, string expected)
    {
        Assert.Equal(expected, InterTransactionDetailsReader.NormalizeKey(key));
    }
}
