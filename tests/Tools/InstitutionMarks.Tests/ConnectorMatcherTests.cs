using System.Text;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Tools.InstitutionMarks.Tests;

public sealed class ConnectorMatcherTests
{
    private static BrazilianInstitution Institution(string name, string website, string compe = "001", string ispb = "00000001") =>
        new(compe, ispb, name, name.ToUpperInvariant(), new Uri(website));

    private static PluggyConnector Connector(int id, string name, string? url, string? image = "https://cdn.example/x.svg") =>
        new(id, name, url, image, "BR", "PERSONAL_BANK", false);

    [Theory]
    [InlineData("https://www.itau.com.br/", "itau.com.br")]
    [InlineData("HTTPS://Nubank.com.br/path?q=1", "nubank.com.br")]
    [InlineData("https://banco.bradesco", "banco.bradesco")]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void A_host_is_normalised(string? url, string? expected) => Assert.Equal(expected, ConnectorMatcher.NormalizeHost(url));

    [Theory]
    [InlineData("itau.com.br", "itau.com.br", true)]
    [InlineData("caixa.gov.br", "internetbanking.caixa.gov.br", true)]
    [InlineData("internetbanking.caixa.gov.br", "caixa.gov.br", true)]
    [InlineData("com.br", "itau.com.br", false)]
    [InlineData("itau.com.br", "bradesco.com.br", false)]
    [InlineData("inter.co", "xinter.co", false)]
    [InlineData(null, "itau.com.br", false)]
    public void Two_hosts_are_the_same_site_only_through_a_subdomain(string? a, string? b, bool expected) => Assert.Equal(expected, ConnectorMatcher.SameSite(a, b));

    [Theory]
    [InlineData("Itaú Unibanco", "itau unibanco")]
    [InlineData("  C6  Bank! ", "c6 bank")]
    [InlineData("Ágora", "agora")]
    [InlineData(null, "")]
    public void A_name_is_normalised(string? name, string expected) => Assert.Equal(expected, ConnectorMatcher.NormalizeName(name));

    [Fact]
    public void The_website_decides_before_the_name()
    {
        var inter = Institution("Inter", "https://inter.co/");
        var connectors = new[]
        {
            Connector(1, "Inter", "https://www.other.com/"),
            Connector(2, "Banco Inter", "https://inter.co"),
        };

        var match = Assert.Single(ConnectorMatcher.Match([inter], connectors));

        Assert.Equal(MatchMethod.Website, match.Method);
        Assert.Equal(2, match.Connector!.Id);
    }

    [Fact]
    public void An_institution_falls_back_to_its_name_without_accents_or_case()
    {
        var agora = Institution("Ágora", "https://www.agorainvestimentos.com.br/");
        var connectors = new[] { Connector(9, "AGORA", "https://cdn.example/") };

        var match = Assert.Single(ConnectorMatcher.Match([agora], connectors));

        Assert.Equal(MatchMethod.Name, match.Method);
        Assert.Equal(9, match.Connector!.Id);
    }

    [Fact]
    public void A_name_prefix_counts_only_on_whole_words_and_the_personal_connector_wins()
    {
        var inter = Institution("Inter", "https://inter.co/");
        var connectors = new[]
        {
            Connector(3, "Inter Empresas", "https://cdn.example/"),
            Connector(4, "Intermedium", "https://cdn.example/"),
            Connector(5, "Inter Shop PJ Especial", "https://cdn.example/"),
        };

        var match = Assert.Single(ConnectorMatcher.Match([inter], connectors));

        Assert.Equal(MatchMethod.NamePrefix, match.Method);
        Assert.Equal(3, match.Connector!.Id);
        Assert.Equal([5], match.Others.Select(o => o.Id));
    }

    [Fact]
    public void An_exact_name_beats_a_longer_one_on_the_same_website()
    {
        var itau = Institution("Itaú", "https://www.itau.com.br/");
        var connectors = new[]
        {
            Connector(7, "Itaú Empresas", "https://www.itau.com.br"),
            Connector(8, "Itaú", "https://itau.com.br"),
        };

        Assert.Equal(8, Assert.Single(ConnectorMatcher.Match([itau], connectors)).Connector!.Id);
    }

