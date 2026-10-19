using System.Globalization;
using System.Text;
using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using Microsoft.Extensions.Logging;

namespace CrxMoney.Integrations.Countries.Br.OpenFinance;

/// <summary>
/// The plugin every Open Finance aggregator shares. A concrete plugin only names itself and hands over
/// its <see cref="IOpenFinanceSource"/>; reading, key assignment, mapping, windowing, checkpoints and the
/// balance report happen here, identically for every aggregator.
/// </summary>
/// <remarks>
/// A request with <c>ConnectionId &lt;= 0</c> is a connection test (<see cref="IOpenFinanceSource.CheckConnectionAsync"/>).
/// A sync asks the source for lines booked from <see cref="OverlapDays"/> before the host's window start
/// (everything when the host gives no start) and sends exactly those. The source returns every line of each
/// booking date it covers, so key ordinals match a full read. The account balance cannot be checked against
/// a window of lines; it is reported with its date for the host to compare with its ledger.
/// </remarks>
public abstract class OpenFinanceIntegrationPlugin : IIntegrationPlugin, IIntegrationImportSchemaManifestProvider
{
    /// <summary>Checkpoint resource holding the latest booking date read.</summary>
    public const string CheckpointResource = "transactions";

    /// <summary>Days re-sent before the host's window start, for lines the bank books late.</summary>
    public const int OverlapDays = 7;

