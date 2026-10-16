namespace CrxMoney.Integrations.Abstractions.Sync;

/// <summary>
/// The outcome of one sync run, returned by <see cref="Plugins.IIntegrationPlugin.SyncAsync"/>.
/// Prefer the <see cref="Success"/> and <see cref="Failure"/> factories over the constructor.
/// </summary>
/// <param name="Status">Overall outcome.</param>
/// <param name="StartedAtUtc">Instant the run started, in UTC.</param>
/// <param name="CompletedAtUtc">Instant the run finished, in UTC.</param>
/// <param name="BalancesImported">Number of balances imported.</param>
/// <param name="TradesImported">Number of trades imported.</param>
/// <param name="TransfersImported">Number of transfers imported.</param>
/// <param name="NonZeroAssetCodes">Assets observed with a non-zero balance during the run.</param>
/// <param name="Checkpoints">New resume points; the host persists them and passes them to the next run.</param>
/// <param name="ImportEvents">Canonical events to import.</param>
/// <param name="ErrorKind">Failure classification; <see cref="IntegrationErrorKind.None"/> on success.</param>
/// <param name="ErrorMessage">Failure detail safe to show to the connection owner. Never include credentials.</param>
/// <param name="ProviderDiagnosticsJson">Opaque diagnostics JSON the host stores with the run. Never include credentials or personal data.</param>
public sealed record IntegrationSyncResult(
    IntegrationSyncStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int BalancesImported,
    int TradesImported,
    int TransfersImported,
    IReadOnlyList<string> NonZeroAssetCodes,
    IReadOnlyList<IntegrationCheckpoint> Checkpoints,
    IReadOnlyList<IntegrationImportEvent> ImportEvents,
    IntegrationErrorKind ErrorKind = IntegrationErrorKind.None,
    string? ErrorMessage = null,
    string? ProviderDiagnosticsJson = null)
{
    /// <summary>Creates a <see cref="IntegrationSyncStatus.Succeeded"/> result; omitted lists default to empty.</summary>
    /// <param name="startedAtUtc">Instant the run started, in UTC.</param>
    /// <param name="completedAtUtc">Instant the run finished, in UTC.</param>
    /// <param name="balancesImported">Number of balances imported.</param>
    /// <param name="tradesImported">Number of trades imported.</param>
    /// <param name="transfersImported">Number of transfers imported.</param>
    /// <param name="nonZeroAssetCodes">Assets observed with a non-zero balance.</param>
    /// <param name="checkpoints">New resume points.</param>
    /// <param name="importEvents">Canonical events to import.</param>
    /// <param name="providerDiagnosticsJson">Opaque diagnostics JSON.</param>
    /// <returns>A successful result.</returns>
    public static IntegrationSyncResult Success(
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        int balancesImported,
        int tradesImported,
        int transfersImported,
        IReadOnlyList<string>? nonZeroAssetCodes = null,
        IReadOnlyList<IntegrationCheckpoint>? checkpoints = null,
        IReadOnlyList<IntegrationImportEvent>? importEvents = null,
        string? providerDiagnosticsJson = null) =>
        new(
            Status: IntegrationSyncStatus.Succeeded,
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: completedAtUtc,
            BalancesImported: balancesImported,
            TradesImported: tradesImported,
            TransfersImported: transfersImported,
            NonZeroAssetCodes: nonZeroAssetCodes ?? Array.Empty<string>(),
            Checkpoints: checkpoints ?? Array.Empty<IntegrationCheckpoint>(),
            ImportEvents: importEvents ?? Array.Empty<IntegrationImportEvent>(),
            ProviderDiagnosticsJson: providerDiagnosticsJson);

    /// <summary>Creates a <see cref="IntegrationSyncStatus.Failed"/> result with zero counts and empty lists.</summary>
    /// <param name="startedAtUtc">Instant the run started, in UTC.</param>
    /// <param name="completedAtUtc">Instant the run finished, in UTC.</param>
    /// <param name="errorKind">Failure classification.</param>
    /// <param name="errorMessage">Failure detail safe to show to the connection owner.</param>
    /// <param name="providerDiagnosticsJson">Opaque diagnostics JSON.</param>
    /// <returns>A failed result.</returns>
    public static IntegrationSyncResult Failure(
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        IntegrationErrorKind errorKind,
        string? errorMessage = null,
        string? providerDiagnosticsJson = null) =>
        new(
            Status: IntegrationSyncStatus.Failed,
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: completedAtUtc,
            BalancesImported: 0,
            TradesImported: 0,
            TransfersImported: 0,
            NonZeroAssetCodes: Array.Empty<string>(),
            Checkpoints: Array.Empty<IntegrationCheckpoint>(),
            ImportEvents: Array.Empty<IntegrationImportEvent>(),
            ErrorKind: errorKind,
            ErrorMessage: errorMessage,
            ProviderDiagnosticsJson: providerDiagnosticsJson);
}