    [Fact]
    public void A_connector_without_an_image_is_not_a_candidate_and_an_unmatched_institution_is_reported()
    {
        var nubank = Institution("Nubank", "https://nubank.com.br/");
        var connectors = new[] { Connector(1, "Nubank", "https://nubank.com.br", image: null), Connector(2, "Other", "https://other.com") };

        var match = Assert.Single(ConnectorMatcher.Match([nubank], connectors));

        Assert.Equal(MatchMethod.None, match.Method);
        Assert.Null(match.Connector);
    }

    [Fact]
    public void A_name_is_compared_without_spaces_and_without_its_explanation_in_parentheses()
    {
        var btg = Institution("BTG Pactual", "https://www.btgpactual.com/", "208");
        var toro = Institution("Toro (Santander Corretora)", "https://www.toroinvestimentos.com.br/", "352", "00000352");
        var connectors = new[]
        {
            Connector(214, "BTGPactual", null),
            Connector(796, "Toro Investimentos", null),
            Connector(797, "Toro Investimentos Empresas", null),
        };

        var matches = ConnectorMatcher.Match([btg, toro], connectors, new Dictionary<string, int>());

        Assert.Equal((MatchMethod.NameCompact, 214), (matches[0].Method, matches[0].Connector!.Id));
        Assert.Equal((MatchMethod.NamePrefix, 796), (matches[1].Method, matches[1].Connector!.Id));
    }

    [Fact]
    public void A_banks_business_connector_is_paired_with_its_main_one()
    {
        // #1379: Inter (215) and Inter Empresas (225) are two connectors of one bank; Itaú Empresas the same for Itaú.
        var inter = Institution("Inter", "https://inter.co/", "077", "00416968");
        var itau = Institution("Itaú Unibanco", "https://www.itau.com.br/", "341", "60701190");
        var citi = Institution("Citibank", "https://www.citibank.com.br/", "745", "33479023");
        var connectors = new[]
        {
            new PluggyConnector(215, "Inter", null, "https://cdn.example/215.svg", "BR", "regulado", false),
            new PluggyConnector(225, "Inter Empresas", null, "https://cdn.example/225.svg", "BR", "business", false),
            new PluggyConnector(601, "Itaú", null, "https://cdn.example/201.svg", "BR", "regulado", false),
            new PluggyConnector(618, "Itaú Empresas", null, "https://cdn.example/618.svg", "BR", "business", false),
            // Shorter names of other business products of the bank lose to the one that says Empresas.
            new PluggyConnector(794, "Itaú BBA", null, "https://cdn.example/794.svg", "BR", "business", false),
            new PluggyConnector(795, "Itaú Emps", null, "https://cdn.example/795.svg", "BR", "business", false),
            new PluggyConnector(619, "Interbank", null, "https://cdn.example/619.svg", "BR", "business", false),
            new PluggyConnector(477, "Citi Empresas", null, "https://cdn.example/477.svg", "BR", "business", false),
        };
        var aliases = new Dictionary<string, int> { ["341"] = 601 };
        var matches = ConnectorMatcher.Match([inter, itau, citi], connectors, aliases);

        var variants = ConnectorMatcher.MatchBusiness(matches, connectors);

        Assert.Equal(
            [("077", 215, 225), ("341", 601, 618)],
            variants.Select(v => (v.Institution.Compe, v.Main.Id, v.Business.Id)));
        // Citi's only connector is a business one: it is the institution's mark, with no separate variant;
        // "Interbank" is another name, not Inter's business connector.
    }

    [Fact]
    public void A_personal_connector_wins_over_a_business_one_of_the_same_name()
    {
        var inter = Institution("Inter", "https://inter.co/");
        var connectors = new[]
        {
            new PluggyConnector(825, "Inter", null, "https://cdn.example/a.svg", "BR", "business", false),
            new PluggyConnector(823, "Inter", null, "https://cdn.example/b.svg", "BR", "regulado", false),
        };

        Assert.Equal(823, Assert.Single(ConnectorMatcher.Match([inter], connectors, new Dictionary<string, int>())).Connector!.Id);
    }

