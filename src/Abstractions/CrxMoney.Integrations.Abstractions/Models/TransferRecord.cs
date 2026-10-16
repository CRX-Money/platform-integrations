namespace CrxMoney.Integrations.Abstractions.Models;

/// <summary>
/// A deposit into or withdrawal from the connected account.
/// </summary>
/// <param name="ExternalTransferId">Provider identifier of the transfer; stable across syncs.</param>
/// <param name="Kind">Direction of the transfer.</param>
/// <param name="AssetSymbol">Asset or currency moved.</param>
/// <param name="Amount">Amount moved; the direction comes from <paramref name="Kind"/>.</param>
/// <param name="OccurredAtUtc">Instant the provider recorded the transfer, in UTC.</param>
/// <param name="Network">Optional transfer network or rail, for example a blockchain network.</param>
/// <param name="FeeAmount">Optional fee charged for the transfer.</param>
/// <param name="TransactionHash">Optional on-chain hash or rail-level transaction identifier.</param>
public sealed record TransferRecord(
    string ExternalTransferId,
    TransferKind Kind,
    string AssetSymbol,
    decimal Amount,
    DateTimeOffset OccurredAtUtc,
    string? Network = null,
    decimal? FeeAmount = null,
    string? TransactionHash = null);
