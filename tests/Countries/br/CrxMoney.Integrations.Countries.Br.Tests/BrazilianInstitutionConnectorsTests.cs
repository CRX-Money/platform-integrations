using System.Text.Json.Nodes;

namespace CrxMoney.Integrations.Countries.Br.Tests;

/// <summary>
/// The connectors per institution (#1379): every main connector of the marks manifest, and the business (PJ) variants
/// its <c>connectors</c> section lists, each with its own mark when the capture found a usable icon.
/// </summary>
public sealed class BrazilianInstitutionConnectorsTests
{
    private static JsonNode Manifest() => JsonNode.Parse(BrazilianInstitutionMarks.ReadManifest())!;

    [Fact]
    public void Inters_main_connector_is_the_one_whose_icon_is_its_mark()
    {
        var inter = BrazilianInstitutionConnectors.For("077", isBusiness: false);

        Assert.NotNull(inter);
        Assert.Equal(("215", "Inter", false), (inter.Id, inter.Name, inter.IsBusiness));
        Assert.Same(inter, BrazilianInstitutionConnectors.Find("215"));
        Assert.Equal("00416968", inter.Institution.Ispb);
    }

    [Fact]
    public void Every_listed_business_connector_belongs_to_a_listed_institution_and_opens_its_mark_when_it_has_one()
    {
        var entries = Manifest()["connectors"]?.AsArray().Select(e => e!).ToList() ?? [];
        Assert.All(entries, entry =>
        {
            var id = entry["connectorId"]!.ToJsonString().Trim('"');
            var connector = BrazilianInstitutionConnectors.Find(id);
            Assert.NotNull(connector);
            Assert.True(connector.IsBusiness);
            Assert.Equal(entry["compe"]!.GetValue<string>(), connector.Institution.Compe);
            Assert.Same(connector, BrazilianInstitutionConnectors.For(connector.Institution.Compe, isBusiness: true));
            if (entry["file"] is not null)
            {
                Assert.True(BrazilianInstitutionMarks.TryOpenConnector(id, out var content, out var mediaType));
                using (content)
                {
                    Assert.Equal(entry["mediaType"]!.GetValue<string>(), mediaType);
                    // The bytes as captured: line endings are never rewritten (.gitattributes).
                    using var buffer = new MemoryStream();
                    content.CopyTo(buffer);
                    Assert.Equal(entry["sha256"]!.GetValue<string>(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.ToArray())));
                }
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(entry["reason"]?.GetValue<string>()));
            }
        });
    }

    [Fact]
    public void Citis_only_connector_is_named_for_companies_and_serves_both_citi_institutions()
    {
        foreach (var compe in new[] { "477", "745" })
        {
            var citi = BrazilianInstitutionConnectors.For(compe, isBusiness: true);
            Assert.NotNull(citi);
            Assert.Equal(("710", "Citi Empresas", compe), (citi.Id, citi.Name, citi.Institution.Compe));
            Assert.Null(BrazilianInstitutionConnectors.For(compe, isBusiness: false));
        }
    }

    [Theory]
    [InlineData("Inter Empresas", true)]
    [InlineData("PicPay Negócios", true)]
    [InlineData("Itaú PJ", true)]
    [InlineData("Itaú BBA", false)]
    [InlineData("Inter", false)]
    [InlineData("Empresário", false)]
    public void A_name_says_a_connector_serves_companies(string name, bool expected) =>
        Assert.Equal(expected, BrazilianInstitutionConnectors.IsBusinessName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("999999")]
    public void An_unknown_connector_has_no_mark(string? id)
    {
        Assert.False(BrazilianInstitutionMarks.TryOpenConnector(id, out _, out _));
        Assert.Null(BrazilianInstitutionConnectors.Find(id));
    }
}
