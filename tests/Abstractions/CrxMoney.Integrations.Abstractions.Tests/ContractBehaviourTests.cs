using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Secrets;
using CrxMoney.Integrations.Abstractions.Sync;

namespace CrxMoney.Integrations.Abstractions.Tests;

/// <summary>
/// Behaviour that hosts and plugins rely on beyond the shape of the types: factory defaults and
/// default interface members. Changing any of these is a breaking change.
/// </summary>
public sealed class ContractBehaviourTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Completed = Started.AddSeconds(30);

    [Fact]
    public void Success_defaults_every_list_to_empty_and_reports_no_error()
    {
        var result = IntegrationSyncResult.Success(Started, Completed, balancesImported: 1, tradesImported: 2, transfersImported: 3);

        Assert.Equal(IntegrationSyncStatus.Succeeded, result.Status);
        Assert.Equal((1, 2, 3), (result.BalancesImported, result.TradesImported, result.TransfersImported));
        Assert.Empty(result.NonZeroAssetCodes);
        Assert.Empty(result.Checkpoints);
        Assert.Empty(result.ImportEvents);
        Assert.Equal(IntegrationErrorKind.None, result.ErrorKind);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Success_keeps_supplied_checkpoints_and_events()
    {
        IntegrationCheckpoint[] checkpoints = [new("statement", "2026-01-02")];
        IntegrationImportEvent[] events =
        [
            new("ext-1", "example:1:ext-1:credit", "transfer", "credit", Started, "account-1",
                "BRL", null, 10.5m, null, null, null),
        ];

        var result = IntegrationSyncResult.Success(Started, Completed, 0, 0, 1, checkpoints: checkpoints, importEvents: events);

        Assert.Same(checkpoints, result.Checkpoints);
        Assert.Same(events, result.ImportEvents);
    }

    [Fact]
    public void Failure_reports_the_error_with_zero_counts()
    {
        var result = IntegrationSyncResult.Failure(Started, Completed, IntegrationErrorKind.RateLimited, "Try again later.");

        Assert.Equal(IntegrationSyncStatus.Failed, result.Status);
        Assert.Equal(IntegrationErrorKind.RateLimited, result.ErrorKind);
        Assert.Equal("Try again later.", result.ErrorMessage);
        Assert.Equal((0, 0, 0), (result.BalancesImported, result.TradesImported, result.TransfersImported));
        Assert.Empty(result.Checkpoints);
        Assert.Empty(result.ImportEvents);
    }

    [Fact]
    public void Plugins_inherit_the_default_schedule()
    {
        IIntegrationPlugin plugin = new MinimalPlugin();

        Assert.Equal(IntegrationPluginDefaults.Default, plugin.Defaults);
        Assert.Equal(15, plugin.Defaults.SyncEveryMinutes);
        Assert.Equal(new DateOnly(2025, 1, 1), plugin.Defaults.SuggestedBackfillFromDate);
    }

    [Fact]
    public async Task Async_secret_resolution_falls_back_to_the_synchronous_resolver()
    {
        IIntegrationSecretResolver resolver = new DictionaryResolver();

        Assert.Equal("value", await resolver.ResolveAsync("known", Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Null(await resolver.ResolveAsync("unknown", Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Empty_manifest_declares_no_schemas()
    {
        Assert.Empty(IntegrationImportSchemaManifest.Empty.Schemas);
    }

    [Fact]
    public void Enum_values_are_stable()
    {
        // Hosts persist these as integers; renumbering would corrupt stored data.
        Assert.Equal(
            [0, 1, 2, 4, 8],
            Enum.GetValues<IntegrationCapability>().Select(value => (int)value));
        Assert.Equal(
            Enumerable.Range(0, 9),
            Enum.GetValues<IntegrationErrorKind>().Select(value => (int)value));
        Assert.Equal(
            [0, 1, 2],
            Enum.GetValues<IntegrationSyncStatus>().Select(value => (int)value));
        Assert.Equal(
            [0, 1],
            Enum.GetValues<Models.TransferKind>().Select(value => (int)value));
    }

    private sealed class MinimalPlugin : IIntegrationPlugin
    {
        public string ProviderId => "minimal";

        public string DisplayName => "Minimal";

        public IntegrationCapability Capabilities => IntegrationCapability.None;

        public Task<IntegrationSyncResult> SyncAsync(IntegrationSyncRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(IntegrationSyncResult.Success(Started, Completed, 0, 0, 0));
    }

    private sealed class DictionaryResolver : IIntegrationSecretResolver
    {
        public string? Resolve(string locator) => locator == "known" ? "value" : null;
    }
}
