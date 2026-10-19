namespace CrxMoney.Integrations.Abstractions.Sync;

/// <summary>
/// Everything a plugin needs for one sync run, prepared by the host.
/// </summary>
/// <param name="ConnectionId">Host identifier of the connection being synced.</param>
/// <param name="ProviderId">Provider the connection belongs to; matches <see cref="Plugins.IIntegrationPlugin.ProviderId"/>.</param>
/// <param name="WindowStartUtc">Optional start of a bounded backfill window.</param>
/// <param name="WindowEndUtc">Optional end of a bounded backfill window.</param>
/// <param name="CorrelationId">Optional identifier for tracing the run across logs.</param>
/// <param name="PreviousCheckpoint">Legacy single-cursor checkpoint; prefer <paramref name="PreviousCheckpoints"/>.</param>
/// <param name="TenantTrackedAssetCodes">Optional list of assets the owner tracks; plugins may narrow imports to them.</param>
/// <param name="ApiKey">Resolved API key or client id, when the provider uses one.</param>
/// <param name="ApiSecret">Resolved API secret or client secret, when the provider uses one.</param>
/// <param name="Passphrase">Resolved passphrase or additional credential, when the provider uses one.</param>
/// <param name="PreviousCheckpoints">Checkpoints returned by the previous successful run.</param>
public sealed record IntegrationSyncRequest(
    long ConnectionId,
    string ProviderId,
    DateTimeOffset? WindowStartUtc,
    DateTimeOffset? WindowEndUtc,
    string? CorrelationId = null,
    string? PreviousCheckpoint = null,
    IReadOnlyList<string>? TenantTrackedAssetCodes = null,
    string? ApiKey = null,
    string? ApiSecret = null,
    string? Passphrase = null,
    IReadOnlyList<IntegrationCheckpoint>? PreviousCheckpoints = null)
{
    /// <summary>
    /// The adapter of the plugin the connection uses (the aggregator reaching the provider, for example
    /// <c>Pluggy</c>), one of <see cref="Plugins.IntegrationConnectionAdapterSettings.AdapterId"/>.
    /// <c>null</c> when the connection names none; a plugin with a single adapter then uses that one.
    /// </summary>
    public string? AdapterId { get; init; }
}
