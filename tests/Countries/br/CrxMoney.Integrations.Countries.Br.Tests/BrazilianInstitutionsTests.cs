using System.Globalization;
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
            Assert.Equal(["compe", "ispb", "name", "legalName", "website", "currencies"], e.AsObject().Select(p => p.Key));
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
        { "352", "Toro (Santander Corretora)", "https://www.toroinvestimentos.com.br/" },
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
    [InlineData("""{"institutions":[{"compe":"77","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/","currencies":["BRL"]}]}""", "COMPE")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"1","name":"A","legalName":"A S.A.","website":"https://a.example/","currencies":["BRL"]}]}""", "ISPB")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":" ","legalName":"A S.A.","website":"https://a.example/","currencies":["BRL"]}]}""", "name")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"http://a.example/","currencies":["BRL"]}]}""", "website")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/conta","currencies":["BRL"]}]}""", "website")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://A.example","currencies":["BRL"]}]}""", "website")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/","currencies":["BRL"]},{"compe":"077","ispb":"00000002","name":"B","legalName":"B S.A.","website":"https://b.example/","currencies":["BRL"]}]}""", "twice")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/","currencies":["BRL"]},{"compe":"078","ispb":"00000001","name":"B","legalName":"B S.A.","website":"https://b.example/","currencies":["BRL"]}]}""", "twice")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/"}]}""", "home currency")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/","currencies":[]}]}""", "home currency")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/","currencies":["USD","BRL"]}]}""", "home currency")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/","currencies":["BRL","usd"]}]}""", "three-letter")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/","currencies":["BRL","US"]}]}""", "three-letter")]
    [InlineData("""{"institutions":[{"compe":"077","ispb":"00000001","name":"A","legalName":"A S.A.","website":"https://a.example/","currencies":["BRL","USD","USD"]}]}""", "appears twice")]
    [InlineData("""{"institutions":[]}""", "no institutions")]
    [InlineData("""not json""", "JSON")]
    public void A_malformed_list_is_rejected_with_the_rule_it_breaks(string json, string expected)
    {
        var failure = Assert.Throws<FormatException>(() => BrazilianInstitutions.Parse(json));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every ISO 4217 currency of a country or region .NET knows (ICU's region data): a declared code must be one of them,
    /// so a typo or an invented code never reaches a host's currency picker.
    /// </summary>
    private static readonly HashSet<string> KnownIsoCurrencies = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(c =>
        {
            try { return new RegionInfo(c.Name).ISOCurrencySymbol; }
            catch (ArgumentException) { return null; }
        })
        .OfType<string>()
        .Where(code => code.Length == 3)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The institutions that offer accounts in more than the home currency, each researched on the institution's own pages
    /// (the README's "Account currencies" table gives the source). Any other institution lists BRL only; adding one here
    /// needs a source in that table.
    /// </summary>
    private static readonly Dictionary<string, string[]> ResearchedMultiCurrency = new(StringComparer.Ordinal)
    {
        ["033"] = ["BRL", "USD"],        // Santander: Select Global.
        ["077"] = ["BRL", "USD", "EUR"], // Inter: Global Account.
        ["208"] = ["BRL", "USD"],        // BTG Pactual: Contas Internacionais.
        ["237"] = ["BRL", "USD"],        // Bradesco: My Account.
        ["336"] = ["BRL", "USD", "EUR"], // C6 Bank: C6 Conta Global.
        ["341"] = ["BRL", "USD", "EUR"], // Itaú: Conta Global.
    };

    [Fact]
    public void Every_declared_currency_is_a_known_iso_4217_code()
    {
        Assert.Contains("BRL", KnownIsoCurrencies);
        Assert.Contains("EUR", KnownIsoCurrencies);
        Assert.All(BrazilianInstitutions.All, institution =>
            Assert.All(institution.Currencies, code => Assert.Contains(code, KnownIsoCurrencies)));
    }

    [Fact]
    public void The_home_currency_comes_first_and_no_currency_repeats()
    {
        Assert.Equal("BRL", BrazilianInstitutions.HomeCurrency);
        Assert.All(BrazilianInstitutions.All, institution =>
        {
            Assert.NotEmpty(institution.Currencies);
            Assert.Equal(BrazilianInstitutions.HomeCurrency, institution.Currencies[0]);
            Assert.Equal(institution.Currencies.Count, institution.Currencies.Distinct(StringComparer.Ordinal).Count());
        });
    }

    [Fact]
    public void Only_the_researched_institutions_offer_more_than_the_home_currency()
    {
        var multi = BrazilianInstitutions.All
            .Where(i => i.Currencies.Count > 1)
            .ToDictionary(i => i.Compe, i => i.Currencies.ToArray(), StringComparer.Ordinal);

        Assert.Equal(ResearchedMultiCurrency.Keys.Order(StringComparer.Ordinal), multi.Keys.Order(StringComparer.Ordinal));
        Assert.All(ResearchedMultiCurrency, pair => Assert.Equal(pair.Value, multi[pair.Key]));
    }

    [Theory]
    [InlineData("077", "BRL USD EUR")]
    [InlineData("208", "BRL USD")]
    [InlineData("001", "BRL")]
    [InlineData("260", "BRL")]
    [InlineData("999", "BRL")]
    [InlineData(null, "BRL")]
    public void A_code_gives_its_account_currencies_and_an_unknown_one_the_home_currency(string? compe, string expected)
    {
        Assert.Equal(expected, string.Join(' ', BrazilianInstitutions.CurrenciesOf(compe)));
    }

    [Fact]
    public void An_institution_built_without_currencies_offers_the_home_currency()
    {
        var institution = new BrazilianInstitution("999", "99999999", "Example", "Example S.A.", new Uri("https://example.com/"));

        Assert.Equal([BrazilianInstitutions.HomeCurrency], institution.Currencies);
    }
}