    private readonly IOpenFinanceSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Creates the plugin around its source.</summary>
    protected OpenFinanceIntegrationPlugin(IOpenFinanceSource source, TimeProvider timeProvider, ILogger logger)
    {
        _source = source;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public abstract string ProviderId { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public IntegrationCapability Capabilities => IntegrationCapability.Transfers;

    /// <summary>Daily: Open Finance aggregators refresh connected items about once a day.</summary>
    public virtual IntegrationPluginDefaults Defaults => new(SyncEveryMinutes: 1440, SuggestedBackfillFromDate: null);

    /// <inheritdoc />
    public IntegrationImportSchemaManifest GetImportSchemaManifest() => OpenFinanceImportSchemas.Manifest;

    /// <inheritdoc />
    public async Task<IntegrationSyncResult> SyncAsync(IntegrationSyncRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = _timeProvider.GetUtcNow();
        try
        {
            return request.ConnectionId <= 0
                ? await TestAsync(request, startedAt, cancellationToken).ConfigureAwait(false)
                : await ReadAsync(request, startedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenFinanceSourceException ex)
        {
            _logger.LogWarning("{Provider} sync for connection {ConnectionId} failed ({ErrorKind}): {Reason}",
                ProviderId, request.ConnectionId, ex.Kind, ex.Message);
            return IntegrationSyncResult.Failure(startedAt, _timeProvider.GetUtcNow(), ex.Kind, ex.Message);
        }
        catch (OpenFinanceIdentityException ex)
        {
            // An account without the bank identity the keys are built from: fail rather than guess one.
            _logger.LogWarning("{Provider} sync for connection {ConnectionId} received unusable data: {Reason}",
                ProviderId, request.ConnectionId, ex.Message);
            return IntegrationSyncResult.Failure(startedAt, _timeProvider.GetUtcNow(), IntegrationErrorKind.Upstream, ex.Message);
        }
    }

    private async Task<IntegrationSyncResult> TestAsync(IntegrationSyncRequest request, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var check = await _source.CheckConnectionAsync(request, cancellationToken).ConfigureAwait(false);
        var diagnostics = Diagnostics(writer =>
        {
            writer.WriteString("check", "connection");
            writer.WriteString("source", _source.SourceName);
            writer.WriteString("summary", check.Summary);
            if (check.CredentialExpiresAt is { } expiresAt)
                writer.WriteString("credentialExpiresAtUtc", expiresAt.ToUniversalTime());
            WriteRaw(writer, "sourceDiagnostics", check.DiagnosticsJson);
        });

        return IntegrationSyncResult.Success(startedAt, _timeProvider.GetUtcNow(), 0, 0, 0, providerDiagnosticsJson: diagnostics);
    }

    private async Task<IntegrationSyncResult> ReadAsync(IntegrationSyncRequest request, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var sendFrom = SendFrom(request);
        var snapshot = await _source.ReadAsync(request, sendFrom, cancellationToken).ConfigureAwait(false);

        var events = new List<IntegrationImportEvent>();
        var accountReports = new List<Action<Utf8JsonWriter>>();
        DateOnly? latest = null;
        var pending = 0;
        foreach (var (account, transactions) in snapshot.Accounts)
        {
            var posted = transactions.Where(t => !t.IsPending).ToList();
            pending += transactions.Count - posted.Count;
            var sent = 0;
            foreach (var (transaction, ordinal) in OpenFinanceTransactionMapper.AssignOrdinals(account, posted))
            {
                if (latest is null || transaction.BookingDate > latest)
                    latest = transaction.BookingDate;
                if (sendFrom is { } from && transaction.BookingDate < from)
                    continue;
                events.Add(OpenFinanceTransactionMapper.Map(account, transaction, ordinal, _source.SourceName));
                sent++;
            }

            var externalAccountId = account.ExternalAccountId;
            accountReports.Add(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", account.Kind == OpenFinanceAccountKind.CreditCard ? "creditCard" : "deposit");
                writer.WriteString("externalAccountId", externalAccountId);
                writer.WriteNumber("lines", transactions.Count);
                writer.WriteNumber("posted", posted.Count);
                writer.WriteNumber("sent", sent);
                // A window of lines proves nothing about the balance; the host compares it with its ledger.
                if (account.Kind == OpenFinanceAccountKind.Deposit && account.Balance is { } balance)
                {
                    writer.WriteNumber("balance", balance);
                    if (account.BalanceAt is { } balanceAt)
                        writer.WriteString("balanceAtUtc", balanceAt.ToUniversalTime());
                }

                writer.WriteEndObject();
            });
        }

        _logger.LogInformation("{Provider} sync for connection {ConnectionId}: {Accounts} account(s), {Events} event(s) sent, {Pending} pending line(s) skipped.",
            ProviderId, request.ConnectionId, snapshot.Accounts.Count, events.Count, pending);

        var diagnostics = Diagnostics(writer =>
        {
            writer.WriteString("source", _source.SourceName);
            writer.WriteNumber("events", events.Count);
            writer.WriteNumber("pendingSkipped", pending);
            if (sendFrom is { } from)
                writer.WriteString("sentFrom", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (snapshot.CredentialExpiresAt is { } expiresAt)
                writer.WriteString("credentialExpiresAtUtc", expiresAt.ToUniversalTime());
            writer.WriteStartArray("accounts");
            foreach (var report in accountReports)
                report(writer);
            writer.WriteEndArray();
            WriteRaw(writer, "sourceDiagnostics", snapshot.DiagnosticsJson);
        });

        // A window with no new lines keeps the latest date an earlier sync read.
        if (PreviousLatest(request) is { } previous && (latest is null || previous > latest))
            latest = previous;
        IntegrationCheckpoint[] checkpoints = latest is { } last
            ? [new IntegrationCheckpoint(CheckpointResource, last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]
            : [];
        return IntegrationSyncResult.Success(
            startedAt,
            _timeProvider.GetUtcNow(),
            balancesImported: 0,
            tradesImported: 0,
            transfersImported: events.Count,
            nonZeroAssetCodes: events.Select(e => e.AssetCode!).Distinct(StringComparer.Ordinal).ToArray(),
            checkpoints: checkpoints,
            importEvents: events,
            providerDiagnosticsJson: diagnostics);
    }

    /// <summary>
    /// The first booking date a sync reads and sends: <see cref="OverlapDays"/> before the host's window start,
    /// or <c>null</c> (the full history) when the host gives no start.
    /// </summary>
    private static DateOnly? SendFrom(IntegrationSyncRequest request) =>
        request.WindowStartUtc is { } start ? BrazilBankingCalendar.DateOf(start).AddDays(-OverlapDays) : null;

    private static DateOnly? PreviousLatest(IntegrationSyncRequest request) =>
        request.PreviousCheckpoints?.FirstOrDefault(c => c.Resource == CheckpointResource) is { } checkpoint
        && DateOnly.TryParseExact(checkpoint.CursorValue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static void WriteRaw(Utf8JsonWriter writer, string name, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        writer.WritePropertyName(name);
        using var document = JsonDocument.Parse(json);
        document.RootElement.WriteTo(writer);
    }

    private static string Diagnostics(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
