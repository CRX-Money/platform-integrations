using System.Text.Json.Nodes;

namespace CrxMoney.Integrations.Countries.Br.Tests;

public sealed class BrazilianInstitutionsTests
{
    [Fact]
    public void Every_entry_of_the_shipped_list_is_well_formed()
    {
        // Parse enforces the rules (codes, unique keys, canonical https://host/ websites); this re-checks them independently.
        var json = JsonNode.Parse(BrazilianInstitutions.ReadResource())!;
        var entries = json["institutions"]!.AsArray().Select(e => e!).ToList();

        Assert.StartsWith("https://www.bcb.gov.br/", json["source"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(DateOnly.TryParseExact(json["retrievedOn"]!.GetValue<string>(), "yyyy-MM-dd", out _));
        Assert.Equal(entries.Count, BrazilianInstitutions.All.Count);
        Assert.Equal(entries.Count, entries.Select(e => e["compe"]!.GetValue<string>()).Distinct().Count());
        Assert.Equal(entries.Count, entries.Select(e => e["ispb"]!.GetValue<string>()).Distinct().Count());
        // A website is one account operator. Only these operators have two codes of their own; any other repeat is a mistake.
        var shared = entries.GroupBy(e => e["website"]!.GetValue<string>())
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(e => e["compe"]!.GetValue<string>())));
        Assert.Equal(new Dictionary<string, string>
        {
            ["https://www.bv.com.br/"] = "413 655",
            ["https://www.citibank.com.br/"] = "477 745",
        }, shared);
        Assert.All(entries.GroupBy(e => e["website"]!.GetValue<string>()), g => Assert.Single(g.Select(e => e["name"]!.GetValue<string>()).Distinct()));
        Assert.All(entries, e =>
        {
            Assert.Equal(["compe", "ispb", "name", "legalName", "website"], e.AsObject().Select(p => p.Key));
            Assert.Matches("^[0-9]{3}$", e["compe"]!.GetValue<string>());
            Assert.Matches("^[0-9]{8}$", e["ispb"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(e["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(e["legalName"]!.GetValue<string>()));
            var website = new Uri(e["website"]!.GetValue<string>());
            Assert.Equal("https", website.Scheme);
            Assert.Equal("/", website.AbsolutePath);
            Assert.Equal(e["website"]!.GetValue<string>(), $"https://{website.Host}/");
        });
        // Sorted by code, so a review diff shows where an entry was added.
        var codes = entries.Select(e => e["compe"]!.GetValue<string>()).ToList();
        Assert.Equal(codes.Order(StringComparer.Ordinal), codes);
    }

    [Theory]
    [InlineData("077", "Inter", "https://inter.co/")]
    [InlineData("77", "Inter", "https://inter.co/")]
    [InlineData("341", "Itaú", "https://www.itau.com.br/")]
    [InlineData("237", "Bradesco", "https://banco.bradesco/")]
    [InlineData("001", "Banco do Brasil", "https://www.bb.com.br/")]
    public void A_code_finds_its_institution(string compe, string name, string website)
    {
        var institution = BrazilianInstitutions.Find(compe);

        Assert.NotNull(institution);
        Assert.Equal(name, institution.Name);
        Assert.Equal(website, BrazilianInstitutions.WebsiteOf(compe));
    }

    /// <summary>
    /// The business institutions the owner curated from Pluggy's Open Finance coverage, groups 1 to 4. The README lists the
    /// ones left out (no COMPE code in the Banco Central file) and why.
    /// </summary>
    public static TheoryData<string, string, string> CuratedBusinessInstitutions => new()
    {
        // 1. Large and traditional banks.
        { "001", "Banco do Brasil", "https://www.bb.com.br/" },
        { "237", "Bradesco", "https://banco.bradesco/" },
        { "104", "Caixa", "https://www.caixa.gov.br/" },
        { "341", "Itaú", "https://www.itau.com.br/" },
        { "033", "Santander", "https://www.santander.com.br/" },
        { "422", "Safra", "https://www.safra.com.br/" },
        { "041", "Banrisul", "https://www.banrisul.com.br/" },
        { "745", "Citi", "https://www.citibank.com.br/" },
        { "477", "Citi", "https://www.citibank.com.br/" },
        { "655", "Banco BV", "https://www.bv.com.br/" },
        { "413", "Banco BV", "https://www.bv.com.br/" },
        { "004", "Banco do Nordeste", "https://www.bnb.gov.br/" },
        { "070", "BRB", "https://www.brb.com.br/" },
        { "389", "Banco Mercantil", "https://www.bancomercantil.com.br/" },
        { "637", "Sofisa", "https://www.sofisa.com.br/" },
        { "318", "Banco BMG", "https://www.bancobmg.com.br/" },
        { "612", "Banco Guanabara", "https://www.bancoguanabara.com.br/" },
        { "069", "Crefisa", "https://www.crefisa.com.br/" },
        { "468", "Porto Bank", "https://www.portoseguro.com.br/" },
        // 2. Digital banks, fintechs and payments.
        { "077", "Inter", "https://inter.co/" },
        { "260", "Nubank", "https://nubank.com.br/" },
        { "336", "C6 Bank", "https://www.c6bank.com.br/" },
        { "323", "Mercado Pago", "https://www.mercadopago.com.br/" },
        { "290", "PagBank", "https://pagbank.com.br/" },
        { "542", "InfinitePay", "https://www.infinitepay.io/" },
        { "197", "Stone", "https://www.stone.com.br/" },
        { "380", "PicPay", "https://picpay.com/" },
        { "362", "Cielo", "https://www.cielo.com.br/" },
        { "509", "Celcoin", "https://www.celcoin.com.br/" },
        { "517", "PagueVeloz", "https://www.pagueveloz.com.br/" },
        { "329", "QI Tech", "https://qitech.com.br/" },
        { "594", "ASA", "https://www.asa.com.br/" },
        // 3. Credit cooperatives: the system's own code (cooperative bank or central), not a local cooperative's.
        { "748", "Sicredi", "https://www.sicredi.com.br/" },
        { "756", "Sicoob", "https://www.sicoob.com.br/" },
        { "136", "Unicred", "https://www.unicred.com.br/" },
        // 4. Investment platforms and brokers.
        { "208", "BTG Pactual", "https://www.btgpactual.com/" },
        { "102", "XP Investimentos", "https://www.xpi.com.br/" },
        { "565", "Ágora", "https://www.agorainvestimentos.com.br/" },
        { "352", "Toro", "https://www.toroinvestimentos.com.br/" },
    };

    [Theory]
    [MemberData(nameof(CuratedBusinessInstitutions))]
    public void Every_curated_business_institution_is_listed(string compe, string name, string website)
    {
        var institution = BrazilianInstitutions.Find(compe);

        Assert.NotNull(institution);
        Assert.Equal(name, institution.Name);
        Assert.Equal(website, institution.Website.AbsoluteUri);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("1234")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_or_malformed_code_finds_nothing(string? compe)
    {
        Assert.Null(BrazilianInstitutions.Find(compe));
        Assert.Null(BrazilianInstitutions.WebsiteOf(compe));
    }

    [Theory]
    [InlineData("""{"institutions":[{"compe":"77","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/"}]}""", "COMPE")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"1","name":"A","legalName":"A S.A.","website":"https://a.example/"}]}""", "ISPB")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":" ","legalName":"A S.A.","website":"https://a.example/"}]}""", "name")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"http://a.example/"}]}""", "website")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/conta"}]}""", "website")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://A.example"}]}""", "website")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/"},{"compe":"077","ispb":"00000002","name":"B","legalName":"B S.A.","website":"https://b.example/"}]}""", "twice")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/"},{"compe":"078","ispb":"00000001","name":"B","legalName":"B S.A.","website":"https://b.example/"}]}""", "twice")]
    [InlineData("""{"institutions":[]}""", "no institutions")]
    [InlineData("""not json""", "JSON")]
    public void A_malformed_list_is_rejected_with_the_rule_it_breaks(string json, string expected)
    {
        var failure = Assert.Throws<FormatException>(() => BrazilianInstitutions.Parse(json));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }
}
