using System.Text.Json.Nodes;
using CrxMoney.Integrations.Countries.Br;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy.Api;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy.Support;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy;

/// <summary>
/// The connector an item reads through (#1379): Inter and Inter Empresas are two Pluggy connectors of one bank. An item
/// connected directly names its connector; an item through Meu Pluggy names MeuPluggy, so its accounts (bank and
/// holder) pick the connector from the country package. The plugin reports it on the connection test and in the sync
/// result's diagnostics as <c>connector</c>.
/// </summary>
public sealed class PluggyConnectorIdentityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_direct_item_of_Inter_Empresas_reports_its_business_connector()
    {
        var server = FakePluggyServer.WithFixtures().On($"/items/{FakePluggyServer.ItemId}", (_, _) => FakePluggyServer.Json(ItemWith(new JsonObject
        {
            ["id"] = 225,
            ["name"] = "Inter Empresas",
            ["type"] = "BUSINESS_BANK",
            ["imageUrl"] = "https://cdn.pluggy.ai/assets/connector-icons/225.svg",
            ["isOpenFinance"] = true,
            ["isSandbox"] = false,
        })));
        using var host = new PluggyTestHost(server);

        var test = await host.Plugin.TestConnectionAsync("Pluggy", PluggyTestHost.Request(connectionId: 0), cancellationToken: Ct);
        var sync = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        Assert.True(test.Succeeded, test.Summary);
        var connector = Assert.IsType<Abstractions.Plugins.IntegrationConnector>(test.Connector);
        Assert.Equal(("225", "Inter Empresas", true), (connector.Id, connector.Name, connector.IsBusiness));
        Assert.Equal("https://cdn.pluggy.ai/assets/connector-icons/225.svg", connector.IconUrl!.AbsoluteUri);
        Assert.Equal("https://inter.co/", connector.InstitutionWebsite!.AbsoluteUri);
        var reported = JsonNode.Parse(sync.ProviderDiagnosticsJson!)!["connector"]!;
        Assert.Equal(("225", "Inter Empresas", true), (reported["id"]!.GetValue<string>(), reported["name"]!.GetValue<string>(), reported["isBusiness"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task A_meu_pluggy_item_with_a_companys_accounts_reads_through_the_banks_business_connector()
    {
        // The fixture item names the MeuPluggy connector; its bank account is Inter's (077) and belongs to a CNPJ.
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var test = await host.Plugin.TestConnectionAsync("Pluggy", PluggyTestHost.Request(connectionId: 0), cancellationToken: Ct);

        var connector = test.Connector!;
        Assert.True(connector.IsBusiness);
        Assert.Equal("https://inter.co/", connector.InstitutionWebsite!.AbsoluteUri);
        // The package's business connector of Inter when the list holds one, else the bank's own name.
        var listed = BrazilianInstitutionConnectors.For("077", isBusiness: true);
        Assert.Equal((listed?.Id, listed?.Name ?? "Inter"), (connector.Id, connector.Name));
        Assert.DoesNotContain("MeuPluggy", connector.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_meu_pluggy_item_with_a_persons_accounts_reads_through_the_banks_main_connector()
    {
        var accounts = JsonNode.Parse(Fixtures.Text("accounts.json"))!;
        foreach (var account in accounts["results"]!.AsArray())
        {
            if (account!["taxNumber"] is not null)
                account["taxNumber"] = SyntheticTaxIds.Cpf(3);
        }

        var server = FakePluggyServer.WithFixtures().On("/accounts", (_, _) => FakePluggyServer.Json(accounts.ToJsonString()));
        using var host = new PluggyTestHost(server);

        var sync = await host.Plugin.SyncAsync(PluggyTestHost.Request(), Ct);

        var reported = JsonNode.Parse(sync.ProviderDiagnosticsJson!)!["connector"]!;
        Assert.False(reported["isBusiness"]!.GetValue<bool>());
        // Inter's main connector, the one whose icon is the institution's mark.
        Assert.Equal(("215", "Inter"), (reported["id"]!.GetValue<string>(), reported["name"]!.GetValue<string>()));
    }

    [Fact]
    public void The_meu_pluggy_connector_is_never_taken_for_a_bank()
    {
        var connector = PluggyOpenFinanceSource.ResolveConnector(
            new PluggyConnector(200, "MeuPluggy", true, false, "PERSONAL_BANK"), compe: null, holder: null);

        Assert.Null(connector);
    }

    private static string ItemWith(JsonObject connector)
    {
        var item = JsonNode.Parse(Fixtures.Text("item.json"))!;
        item["connector"] = connector;
        return item.ToJsonString();
    }
}
