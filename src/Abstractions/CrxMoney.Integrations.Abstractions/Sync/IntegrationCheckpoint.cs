namespace CrxMoney.Integrations.Abstractions.Sync;

/// <summary>
/// A resume point for one provider resource, persisted by the host between syncs.
/// </summary>
/// <param name="Resource">Plugin-defined resource name, for example <c>trades:BTCBRL</c> or <c>statement</c>.</param>
/// <param name="CursorValue">Opaque cursor (timestamp, page token or last id) the plugin resumes from.</param>
public sealed record IntegrationCheckpoint(
    string Resource,
    string CursorValue);
