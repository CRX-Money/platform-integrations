using CrxMoney.Integrations.Abstractions.Sync;

namespace CrxMoney.Integrations.Countries.Br.OpenFinance;

/// <summary>
/// An aggregator (or any other channel) that reads Brazilian Open Finance data. This is the only
/// thing a new aggregator has to implement: <see cref="OpenFinanceIntegrationPlugin"/> does the
/// mapping, keys, checkpoints and diagnostics for every source the same way.
/// </summary>
public interface IOpenFinanceSource
{
    /// <summary>Short name recorded in provider data (<c>Pluggy</c>).</summary>
    string SourceName { get; }

    /// <summary>Checks the credentials and the consent without reading transactions.</summary>
    /// <exception cref="OpenFinanceSourceException">An expected failure: bad credentials, consent problems, upstream errors.</exception>
    Task<OpenFinanceConnectionCheck> CheckConnectionAsync(IntegrationSyncRequest request, CancellationToken cancellationToken);

    /// <summary>Reads every account of the connection with its transactions.</summary>
    /// <exception cref="OpenFinanceSourceException">An expected failure: bad credentials, consent problems, upstream errors.</exception>
    Task<OpenFinanceSnapshot> ReadAsync(IntegrationSyncRequest request, CancellationToken cancellationToken);
}

/// <summary>An expected source failure. <see cref="Exception.Message"/> is safe to show: no secrets, no personal data.</summary>
public sealed class OpenFinanceSourceException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OpenFinanceSourceException(IntegrationErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The contract's classification of the failure.</summary>
    public IntegrationErrorKind Kind { get; }
}
