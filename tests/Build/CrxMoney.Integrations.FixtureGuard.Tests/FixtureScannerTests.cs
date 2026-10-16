namespace CrxMoney.Integrations.FixtureGuard.Tests;

// This file lives under tests/, so the guard scans it too. Every value the guard must flag is
// therefore assembled at run time (check digits computed, strings concatenated) instead of
// being written as a literal.
public sealed class FixtureScannerTests
{
    private static string[] Rules(string content) =>
        FixtureScanner.Scan("fixture.txt", content).Select(finding => finding.Rule).ToArray();

    [Fact]
    public void Check_digits_match_the_published_examples()
    {
        Assert.Equal("123456789" + "09", BrazilianDocuments.CompleteCpf("123456789"));
        Assert.Equal("112223330001" + "81", BrazilianDocuments.CompleteCnpj("112223330001"));
        Assert.Equal("12ABC34501DE" + "35", BrazilianDocuments.CompleteCnpj("12ABC34501DE"));
    }

    [Fact]
    public void Flags_cpf_with_valid_check_digits_in_any_format()
    {
        var cpf = BrazilianDocuments.CompleteCpf("529982247");
        var formatted = $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}";

        Assert.Equal(["FG001"], Rules($"\"cpf\": \"{cpf}\""));
        Assert.Equal(["FG001"], Rules($"Pagador {formatted}"));
    }

    [Theory]
    [InlineData("123.456.789-00")] // wrong check digits
    [InlineData("111.111.111-11")] // repeated digit, never issued
    [InlineData("12345678900")]
    public void Accepts_synthetic_cpf_forms(string value)
    {
        Assert.Empty(Rules($"\"cpf\": \"{value}\""));
    }

    [Fact]
    public void Flags_numeric_and_alphanumeric_cnpj()
    {
        var numeric = BrazilianDocuments.CompleteCnpj("112223330001");
        var alphanumeric = BrazilianDocuments.CompleteCnpj("12ABC34501DE");
        var formatted = $"{alphanumeric[..2]}.{alphanumeric[2..5]}.{alphanumeric[5..8]}/{alphanumeric[8..12]}-{alphanumeric[12..]}";

        Assert.Equal(["FG002"], Rules(numeric));
        Assert.Equal(["FG002"], Rules(formatted));
        Assert.Equal(["FG002"], Rules($"\"cnpj\": \"{alphanumeric}\""));
    }

    [Fact]
    public void Ignores_bare_alphanumeric_cnpj_outside_cnpj_context()
    {
        // Fourteen-character ids are common; only a nearby "cnpj" makes them suspicious.
        Assert.Empty(Rules($"\"id\": \"{BrazilianDocuments.CompleteCnpj("12ABC34501DE")}\""));
    }

    [Theory]
    [InlineData("12.345.678/0001-00")]
    [InlineData("00.000.000/0000-00")]
    public void Accepts_synthetic_cnpj_forms(string value)
    {
        Assert.Empty(Rules($"\"cnpj\": \"{value}\""));
    }

    [Fact]
    public void Ignores_ofx_timestamps_that_happen_to_have_cnpj_check_digits()
    {
        var timestamp = Enumerable.Range(0, 60)
            .Select(minute => BrazilianDocuments.CompleteCnpj($"2026010212{minute:00}"))
            .First(candidate => int.Parse(candidate[12..]) < 60);

        Assert.Empty(Rules($"<DTPOSTED>{timestamp}[-3:BRT]"));
    }

    [Fact]
    public void Flags_email_outside_reserved_domains()
    {
        Assert.Equal(["FG003"], Rules("\"chave\": \"fulano" + "@" + "gmail.com\""));
        Assert.Empty(Rules("\"chave\": \"fulano@example.com\""));
        Assert.Empty(Rules("\"email\": \"pagador@empresa.test\""));
    }

    [Fact]
    public void Flags_phone_numbers_with_real_area_codes()
    {
        Assert.Equal(["FG004"], Rules("\"chavePix\": \"+55" + "11912345678\""));
        Assert.Equal(["FG004"], Rules("Telefone (21) 9" + "8765-4321"));
        Assert.Empty(Rules("\"chavePix\": \"+5510912345678\""));
        Assert.Empty(Rules("Telefone (20) 98765-4321"));
    }

    [Fact]
    public void Flags_random_pix_keys_only_in_pix_context()
    {
        var evp = Guid.NewGuid().ToString();

        Assert.Equal(["FG005"], Rules($"\"chavePixRecebedor\": \"{evp}\""));
        Assert.Empty(Rules($"\"idTransacao\": \"{evp}\""));
        Assert.Empty(Rules("\"chavePixRecebedor\": \"00000000-0000-4000-8000-000000000001\""));
    }

    [Theory]
    [InlineData("SYNTH-0001")]
    [InlineData("12345678-9")]
    [InlineData("99999999")]
    [InlineData("1234")]
    public void Accepts_synthetic_ofx_account_ids(string value)
    {
        Assert.Empty(Rules($"<ACCTID>{value}"));
    }

    [Fact]
    public void Flags_real_looking_ofx_account_ids()
    {
        Assert.Equal(["FG007"], Rules("<ACC" + "TID>48213977</ACCTID>"));
    }

    [Fact]
    public void Flags_account_numbers_next_to_an_agency()
    {
        var json = string.Join('\n', "{", "  \"agencia\": \"0001\",", "  \"conta\": \"" + "4821397-7\"", "}");

        var finding = Assert.Single(FixtureScanner.Scan("statement.json", json));
        Assert.Equal(("FG006", 3), (finding.Rule, finding.Line));
    }

    [Fact]
    public void Ignores_account_numbers_without_an_agency_or_that_are_synthetic()
    {
        Assert.Empty(Rules("\"conta\": \"" + "4821397-7\""));
        Assert.Empty(Rules("Agência 0001\nConta corrente 12345678-9"));
    }

    [Fact]
    public void Masks_values_in_findings()
    {
        var finding = new Finding("f", 1, "FG001", "CPF", BrazilianDocuments.CompleteCpf("529982247"));

        Assert.Equal("529********", finding.Masked);
    }
}
