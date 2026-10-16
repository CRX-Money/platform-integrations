namespace CrxMoney.Integrations.Abstractions.Plugins;

/// <summary>
/// Scheduling defaults a plugin suggests to the host. Hosts may override them per connection.
/// </summary>
/// <param name="SyncEveryMinutes">Suggested interval between scheduled syncs, in minutes.</param>
/// <param name="SuggestedBackfillFromDate">Suggested earliest date for a first (backfill) sync, or <see langword="null"/> for no suggestion.</param>
public readonly record struct IntegrationPluginDefaults(
    short SyncEveryMinutes,
    DateOnly? SuggestedBackfillFromDate)
{
    /// <summary>Every 15 minutes, backfilling from 1 January 2025.</summary>
    public static IntegrationPluginDefaults Default => new(15, new DateOnly(2025, 1, 1));
}
