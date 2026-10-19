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
        Assert.Equal(entries.Count, entries.Select(e => e["website"]!.GetValue<string>()).Distinct().Count());
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
