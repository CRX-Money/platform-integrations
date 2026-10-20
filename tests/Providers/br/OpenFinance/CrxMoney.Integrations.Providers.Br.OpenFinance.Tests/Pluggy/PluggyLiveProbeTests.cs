using System.Text.Json.Nodes;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.OpenFinance;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;
using Microsoft.Extensions.DependencyInjection;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Tests.Pluggy;

/// <summary>
/// Opt-in check against a real Pluggy item, skipped unless <c>PLUGGY_CLIENT_ID</c>, <c>PLUGGY_CLIENT_SECRET</c>
/// and <c>PLUGGY_ITEM_ID</c> are set (CI never sets them). It prints only aggregates (counts, key shapes,
/// the deposit, card and position balance checks, position types, movement operations, pair counts), never names,
/// documents, amounts or descriptions.
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

        using var services = BuildServices();
        using var scope = services.CreateScope();
        var plugin = scope.ServiceProvider.GetServices<IIntegrationPlugin>().OfType<OpenFinanceBrasilIntegrationPlugin>().Single();
        var request = new IntegrationSyncRequest(1, OpenFinanceBrasilIntegrationPlugin.Id, null, null, ApiKey: clientId, ApiSecret: clientSecret, Passphrase: itemId)
        {
            AdapterId = PluggyAdapter.Name,
        };
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

        // On a full read, an account's events still add up to the balance reported: credits minus debits for a deposit
        // account, debits minus credits (what is owed) for a card's settled balance, less the bill payments still pending
        // on the card (the host books them from the paying account's debit). A position's events usually do not:
        // fixed income accrues without movements, so its check is reported, not asserted.
        var diagnostics = JsonNode.Parse(result.ProviderDiagnosticsJson!)!;
        var rows = diagnostics["accounts"]!.AsArray().Select(a => a!).ToList();
        decimal Net(JsonNode row) => result.ImportEvents
            .Where(e => e.ExternalAccountId == row["externalAccountId"]!.GetValue<string>())
            .Sum(e => e.Type == "deposit" ? e.Amount!.Value : -e.Amount!.Value);
        string Kind(JsonNode row) => row["kind"]!.GetValue<string>();
        bool Reconciles(JsonNode row) =>
            (Kind(row) == "creditCard" ? -Net(row) - row["pendingBillPayments"]!.GetValue<decimal>() : Net(row)) == row["balance"]!.GetValue<decimal>();
        var reconciles = rows.Where(r => Kind(r) == "deposit" && r["balance"] is not null).Select(Reconciles).ToList();
        var cardReconciles = rows.Where(r => Kind(r) == "creditCard" && r["balance"] is not null).Select(Reconciles).ToList();
        var investmentRows = rows.Where(r => Kind(r) == "investment").ToList();

        // Every investment event belongs to a reported position.
        var positions = investmentRows.Select(r => r["externalAccountId"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.All(result.ImportEvents.Where(e => e.ExternalAccountId.Contains(":inv:", StringComparison.Ordinal)), e => Assert.Contains(e.ExternalAccountId, positions));

        var data = result.ImportEvents.Select(e => JsonNode.Parse(e.ProviderData!)!).ToList();
        var investmentData = data.Where(d => d["accountKind"]?.GetValue<string>() == "investment").ToList();
        var accountShapes = result.ImportEvents.Select(e => System.Text.RegularExpressions.Regex.Replace(e.ExternalAccountId, "[0-9A-Z]", "9")).Distinct();
        var message = string.Join(" | ",
            $"events={result.ImportEvents.Count}",
            $"accounts=[{string.Join(", ", accountShapes)}]",
            $"deposit={result.ImportEvents.Count(e => e.Type == "deposit")}",
            $"withdrawal={result.ImportEvents.Count(e => e.Type == "withdrawal")}",
            $"withTaxId={data.Count(d => d["counterparty"]?["taxId"] is not null)}",
            $"holderSide={data.Count(d => d["counterpartyIsHolder"] is not null)}",
            $"hints=[{string.Join(", ", data.GroupBy(d => $"{d["accountKind"]}:{d["transferHint"]?.GetValue<string>() ?? "none"}").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}"))}]",
            $"windowed={windowed.ImportEvents.Count} sentFrom={sentFrom:yyyy-MM-dd}",
            $"depositBalanceMatchesFullNet=[{string.Join(", ", reconciles)}]",
            $"cardSettledMatchesFullNet=[{string.Join(", ", cardReconciles)}]",
            $"cards=[{string.Join(", ", rows.Where(r => Kind(r) == "creditCard").Select(r => $"pendingLines:{r["pendingLines"]?.GetValue<int>()} limit:{r["creditLimit"] is not null} due:{r["billDueDate"] is not null} pendingBillPaymentLines:{r["pendingBillPaymentLines"]?.GetValue<int>()}"))}]",
            $"positions={investmentRows.Count} [{string.Join(", ", investmentRows.GroupBy(r => $"{r["type"]}/{r["subtype"]}/{r["externalAccountIdBasis"]}/{r["status"]}").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}"))}]",
            $"positionBalanceMatchesFullNet=[{string.Join(", ", investmentRows.Select(Reconciles))}]",
            $"movements=[{string.Join(", ", investmentData.GroupBy(d => $"{d["operation"]}{(d["component"] is { } c ? "#" + c : string.Empty)}").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}"))}]",
            $"pairs={diagnostics["transferPairs"]?.ToJsonString()}",
            $"diagnostics={Redact(result.ProviderDiagnosticsJson)}",
            $"windowedDiagnostics={Redact(windowed.ProviderDiagnosticsJson)}");
        TestContext.Current.SendDiagnosticMessage(message);
        TestContext.Current.AddAttachment("summary", message);
        if (Environment.GetEnvironmentVariable("PLUGGY_PROBE_OUT") is { Length: > 0 } outPath)
            await File.WriteAllTextAsync(outPath, message, ct);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddOpenFinanceBrasil().AddPluggy();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Dates, account ids, names, issuers, balances, limits and every other amount are replaced by markers;
    /// counts, kinds, types, statuses and identity bases stay.
    /// </summary>
    private static string Redact(string? diagnostics) => diagnostics is null
        ? "none"
        : System.Text.RegularExpressions.Regex.Replace(diagnostics,
            "\"(lastUpdatedAtUtc|credentialExpiresAtUtc|balanceAtUtc|externalAccountId|balance|pendingAmount|balanceWithPending|pendingBillPayments|creditLimit"
            + "|availableCreditLimit|minimumPayment|billCloseDate|billDueDate|name|issuer|grossAmount|amountInvested|profit|incomeTax"
            + "|financialTax|issueDate|dueDate|transactionsDateFrom|cardTransactionsDateFrom|sentFrom)\":(\"[^\"]*\"|-?[0-9.]+)", "\"$1\":\"<set>\"");
}
