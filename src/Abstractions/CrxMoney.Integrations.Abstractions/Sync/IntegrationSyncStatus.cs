namespace CrxMoney.Integrations.Abstractions.Sync;

/// <summary>
/// Overall outcome of a sync run.
/// </summary>
public enum IntegrationSyncStatus
{
    /// <summary>Every requested resource was synced.</summary>
    Succeeded = 0,

    /// <summary>Some resources were synced and others failed.</summary>
    PartiallySucceeded = 1,

    /// <summary>The run failed.</summary>
    Failed = 2
}
