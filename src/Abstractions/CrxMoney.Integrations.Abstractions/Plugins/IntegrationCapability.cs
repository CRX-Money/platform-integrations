namespace CrxMoney.Integrations.Abstractions.Plugins;

/// <summary>
/// Flags describing what a plugin imports.
/// </summary>
[Flags]
public enum IntegrationCapability
{
    /// <summary>The plugin imports nothing.</summary>
    None = 0,

    /// <summary>Account balances (see <see cref="Models.BalanceSnapshot"/>).</summary>
    Balances = 1 << 0,

    /// <summary>Trade executions (see <see cref="Models.TradeFill"/>).</summary>
    Trades = 1 << 1,

    /// <summary>Deposits, withdrawals and other money movements (see <see cref="Models.TransferRecord"/>).</summary>
    Transfers = 1 << 2,

    /// <summary>Market prices or exchange rates.</summary>
    Prices = 1 << 3
}
