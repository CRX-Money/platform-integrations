namespace CrxMoney.Integrations.Abstractions.Models;

/// <summary>
/// A single execution (fill) of a trade at the provider.
/// </summary>
/// <param name="ExternalTradeId">Provider identifier of the fill; stable across syncs.</param>
/// <param name="Symbol">Provider market symbol, for example <c>BTCBRL</c>.</param>
/// <param name="ExecutedAtUtc">Execution instant in UTC.</param>
/// <param name="Quantity">Executed base-asset quantity.</param>
/// <param name="Price">Execution price in the quote asset.</param>
/// <param name="FeeAmount">Fee charged for the fill, in <paramref name="FeeAsset"/>.</param>
/// <param name="FeeAsset">Asset in which the fee was charged.</param>
/// <param name="IsBuy"><see langword="true"/> when the account bought the base asset.</param>
public sealed record TradeFill(
    string ExternalTradeId,
    string Symbol,
    DateTimeOffset ExecutedAtUtc,
    decimal Quantity,
    decimal Price,
    decimal FeeAmount,
    string FeeAsset,
    bool IsBuy);
