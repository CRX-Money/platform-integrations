namespace CrxMoney.Integrations.Abstractions.Models;

/// <summary>
/// Point-in-time balance of one asset held at the provider.
/// </summary>
/// <param name="AssetSymbol">Provider asset or currency code, for example <c>BTC</c> or <c>BRL</c>.</param>
/// <param name="Total">Total balance (<paramref name="Available"/> plus <paramref name="Locked"/>).</param>
/// <param name="Available">Balance that can be moved or spent.</param>
/// <param name="Locked">Balance reserved by open orders, holds or blocks.</param>
/// <param name="AsOfUtc">Instant the provider reported the balance, in UTC.</param>
public sealed record BalanceSnapshot(
    string AssetSymbol,
    decimal Total,
    decimal Available,
    decimal Locked,
    DateTimeOffset AsOfUtc);
