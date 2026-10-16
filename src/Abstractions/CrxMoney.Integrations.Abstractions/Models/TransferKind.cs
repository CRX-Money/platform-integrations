namespace CrxMoney.Integrations.Abstractions.Models;

/// <summary>
/// Direction of a <see cref="TransferRecord"/> relative to the connected account.
/// </summary>
public enum TransferKind
{
    /// <summary>Funds arriving in the connected account.</summary>
    Deposit = 0,

    /// <summary>Funds leaving the connected account.</summary>
    Withdrawal = 1
}
