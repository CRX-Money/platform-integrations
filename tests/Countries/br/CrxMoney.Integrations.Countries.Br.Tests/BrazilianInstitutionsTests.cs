using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br.OpenFinance;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrxMoney.Integrations.Countries.Br.Tests;

public sealed class BrazilianInstitutionsTests
{
    private static readonly JsonElement Raw = JsonDocument.Parse("""{"id":"raw"}""").RootElement;

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

    [Fact]
    public async Task Accounts_and_events_carry_the_website_only_when_the_code_is_listed()
    {
        var known = new OpenFinanceAccount("k", OpenFinanceAccountKind.Deposit, BrazilianBankAccount.Create("077", "0001", "12345678-9"), "077", null, "BRL", 10m);
        var unknown = new OpenFinanceAccount("u", OpenFinanceAccountKind.Deposit, BrazilianBankAccount.Create("999", "0001", "1234567-8"), "999", null, "BRL", 10m);
        var unknownCard = new OpenFinanceAccount("c", OpenFinanceAccountKind.CreditCard, null, "999", "1234", "BRL", null);
        var line = new OpenFinanceTransaction("t", new DateTimeOffset(2026, 9, 10, 15, 0, 0, TimeSpan.Zero), OpenFinanceDirection.Credit, 1m, "PIX", null, null, null, null, false, null, Raw);
        var snapshot = new OpenFinanceSnapshot(
        [
            new OpenFinanceAccountTransactions(known, [line]),
            new OpenFinanceAccountTransactions(unknown, [line with { SourceTransactionId = "t2" }]),
            new OpenFinanceAccountTransactions(unknownCard, []),
        ], null);

        var result = await new TestPlugin(snapshot).SyncAsync(new IntegrationSyncRequest(42, "Any", null, null), TestContext.Current.CancellationToken);

        var rows = JsonNode.Parse(result.ProviderDiagnosticsJson!)!["accounts"]!.AsArray();
        Assert.Equal("https://inter.co/", rows[0]!["institutionWebsite"]!.GetValue<string>());
        Assert.Null(rows[1]!["institutionWebsite"]);
        Assert.Null(rows[2]!["institutionWebsite"]);
        Assert.Equal("br:999:0001:12345678", rows[1]!["externalAccountId"]!.GetValue<string>()); // the code stays in the id
        var events = result.ImportEvents.ToDictionary(e => e.ExternalId, e => JsonNode.Parse(e.ProviderData!)!);
        Assert.Equal("https://inter.co/", events["t"]["institutionWebsite"]!.GetValue<string>());
        Assert.Null(events["t2"]["institutionWebsite"]);
    }

    private sealed class TestPlugin(OpenFinanceSnapshot snapshot) : OpenFinanceIntegrationPlugin(new Source(snapshot), TimeProvider.System, NullLogger.Instance)
    {
        public override string ProviderId => "Test";

        public override string DisplayName => "Test";

        private sealed class Source(OpenFinanceSnapshot snapshot) : IOpenFinanceSource
        {
            public string SourceName => "A";

            public Task<OpenFinanceConnectionCheck> CheckConnectionAsync(IntegrationSyncRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new OpenFinanceConnectionCheck("connected", null));

            public Task<OpenFinanceSnapshot> ReadAsync(IntegrationSyncRequest request, DateOnly? readFrom, CancellationToken cancellationToken) =>
                Task.FromResult(snapshot);
        }
    }
}
