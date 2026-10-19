using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests;

/// <summary>The account operator's website from the country package's institution list, on rows and events.</summary>
public sealed class InstitutionWebsiteTests
{
    private static readonly JsonElement Raw = JsonDocument.Parse("""{"id":"raw"}""").RootElement;

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
