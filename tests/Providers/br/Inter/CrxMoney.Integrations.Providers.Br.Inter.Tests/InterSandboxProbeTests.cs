using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Sync;
using Microsoft.Extensions.DependencyInjection;
using CrxMoney.Integrations.Abstractions.Plugins;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests;

/// <summary>
/// Opt-in check against Inter's sandbox, skipped unless the <c>INTER_SANDBOX_*</c> variables point at a
/// sandbox integration's credentials (CI never sets them). It prints only the <em>key paths</em> found in
/// <c>detalhes</c> per <c>tipoTransacao</c>, never values, so the counterparty key table can be checked
/// against real responses without copying any response into the repository.
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class InterSandboxProbeTests
{
    [Fact]
    public async Task Sandbox_statement_maps_and_shows_its_detail_keys()
    {
        var clientId = Environment.GetEnvironmentVariable("INTER_SANDBOX_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("INTER_SANDBOX_CLIENT_SECRET");
        var certificatePath = Environment.GetEnvironmentVariable("INTER_SANDBOX_CERTIFICATE_PEM");
        var keyPath = Environment.GetEnvironmentVariable("INTER_SANDBOX_KEY_PEM");
        var account = Environment.GetEnvironmentVariable("INTER_SANDBOX_ACCOUNT");
        Assert.SkipWhen(
            new[] { clientId, clientSecret, certificatePath, keyPath, account }.Any(string.IsNullOrWhiteSpace),
            "Set INTER_SANDBOX_CLIENT_ID, INTER_SANDBOX_CLIENT_SECRET, INTER_SANDBOX_CERTIFICATE_PEM, INTER_SANDBOX_KEY_PEM and INTER_SANDBOX_ACCOUNT to run against the Inter sandbox.");

        var bundle = InterCredentialBundle.CreateFromPem(
            await File.ReadAllTextAsync(certificatePath!, TestContext.Current.CancellationToken),
            await File.ReadAllTextAsync(keyPath!, TestContext.Current.CancellationToken),
            Environment.GetEnvironmentVariable("INTER_SANDBOX_KEY_PASSWORD"),
            InterEnvironment.Sandbox,
            account!);

        using var services = new ServiceCollection().AddInterIntegration().BuildServiceProvider();
        using var scope = services.CreateScope();
        var plugin = scope.ServiceProvider.GetServices<IIntegrationPlugin>().OfType<InterIntegrationPlugin>().Single();
        var end = DateTimeOffset.UtcNow;
        var request = new IntegrationSyncRequest(1, InterIntegrationPlugin.Id, end.AddDays(-89), end, ApiKey: clientId, ApiSecret: clientSecret, Passphrase: bundle);

        var test = await plugin.SyncAsync(request with { ConnectionId = 0 }, TestContext.Current.CancellationToken);
        Assert.True(test.Status == IntegrationSyncStatus.Succeeded, $"Connection test failed: {test.ErrorKind} {test.ErrorMessage}");

        var result = await plugin.SyncAsync(request, TestContext.Current.CancellationToken);
        Assert.True(result.Status == IntegrationSyncStatus.Succeeded, $"Sync failed: {result.ErrorKind} {result.ErrorMessage}");

        foreach (var group in result.ImportEvents.GroupBy(e => JsonDocument.Parse(e.ProviderData!).RootElement.GetProperty("tipoTransacao").GetString()))
        {
            var keys = group
                .SelectMany(e => KeyPaths(JsonDocument.Parse(e.ProviderData!).RootElement.TryGetProperty("detalhes", out var d) ? d : default, ""))
                .Distinct()
                .Order();
            TestContext.Current.SendDiagnosticMessage($"{group.Key} ({group.Count()}): {string.Join(", ", keys)}");
        }
    }

    private static IEnumerable<string> KeyPaths(JsonElement element, string prefix)
    {
        if (element.ValueKind != JsonValueKind.Object)
            yield break;
        foreach (var property in element.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            yield return path;
            foreach (var nested in KeyPaths(property.Value, path))
                yield return nested;
        }
    }
}