    [Fact]
    public void An_alias_pins_the_connector_and_a_stale_alias_is_reported_unmatched()
    {
        var caixa = Institution("Caixa", "https://www.caixa.gov.br/", "104");
        var connectors = new[]
        {
            Connector(783, "Caixa Tem", null),
            Connector(619, "Caixa Econômica Federal", null),
        };

        var pinned = Assert.Single(ConnectorMatcher.Match([caixa], connectors, new Dictionary<string, int> { ["104"] = 619 }));
        var withoutAlias = Assert.Single(ConnectorMatcher.Match([caixa], connectors, new Dictionary<string, int>()));
        var stale = Assert.Single(ConnectorMatcher.Match([caixa], connectors, new Dictionary<string, int> { ["104"] = 1 }));

        Assert.Equal((MatchMethod.Alias, 619), (pinned.Method, pinned.Connector!.Id));
        Assert.Equal(783, withoutAlias.Connector!.Id); // what the name rule alone would pick: why the alias exists
        Assert.Equal(MatchMethod.None, stale.Method);
    }

    [Fact]
    public void Every_alias_names_a_listed_institution()
    {
        Assert.All(ConnectorMatcher.Aliases.Keys, compe => Assert.NotNull(BrazilianInstitutions.Find(compe)));
    }

    [Fact]
    public void Every_listed_institution_can_be_matched_against_its_own_website()
    {
        var connectors = BrazilianInstitutions.All
            .Select((i, n) => Connector(n + 1, i.Name, i.Website.AbsoluteUri.TrimEnd('/')))
            .ToList();

        var matches = ConnectorMatcher.Match(BrazilianInstitutions.All, connectors);

        Assert.All(matches, m => Assert.Equal(MatchMethod.Website, m.Method));
    }

    [Fact]
    public void Sniffing_trusts_the_bytes()
    {
        Assert.Equal(("svg", "image/svg+xml"), MarkImages.Sniff("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
        Assert.Equal(("svg", "image/svg+xml"), MarkImages.Sniff("<?xml version=\"1.0\"?>\n<svg/>"u8));
        Assert.Equal(("png", "image/png"), MarkImages.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Null(MarkImages.Sniff("<html><svg/></html>"u8));
        Assert.Null(MarkImages.Sniff([0xFF, 0xD8, 0xFF]));
    }

    [Theory]
    [InlineData("<svg><script>alert(1)</script></svg>", "contains a script")]
    [InlineData("<svg onload=\"x()\"/>", "contains an event handler")]
    [InlineData("<svg><foreignObject/></svg>", "contains a foreignObject")]
    [InlineData("<svg><image href=\"https://evil.example/x.png\"/></svg>", "references an external resource")]
    [InlineData("<svg><rect style=\"fill:url(https://evil.example/x)\"/></svg>", "references an external resource")]
    [InlineData("<svg><a href=\"javascript:alert(1)\"/></svg>", "contains a script")]
    public void An_unsafe_svg_is_rejected(string svg, string reason) => Assert.Equal(reason, MarkImages.UnsafeSvgReason(svg));

    [Fact]
    public void A_plain_svg_is_accepted_and_oversize_or_foreign_images_are_not()
    {
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"><defs><linearGradient id=\"g\"/></defs><rect fill=\"url(#g)\"/><image href=\"data:image/png;base64,AAAA\"/></svg>";

        Assert.Null(MarkImages.Reject(Encoding.UTF8.GetBytes(svg)));
        Assert.NotNull(MarkImages.Reject([]));
        Assert.NotNull(MarkImages.Reject(new byte[MarkImages.MaxBytes + 1]));
        Assert.NotNull(MarkImages.Reject("GIF89a"u8.ToArray()));
    }
}
