using CrxMoney.Integrations.Providers.Br.Inter.Mapping;
using CrxMoney.Integrations.Providers.Br.Inter.Tests.Support;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests;

public sealed class BrazilianTaxIdTests
{
    [Fact]
    public void Accepts_generated_cpf_and_cnpj_with_or_without_punctuation()
    {
        var cpf = SyntheticTaxIds.Cpf(7);
        var cnpj = SyntheticTaxIds.Cnpj(7);
        var formattedCpf = $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}";
        var formattedCnpj = $"{cnpj[..2]}.{cnpj[2..5]}.{cnpj[5..8]}/{cnpj[8..12]}-{cnpj[12..]}";

        Assert.Equal(new BrazilianTaxId(cpf, BrazilianTaxId.Cpf), BrazilianTaxId.TryParse(cpf));
        Assert.Equal(new BrazilianTaxId(cpf, BrazilianTaxId.Cpf), BrazilianTaxId.TryParse(formattedCpf));
        Assert.Equal(new BrazilianTaxId(cnpj, BrazilianTaxId.Cnpj), BrazilianTaxId.TryParse(formattedCnpj));
    }

    [Fact]
    public void Accepts_the_2026_alphanumeric_cnpj_case_insensitively()
    {
        var cnpj = SyntheticTaxIds.CompleteCnpj("12ABC34501DE");

        Assert.Equal(new BrazilianTaxId(cnpj, BrazilianTaxId.Cnpj), BrazilianTaxId.TryParse(cnpj.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("123.456.789-00")]          // wrong check digits
    [InlineData("111.111.111-11")]          // repeated digit, never issued
    [InlineData("12.345.678/0001-00")]      // wrong check digits
    [InlineData("***.456.789-**")]          // masked by the bank
    [InlineData("1234567890")]              // too short
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_values_that_are_not_valid_tax_ids(string? value)
    {
        Assert.Null(BrazilianTaxId.TryParse(value));
    }

    [Fact]
    public void Rejects_a_cpf_with_one_wrong_check_digit()
    {
        var cpf = SyntheticTaxIds.Cpf(3);
        var broken = cpf[..10] + (char)('0' + (cpf[10] - '0' + 1) % 10);

        Assert.Null(BrazilianTaxId.TryParse(broken));
    }
}
