namespace CrxMoney.Integrations.Abstractions.Sync;

/// <summary>
/// The canonical, idempotent shape every provider movement is folded into before the host imports it.
/// </summary>
/// <param name="ExternalId">Provider identifier of the movement.</param>
/// <param name="IdempotencyKey">
/// Key the host uses to import a movement at most once. Derive it deterministically from provider,
/// connection, external id and side, so that re-running a sync produces the same keys.
/// </param>
/// <param name="Type">Plugin-defined event type.</param>
/// <param name="Side">Plugin-defined side or direction of the movement.</param>
/// <param name="OccurredAtUtc">Instant the movement occurred, in UTC.</param>
/// <param name="ExternalAccountId">Provider identifier of the account or wallet the movement belongs to.</param>
/// <param name="AssetCode">Asset or currency code, when the movement has one.</param>
/// <param name="Quantity">Asset quantity moved, when applicable.</param>
/// <param name="Amount">Monetary amount, when applicable.</param>
/// <param name="FeeAmount">Fee charged, when applicable.</param>
/// <param name="FeeAssetCode">Asset in which the fee was charged.</param>
/// <param name="ProviderData">
/// Opaque JSON with provider-specific detail; declare its shape through
/// <see cref="Plugins.IIntegrationImportSchemaManifestProvider"/>.
/// </param>
/// <param name="ProviderDataSchemaId">Schema identifier of <paramref name="ProviderData"/>.</param>
/// <param name="ProviderDataSchemaVersion">Schema version of <paramref name="ProviderData"/>.</param>
public sealed record IntegrationImportEvent(
    string ExternalId,
    string IdempotencyKey,
    string Type,
    string Side,
    DateTimeOffset OccurredAtUtc,
    string ExternalAccountId,
    string? AssetCode,
    decimal? Quantity,
    decimal? Amount,
    decimal? FeeAmount,
    string? FeeAssetCode,
    string? ProviderData,
    string? ProviderDataSchemaId = null,
    short? ProviderDataSchemaVersion = null);
