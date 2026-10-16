using CrxMoney.Integrations.Abstractions.Sync;

namespace CrxMoney.Integrations.Abstractions.Plugins;

/// <summary>
/// The contract every provider plugin implements. The host selects a plugin by
/// <see cref="ProviderId"/> and calls <see cref="SyncAsync"/> for one connection at a time.
/// </summary>
public interface IIntegrationPlugin
{
    /// <summary>
    /// Stable identifier of the provider, for example <c>binance</c>. It must never change once
    /// released, because the host stores it with every connection.
    /// </summary>
    string ProviderId { get; }

    /// <summary>Human-readable provider name shown to users.</summary>
    string DisplayName { get; }

    /// <summary>What the plugin imports. Descriptive metadata; hosts may use it to filter plugins.</summary>
    IntegrationCapability Capabilities { get; }

    /// <summary>Suggested scheduling defaults. Override to change the cadence or the backfill anchor.</summary>
    IntegrationPluginDefaults Defaults => IntegrationPluginDefaults.Default;

    /// <summary>
    /// Runs one incremental sync for the connection described by <paramref name="request"/>.
    /// </summary>
    /// <param name="request">Connection, window, checkpoints and resolved credentials for this run.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>
    /// The outcome of the run. Return <see cref="IntegrationSyncResult.Failure"/> for expected provider
    /// failures (authentication, rate limits, upstream errors) rather than throwing.
    /// </returns>
    Task<IntegrationSyncResult> SyncAsync(IntegrationSyncRequest request, CancellationToken cancellationToken = default);
}
