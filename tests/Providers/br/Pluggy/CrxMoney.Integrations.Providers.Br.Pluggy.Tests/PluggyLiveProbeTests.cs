using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace CrxMoney.Integrations.Providers.Br.Pluggy.Tests;

/// <summary>
/// Opt-in check against a real Pluggy item, skipped unless <c>PLUGGY_CLIENT_ID</c>, <c>PLUGGY_CLIENT_SECRET</c>
/// and <c>PLUGGY_ITEM_ID</c> are set (CI never sets them). It prints only aggregates (counts, key shapes,
/// the balance check), never names, documents, amounts or descriptions.
/// </summary>
[Trait("Category", "Live")]
public sealed class PluggyLiveProbeTests
{
    [Fact]
    public async Task Live_item_maps_and_reconciles()
    {
        var clientId = Environment.GetEnvironmentVariable("PLUGGY_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("PLUGGY_CLIENT_SECRET");
        var itemId = Environment.GetEnvironmentVariable("PLUGGY_ITEM_ID");
        Assert.SkipWhen(
            new[] { clientId, clientSecret, itemId }.Any(string.IsNullOrWhiteSpace),
            "Set PLUGGY_CLIENT_ID, PLUGGY_CLIENT_SECRET and PLUGGY_ITEM_ID to run against a real Pluggy item.");

        using var services = new ServiceCollection().AddPluggyIntegration().BuildServiceProvider();
        using var scope = services.CreateScope();
        var plugin = scope.ServiceProvider.GetServices<IIntegrationPlugin>().OfType<PluggyIntegrationPlugin>().Single();
        var request = new IntegrationSyncRequest(1, PluggyIntegrationPlugin.Id, null, null, ApiKey: clientId, ApiSecret: clientSecret, Passphrase: itemId);
        var ct = TestContext.Current.CancellationToken;

        var test = await plugin.SyncAsync(request with { ConnectionId = 0 }, ct);
        Assert.True(test.Status == IntegrationSyncStatus.Succeeded, $"Connection test failed: {test.ErrorKind} {test.ErrorMessage}");

        var result = await plugin.SyncAsync(request, ct);
        Assert.True(result.Status == IntegrationSyncStatus.Succeeded, $"Sync failed: {result.ErrorKind} {result.ErrorMessage}");

        Assert.Equal(result.ImportEvents.Count, result.ImportEvents.Select(e => e.IdempotencyKey).Distinct().Count());
        var again = await plugin.SyncAsync(request, ct);
        Assert.Equal(result.ImportEvents.Select(e => e.IdempotencyKey).Order(), again.ImportEvents.Select(e => e.IdempotencyKey).Order());

        var data = result.ImportEvents.Select(e => JsonNode.Parse(e.ProviderData!)!).ToList();
        var accountShapes = result.ImportEvents.Select(e => System.Text.RegularExpressions.Regex.Replace(e.ExternalAccountId, "[0-9]", "9")).Distinct();
        var message = string.Join(" | ",
            $"events={result.ImportEvents.Count}",
            $"accounts=[{string.Join(", ", accountShapes)}]",
            $"deposit={result.ImportEvents.Count(e => e.Type == "deposit")}",
            $"withdrawal={result.ImportEvents.Count(e => e.Type == "withdrawal")}",
            $"withTaxId={data.Count(d => d["counterparty"]?["taxId"] is not null)}",
            $"holderSide={data.Count(d => d["counterpartyIsHolder"] is not null)}",
            $"hints=[{string.Join(", ", data.GroupBy(d => d["transferHint"]?.GetValue<string>() ?? "none").Select(g => $"{g.Key}:{g.Count()}"))}]",
            $"diagnostics={Redact(result.ProviderDiagnosticsJson)}");
        TestContext.Current.SendDiagnosticMessage(message);
        TestContext.Current.AddAttachment("summary", message);
        if (Environment.GetEnvironmentVariable("PLUGGY_PROBE_OUT") is { Length: > 0 } outPath)
            await File.WriteAllTextAsync(outPath, message, ct);
    }

    private static string Redact(string? diagnostics) => diagnostics is null
        ? "none"
        : System.Text.RegularExpressions.Regex.Replace(diagnostics, "\"(lastUpdatedAtUtc|credentialExpiresAtUtc)\":\"[^\"]+\"", "\"$1\":\"<set>\"");
}
