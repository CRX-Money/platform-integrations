namespace CrxMoney.Integrations.Countries.Br.Tests;

public sealed class BrazilBuildingBlocksTests
{
    [Fact]
    public void Tax_ids_are_accepted_only_with_valid_check_digits()
    {
        var cpf = SyntheticTaxIds.Cpf(4);
        var cnpj = SyntheticTaxIds.Cnpj(4);
        var alphanumeric = SyntheticTaxIds.CompleteCnpj("12ABC34501DE");

        Assert.Equal(new BrazilianTaxId(cpf, BrazilianTaxId.Cpf), BrazilianTaxId.TryParse($"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}"));
        Assert.Equal(new BrazilianTaxId(cnpj, BrazilianTaxId.Cnpj), BrazilianTaxId.TryParse(cnpj));
        Assert.Equal(new BrazilianTaxId(alphanumeric, BrazilianTaxId.Cnpj), BrazilianTaxId.TryParse(alphanumeric.ToLowerInvariant()));
        Assert.Null(BrazilianTaxId.TryParse("123.456.789-00"));
        Assert.Null(BrazilianTaxId.TryParse("111.111.111-11"));
        Assert.Null(BrazilianTaxId.TryParse("***.456.789-**"));
        Assert.Null(BrazilianTaxId.TryParse(null));
    }

    [Theory]
    [InlineData("077/0001/12345678-9", "077", "0001", "123456789")]
    [InlineData("77 / 0001 / 0012345678-9", "077", "0001", "123456789")]
    [InlineData("001/1234-5/99999-9", "001", "12345", "999999")]
    public void Transfer_numbers_parse_into_the_bank_identity(string value, string compe, string branch, string number)
    {
        var account = BrazilianBankAccount.TryParseTransferNumber(value);

        Assert.Equal(new BrazilianBankAccount(compe, branch, number), account);
        Assert.Equal($"br:{compe}:{branch}:{number}", account!.ExternalAccountId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345678-9")]
    [InlineData("1234/0001/12345678-9")]
    [InlineData("077/0001/conta")]
    public void Anything_else_is_not_a_transfer_number(string? value)
    {
        Assert.Null(BrazilianBankAccount.TryParseTransferNumber(value));
    }

    [Fact]
    public void Card_ids_name_the_issuer_and_the_last_four_digits()
    {
        Assert.Equal("br:077:card:1234", BrazilianBankAccount.CardExternalAccountId("77", "1234"));
    }

    [Fact]
    public void Banking_calendar_dates_by_sao_paulo_time()
    {
        Assert.Equal(new DateOnly(2026, 10, 16), BrazilBankingCalendar.DateOf(new DateTimeOffset(2026, 10, 17, 2, 59, 59, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 17), BrazilBankingCalendar.DateOf(new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.FromHours(-3)), BrazilBankingCalendar.StartOfDay(new DateOnly(2026, 3, 10)));
    }
}
