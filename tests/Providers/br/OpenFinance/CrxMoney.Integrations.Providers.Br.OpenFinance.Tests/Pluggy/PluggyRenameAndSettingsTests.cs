using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy.Support;
using Microsoft.Extensions.DependencyInjection;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy;

/// <summary>
/// The move from <c>CrxMoney.Integrations.Providers.Br.Pluggy</c> to the Open Finance Brasil plugin changes no key,
/// id or provider data, and the Pluggy adapter implements the connection settings contract.
/// </summary>
public sealed class PluggyRenameAndSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <c>golden-events-82b8c61.json</c> was written by the code of commit 82b8c61, before the move: every event's
    /// ExternalId, external account id, idempotency key, type, amount, date, schema and the SHA-256 of its provider
    /// data, for a full and two windowed reads. The moved code must reproduce it byte for byte.
    /// </summary>
    [Theory]
    [InlineData("full", null)]
    [InlineData("window-2026-09-27", "2026-09-27T12:00:00Z")]
    [InlineData("window-2026-10-12", "2026-10-12T12:00:00Z")]
    public async Task Keys_account_ids_and_provider_data_are_byte_identical_to_the_pre_rename_code(string read, string? windowStart)
    {
        var golden = JsonNode.Parse(Fixtures.Text("golden-events-82b8c61.json"))!["reads"]![read]!.AsArray();
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());
        DateTimeOffset? start = windowStart is null ? null : DateTimeOffset.Parse(windowStart, CultureInfo.InvariantCulture);

        var result = await host.Plugin.SyncAsync(PluggyTestHost.Request(windowStart: start), Ct);

        var actual = result.ImportEvents.OrderBy(e => e.ExternalId, StringComparer.Ordinal).ToList();
        Assert.Equal(golden.Count, actual.Count);
        foreach (var (expected, e) in golden.Zip(actual))
        {
            Assert.Equal(expected!["externalId"]!.GetValue<string>(), e.ExternalId);
            Assert.Equal(expected["externalAccountId"]!.GetValue<string>(), e.ExternalAccountId);
            Assert.Equal(expected["idempotencyKey"]!.GetValue<string>(), e.IdempotencyKey);
            Assert.Equal(expected["type"]!.GetValue<string>(), e.Type);
            Assert.Equal(expected["amount"]!.GetValue<decimal>(), e.Amount);
            Assert.Equal(expected["occurredAtUtc"]!.GetValue<string>(), e.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
            Assert.Equal(expected["schemaId"]!.GetValue<string>(), e.ProviderDataSchemaId);
            Assert.Equal(expected["providerDataSha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(e.ProviderData!))));
        }
    }

    [Fact]
    public void The_host_registers_the_pluggy_adapter_through_the_open_finance_plugin()
    {
        var services = new ServiceCollection();
        services.AddOpenFinanceBrasil().AddPluggy();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        var plugin = Assert.Single(scope.ServiceProvider.GetServices<IIntegrationPlugin>());

        Assert.Equal("Pluggy", plugin.ProviderId); // unchanged: the host stores it with every connection
        Assert.IsType<IIntegrationConnectionSettingsProvider>(plugin, exactMatch: false);
        Assert.IsType<IIntegrationConnectionTester>(plugin, exactMatch: false);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("en-US", "en")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt", "pt-BR")]
    [InlineData("PT-pt", "pt-BR")]
    [InlineData("es", "en")]
    public void The_settings_declare_the_pluggy_fields_in_the_culture_asked_for(string? culture, string expectedCulture)
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var manifest = host.Plugin.GetConnectionSettings(culture);

        Assert.Equal(("Pluggy", expectedCulture), (manifest.ProviderId, manifest.Culture));
        var adapter = Assert.Single(manifest.Adapters);
        Assert.Equal(("pluggy", "Pluggy", (short)1), (adapter.AdapterId, adapter.DisplayName, adapter.SchemaVersion));
        Assert.Equal(
            [("clientId", IntegrationCredentialSlot.ApiKey, false, true), ("clientSecret", IntegrationCredentialSlot.ApiSecret, true, true), ("itemId", IntegrationCredentialSlot.Passphrase, false, true)],
            adapter.Fields.Select(f => (f.Name, f.Slot, f.IsSecret, f.IsRequired)));
        var walkthrough = adapter.Walkthrough!;
        Assert.Equal(5, walkthrough.Steps.Count);
        Assert.Equal(["https://meu.pluggy.ai/", "https://dashboard.pluggy.ai/"], walkthrough.Steps.Where(s => s.Link is not null).Select(s => s.Link!.AbsoluteUri));
        Assert.False(string.IsNullOrWhiteSpace(walkthrough.Note));
        Assert.Contains(expectedCulture == "pt-BR" ? "Conecte" : "Connect", walkthrough.Steps[0].Text, StringComparison.Ordinal);
    }

    /// <summary>The schema and the field list say the same thing, so a host may read either.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void The_json_schema_matches_the_declared_fields(string culture)
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());
        var adapter = host.Plugin.GetConnectionSettings(culture).Adapters.Single();

        var schema = JsonNode.Parse(adapter.JsonSchema)!;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var properties = schema["properties"]!.AsObject();
        Assert.Equal(adapter.Fields.Select(f => f.Name), properties.Select(p => p.Key));
        Assert.Equal(adapter.Fields.Where(f => f.IsRequired).Select(f => f.Name), schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.All(adapter.Fields, f =>
        {
            var property = properties[f.Name]!;
            Assert.Equal("string", property["type"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(property["title"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(property["description"]!.GetValue<string>()));
            Assert.Equal(f.IsSecret, property["writeOnly"]?.GetValue<bool>() ?? false);
        });
        Assert.Equal(adapter.Fields.Count, adapter.Fields.Select(f => f.Slot).Distinct().Count());
        Assert.Equal("uuid", properties["clientId"]!["format"]!.GetValue<string>());
        Assert.Equal("uuid", properties["itemId"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_connection_test_reports_refresh_consent_and_the_account_operator()
    {
        var server = FakePluggyServer.WithFixtures();
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.TestConnectionAsync("pluggy", PluggyTestHost.Request(connectionId: 0), cancellationToken: Ct);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(IntegrationErrorKind.None, result.ErrorKind);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero), result.LastRefreshedAtUtc);
        Assert.Equal(new DateTimeOffset(2027, 10, 13, 9, 0, 0, TimeSpan.Zero), result.CredentialExpiresAtUtc);
        Assert.False(result.IsSandbox);
        var accountOperator = Assert.Single(result.AccountOperators);
        Assert.Equal(("Inter", "https://inter.co/"), (accountOperator.Name, accountOperator.Website!.AbsoluteUri));
        Assert.Equal("Connection works. Data last refreshed 2026-10-13 09:00 UTC. Consent valid until 2027-10-13. Accounts at Inter.", result.Summary);
        Assert.DoesNotContain("Pluggy", result.Summary, StringComparison.Ordinal); // provider-neutral
        Assert.Empty(server.RequestsTo("/v2/transactions"));
    }

    [Fact]
    public async Task A_connection_test_answers_in_portuguese()
    {
        using var host = new PluggyTestHost(FakePluggyServer.WithFixtures());

        var result = await host.Plugin.TestConnectionAsync("pluggy", PluggyTestHost.Request(connectionId: 0), "pt-BR", Ct);

        Assert.Equal("Conexão funcionando. Dados atualizados em 13/10/2026 09:00 UTC. Consentimento válido até 13/10/2027. Contas em Inter.", result.Summary);
    }

    [Fact]
    public async Task Accounts_that_cannot_be_listed_leave_the_test_successful_without_operators()
    {
        var server = FakePluggyServer.WithFixtures().On("/accounts", (_, _) =>
            FakePluggyServer.Json("""{"message":"Product not enabled","code":403}""", HttpStatusCode.Forbidden));
        using var host = new PluggyTestHost(server);

        var result = await host.Plugin.TestConnectionAsync("pluggy", PluggyTestHost.Request(connectionId: 0), cancellationToken: Ct);

        Assert.True(result.Succeeded);
        Assert.Empty(result.AccountOperators);
    }

    [Fact]
    public async Task Rejected_credentials_and_unknown_adapters_fail_with_their_kind()
    {
        var server = FakePluggyServer.WithFixtures().On("/auth", (_, _) =>
            FakePluggyServer.Json("""{"message":"Invalid credentials","code":401}""", HttpStatusCode.Unauthorized));
        using var host = new PluggyTestHost(server);

        var rejected = await host.Plugin.TestConnectionAsync("pluggy", PluggyTestHost.Request(connectionId: 0), cancellationToken: Ct);
        var unknown = await host.Plugin.TestConnectionAsync("belvo", PluggyTestHost.Request(connectionId: 0), "pt-BR", Ct);
        var noItem = await host.Plugin.TestConnectionAsync("pluggy", PluggyTestHost.Request(connectionId: 0, itemId: null), cancellationToken: Ct);

        Assert.Equal((false, IntegrationErrorKind.Authentication), (rejected.Succeeded, rejected.ErrorKind));
        Assert.DoesNotContain("synthetic-client-secret", rejected.Summary, StringComparison.Ordinal);
        Assert.Equal((false, IntegrationErrorKind.Configuration), (unknown.Succeeded, unknown.ErrorKind));
        Assert.Equal("Este plugin não tem o adaptador 'belvo'.", unknown.Summary);
        Assert.Equal(IntegrationErrorKind.Configuration, noItem.ErrorKind);
    }
}
