using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

/// <summary>
/// An adapter: one aggregator (or any other channel) that reads Brazilian Open Finance data. This is the
/// only thing a new aggregator has to implement: <see cref="OpenFinanceIntegrationPlugin"/> does the
/// mapping, keys, checkpoints and diagnostics for every adapter the same way.
/// </summary>
public interface IOpenFinanceSource
{
    /// <summary>
    /// The aggregator's name (<c>Pluggy</c>): the adapter id the host stores with each connection
    /// (<see cref="IntegrationSyncRequest.AdapterId"/>) and the <c>provider</c> recorded in provider data.
    /// </summary>
    string SourceName { get; }

    /// <summary>The adapter's connection settings in <paramref name="culture"/>, or <c>null</c> when it declares none.</summary>
    /// <param name="culture">A BCP 47 culture name; the adapter falls back to its default.</param>
    IntegrationConnectionAdapterSettings? GetConnectionSettings(string? culture) => null;

    /// <summary>Checks the credentials and the consent without reading transactions.</summary>
    /// <exception cref="OpenFinanceSourceException">An expected failure: bad credentials, consent problems, upstream errors.</exception>
    Task<OpenFinanceConnectionCheck> CheckConnectionAsync(IntegrationSyncRequest request, CancellationToken cancellationToken);

    /// <summary>Reads every account of the connection with its transactions from <paramref name="readFrom"/> on, and its investment positions.</summary>
    /// <param name="request">The host's request.</param>
    /// <param name="readFrom">
    /// The first booking date (Brazil's calendar) the plugin will send, or <c>null</c> for the full history.
    /// A source must return <b>every</b> posted line booked on or after it and may return earlier lines too
    /// (the plugin sends only those booked, or for a card installment dated by its bill, on or after it). Lines that share a key identity also share their booking date, so a read that
    /// starts at a whole date numbers them exactly as a full read does and the keys stay the same. A card installment
    /// is sent by its bill's date (<see cref="OpenFinanceTransactionMapper.EventDate"/>), which can be months after
    /// its booking date (the purchase): a source should also return the installment lines put on a bill on or after
    /// <paramref name="readFrom"/>, as a window on the aggregator's bill-dated line date does.
    /// <para>
    /// Two more rules, whatever <paramref name="readFrom"/> is. A card reported with an
    /// <see cref="OpenFinanceAccount.Card"/> balance that includes pending lines must come with <b>every</b> pending
    /// line of that card, so the plugin can split the balance into settled and pending. Investment positions
    /// (<see cref="OpenFinanceSnapshot.Investments"/>) come with every movement the source holds; the plugin
    /// applies the window to them as it does to account lines.
    /// </para>
    /// <para>
    /// A card may come with its bills (<see cref="OpenFinanceAccountTransactions.Bills"/>), every bill the source
    /// lists, best effort: a source that cannot read them returns none rather than failing the read.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="OpenFinanceSourceException">An expected failure: bad credentials, consent problems, upstream errors.</exception>
    Task<OpenFinanceSnapshot> ReadAsync(IntegrationSyncRequest request, DateOnly? readFrom, CancellationToken cancellationToken);
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
