using System.Globalization;
using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.Inter.Api;
using CrxMoney.Integrations.Providers.Br.Inter.Mapping;
using CrxMoney.Integrations.Providers.Br.Inter.Sync;
using Microsoft.Extensions.Logging;

namespace CrxMoney.Integrations.Providers.Br.Inter;

/// <summary>
/// Syncs an Inter Empresas (Banco Inter PJ) account statement through the Inter Banking API.
/// </summary>
/// <remarks>
/// Credentials: <see cref="IntegrationSyncRequest.ApiKey"/> = client id,
/// <see cref="IntegrationSyncRequest.ApiSecret"/> = client secret,
/// <see cref="IntegrationSyncRequest.Passphrase"/> = an <see cref="InterCredentialBundle"/>.
/// A request with <c>ConnectionId &lt;= 0</c> is a connection test: it obtains a token and reads the
/// balance (<c>/banking/v2/saldo</c>) without importing anything.
/// </remarks>
public sealed class InterIntegrationPlugin : IIntegrationPlugin, IIntegrationImportSchemaManifestProvider
{
    /// <summary>The provider id; equals the platform's <c>Integration.Provider</c> name.</summary>
    public const string Id = "Inter";

    private readonly InterClientRegistry _clients;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InterIntegrationPlugin> _logger;

    internal InterIntegrationPlugin(InterClientRegistry clients, TimeProvider timeProvider, ILogger<InterIntegrationPlugin> logger)
    {
        _clients = clients;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderId => Id;

    /// <inheritdoc />
    public string DisplayName => "Inter Empresas";

    /// <inheritdoc />
    public IntegrationCapability Capabilities => IntegrationCapability.Transfers;

    /// <summary>Hourly: Inter allows ten statement calls a minute, and a business statement rarely needs more.</summary>
    public IntegrationPluginDefaults Defaults => new(SyncEveryMinutes: 60, SuggestedBackfillFromDate: new DateOnly(2025, 1, 1));

    /// <inheritdoc />
    public IntegrationImportSchemaManifest GetImportSchemaManifest() => InterImportSchemas.Manifest;

    /// <inheritdoc />
    public async Task<IntegrationSyncResult> SyncAsync(IntegrationSyncRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = _timeProvider.GetUtcNow();

        try
        {
            var settings = InterCredentialBundle.Parse(request.ApiKey, request.ApiSecret, request.Passphrase);
            var client = _clients.GetClient(settings);

            if (client.CertificateNotAfterUtc is { } notAfter && notAfter <= startedAt)
            {
                return Fail(startedAt, IntegrationErrorKind.Configuration,
                    $"The Inter certificate expired on {notAfter:yyyy-MM-dd}. Create new credentials in Inter's Internet Banking and update the connection.");
            }

            return request.ConnectionId <= 0
                ? await TestConnectionAsync(client, startedAt, cancellationToken).ConfigureAwait(false)
                : await SyncStatementAsync(client, request, startedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (InterCredentialException ex)
        {
            _logger.LogWarning("Inter connection {ConnectionId} is misconfigured: {Reason}", request.ConnectionId, ex.Message);
            return Fail(startedAt, IntegrationErrorKind.Configuration, ex.Message);
        }
        catch (InterApiException ex)
        {
            _logger.LogWarning("Inter sync for connection {ConnectionId} failed ({ErrorKind}, HTTP {StatusCode}): {Reason}",
                request.ConnectionId, ex.Kind, ex.StatusCode is { } status ? (int)status : null, ex.Message);
            return Fail(startedAt, ex.Kind, ex.Message);
        }
        catch (InterMappingException ex)
        {
            _logger.LogWarning("Inter sync for connection {ConnectionId} received an unusable statement entry: {Reason}", request.ConnectionId, ex.Message);
            return Fail(startedAt, IntegrationErrorKind.Upstream, ex.Message);
        }
        catch (FormatException ex)
        {
            return Fail(startedAt, IntegrationErrorKind.Validation, ex.Message);
        }
    }

    private async Task<IntegrationSyncResult> TestConnectionAsync(InterApiClient client, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await client.GetAccessTokenAsync(InterApiClient.StatementReadScope, cancellationToken).ConfigureAwait(false);
        var balance = await client.GetBalanceAsync(date: null, cancellationToken).ConfigureAwait(false);

        var diagnostics = Diagnostics(writer =>
        {
            writer.WriteString("check", "token+saldo");
            writer.WriteString("environment", EnvironmentName(client.Environment));
            writer.WriteBoolean("balanceRead", balance.Disponivel is not null);
            if (client.CertificateNotAfterUtc is { } notAfter)
                writer.WriteString("certificateNotAfterUtc", notAfter);
        });

        return IntegrationSyncResult.Success(startedAt, _timeProvider.GetUtcNow(), 0, 0, 0, providerDiagnosticsJson: diagnostics);
    }

    private async Task<IntegrationSyncResult> SyncStatementAsync(
        InterApiClient client,
        IntegrationSyncRequest request,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var today = InterTransactionMapper.BankingToday(startedAt);
        var plan = InterStatementPlan.Create(request, today, Defaults.SuggestedBackfillFromDate ?? today, _clients.Options);
        var reader = new InterStatementReader(client, _clients.Options);

        var events = new List<IntegrationImportEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = 0;
        foreach (var (from, to) in plan.Windows)
        {
            foreach (var transaction in await reader.ReadAsync(from, to, cancellationToken).ConfigureAwait(false))
            {
                entries++;
                var importEvent = InterTransactionMapper.Map(transaction, request.ConnectionId, client.AccountNumber, client.Environment);
                // An entry can move between pages while Inter books new ones; keep its first copy.
                if (seen.Add(importEvent.ExternalId))
                    events.Add(importEvent);
            }
        }

        _logger.LogInformation(
            "Inter sync for connection {ConnectionId} read {Entries} statement entries ({Events} distinct) from {From:yyyy-MM-dd} to {To:yyyy-MM-dd} in {Windows} window(s), {Pages} page(s).",
            request.ConnectionId, entries, events.Count, plan.From, plan.To, plan.Windows.Count, reader.PagesRead);

        var diagnostics = Diagnostics(writer =>
        {
            writer.WriteString("environment", EnvironmentName(client.Environment));
            writer.WriteString("from", plan.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString("to", plan.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteNumber("windows", plan.Windows.Count);
            writer.WriteNumber("pages", reader.PagesRead);
            writer.WriteNumber("entries", entries);
            writer.WriteNumber("events", events.Count);
            if (client.CertificateNotAfterUtc is { } notAfter)
                writer.WriteString("certificateNotAfterUtc", notAfter);
        });

        IntegrationCheckpoint[] checkpoints = plan.IsEmpty ? [] : [InterStatementPlan.CheckpointFor(plan.To)];
        return IntegrationSyncResult.Success(
            startedAt,
            _timeProvider.GetUtcNow(),
            balancesImported: 0,
            tradesImported: 0,
            transfersImported: events.Count,
            nonZeroAssetCodes: events.Count > 0 ? [InterTransactionMapper.AssetCode] : [],
            checkpoints: checkpoints,
            importEvents: events,
            providerDiagnosticsJson: diagnostics);
    }

    private IntegrationSyncResult Fail(DateTimeOffset startedAt, IntegrationErrorKind kind, string message) =>
        IntegrationSyncResult.Failure(startedAt, _timeProvider.GetUtcNow(), kind, message);

    private static string EnvironmentName(InterEnvironment environment) =>
        environment == InterEnvironment.Production ? "production" : "sandbox";

    private static string Diagnostics(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
