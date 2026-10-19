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

        // A windowed read (Pluggy's dateFrom) sends exactly what the full read sends from the same date, with the same keys.
        var windowStart = DateTimeOffset.UtcNow.AddDays(-30);
        var windowed = await plugin.SyncAsync(request with { WindowStartUtc = windowStart }, ct);
        Assert.True(windowed.Status == IntegrationSyncStatus.Succeeded, $"Windowed sync failed: {windowed.ErrorKind} {windowed.ErrorMessage}");
        var sentFrom = DateOnly.Parse(JsonNode.Parse(windowed.ProviderDiagnosticsJson!)!["sentFrom"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            result.ImportEvents.Where(e => DateOnly.FromDateTime(e.OccurredAtUtc.ToOffset(TimeSpan.FromHours(-3)).DateTime) >= sentFrom).Select(e => e.IdempotencyKey).Order(),
            windowed.ImportEvents.Select(e => e.IdempotencyKey).Order());

        // On a full read, a deposit account's credits minus debits still equal the balance Pluggy reports.
        var reconciles = JsonNode.Parse(result.ProviderDiagnosticsJson!)!["accounts"]!.AsArray()
            .Where(a => a!["balance"] is not null)
            .Select(a => result.ImportEvents
                .Where(e => e.ExternalAccountId == a!["externalAccountId"]!.GetValue<string>())
                .Sum(e => e.Type == "deposit" ? e.Amount!.Value : -e.Amount!.Value) == a!["balance"]!.GetValue<decimal>())
            .ToList();

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
            $"windowed={windowed.ImportEvents.Count} sentFrom={sentFrom:yyyy-MM-dd}",
            $"depositBalanceMatchesFullNet=[{string.Join(", ", reconciles)}]",
            $"diagnostics={Redact(result.ProviderDiagnosticsJson)}",
            $"windowedDiagnostics={Redact(windowed.ProviderDiagnosticsJson)}");
        TestContext.Current.SendDiagnosticMessage(message);
        TestContext.Current.AddAttachment("summary", message);
        if (Environment.GetEnvironmentVariable("PLUGGY_PROBE_OUT") is { Length: > 0 } outPath)
            await File.WriteAllTextAsync(outPath, message, ct);
    }

    /// <summary>Dates, account ids and balances are replaced by markers; counts and statuses stay.</summary>
    private static string Redact(string? diagnostics) => diagnostics is null
        ? "none"
        : System.Text.RegularExpressions.Regex.Replace(diagnostics,
            "\"(lastUpdatedAtUtc|credentialExpiresAtUtc|balanceAtUtc|externalAccountId|balance)\":(\"[^\"]*\"|-?[0-9.]+)", "\"$1\":\"<set>\"");
}
