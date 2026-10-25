using System.Globalization;
using System.Text;
using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br;
using Microsoft.Extensions.Logging;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

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
/// a window of lines; it is reported with its date for the host to compare with its ledger. A card's balance is
/// reported settled (without its pending lines, but with the pending bill payments it received applied) with the
/// pending amount beside it. Investment movements become events on their position's account under the same
/// window, and sweeps and card bill payments are paired with their other side when one candidate matches
/// (<see cref="OpenFinancePairing"/>).
/// </remarks>
public abstract class OpenFinanceIntegrationPlugin
    : IIntegrationPlugin, IIntegrationImportSchemaManifestProvider, IIntegrationConnectionSettingsProvider, IIntegrationConnectionTester
{
    /// <summary>Checkpoint resource holding the latest booking date read.</summary>
    public const string CheckpointResource = "transactions";

    /// <summary>Days re-sent before the host's window start, for lines the bank books late.</summary>
    public const int OverlapDays = 7;

    private readonly IReadOnlyList<IOpenFinanceSource> _sources;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Creates the plugin around a single adapter.</summary>
    protected OpenFinanceIntegrationPlugin(IOpenFinanceSource source, TimeProvider timeProvider, ILogger logger)
        : this([source], timeProvider, logger)
    {
    }

    /// <summary>Creates the plugin around its adapters; a sync uses the one its request names (<see cref="IntegrationSyncRequest.AdapterId"/>).</summary>
    /// <exception cref="ArgumentException">Two adapters share a name.</exception>
    protected OpenFinanceIntegrationPlugin(IEnumerable<IOpenFinanceSource> sources, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
        if (_sources.GroupBy(s => s.SourceName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new ArgumentException($"Two Open Finance adapters are named '{duplicate.Key}'.", nameof(sources));
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The adapters' names (their adapter ids), in registration order.</summary>
    public IReadOnlyList<string> AdapterIds => [.. _sources.Select(s => s.SourceName)];

    /// <summary>
    /// The adapter a request names; with no name, the only adapter. Returns the failure message when there is none.
    /// </summary>
    private IOpenFinanceSource? SelectSource(string? adapterId, OpenFinanceTexts texts, out string? failure)
    {
        failure = null;
        if (adapterId is null)
        {
            if (_sources.Count == 1)
                return _sources[0];
            failure = texts.NoAdapter(_sources.Count);
            return null;
        }

        var source = _sources.FirstOrDefault(s => string.Equals(s.SourceName, adapterId, StringComparison.Ordinal));
        if (source is null)
            failure = texts.UnknownAdapter(adapterId);
        return source;
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

    /// <summary>The settings each adapter declares, in registration order.</summary>
    /// <inheritdoc />
    public virtual IntegrationConnectionSettingsManifest GetConnectionSettings(string? culture = null) =>
        new(ProviderId, OpenFinanceTexts.Resolve(culture), [.. _sources.Select(s => s.GetConnectionSettings(culture)).OfType<IntegrationConnectionAdapterSettings>()]);

    /// <summary>
    /// Checks the settings through the source (<see cref="IOpenFinanceSource.CheckConnectionAsync"/>) and describes
    /// the result without the aggregator's name: refresh time, consent expiry, sandbox flag, and the account
    /// operators found by their COMPE code in the country package's institution list.
    /// </summary>
    /// <inheritdoc />
    public async Task<IntegrationConnectionTestResult> TestConnectionAsync(
        string adapterId,
        IntegrationSyncRequest request,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var texts = OpenFinanceTexts.For(culture);
        if (SelectSource(adapterId, texts, out var failure) is not { } source)
            return IntegrationConnectionTestResult.Failure(IntegrationErrorKind.Configuration, failure!);

        try
        {
            var check = await source.CheckConnectionAsync(request with { AdapterId = adapterId }, cancellationToken).ConfigureAwait(false);
            var operators = check.AccountOperatorCompes
                .Select(BrazilianInstitutions.Find)
                .OfType<BrazilianInstitution>()
                .DistinctBy(i => i.Compe)
                .Select(i => new IntegrationAccountOperator(i.Name, i.Website))
                .ToList();
            return new IntegrationConnectionTestResult(true, texts.Connected(check.LastRefreshedAt, check.CredentialExpiresAt, operators, check.IsSandbox))
            {
                CredentialExpiresAtUtc = check.CredentialExpiresAt?.ToUniversalTime(),
                LastRefreshedAtUtc = check.LastRefreshedAt?.ToUniversalTime(),
                IsSandbox = check.IsSandbox,
                AccountOperators = operators,
                Connector = check.Connector,
            };
        }
        catch (OpenFinanceSourceException ex)
        {
            _logger.LogWarning("{Provider} connection test failed ({ErrorKind}): {Reason}", ProviderId, ex.Kind, ex.Message);
            return IntegrationConnectionTestResult.Failure(ex.Kind, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IntegrationSyncResult> SyncAsync(IntegrationSyncRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = _timeProvider.GetUtcNow();
        if (SelectSource(request.AdapterId, OpenFinanceTexts.For(null), out var failure) is not { } source)
        {
            _logger.LogWarning("{Provider} sync for connection {ConnectionId} has no usable adapter: {Reason}", ProviderId, request.ConnectionId, failure);
            return IntegrationSyncResult.Failure(startedAt, _timeProvider.GetUtcNow(), IntegrationErrorKind.Configuration, failure!);
        }

        try
        {
            return request.ConnectionId <= 0
                ? await TestAsync(source, request, startedAt, cancellationToken).ConfigureAwait(false)
                : await ReadAsync(source, request, startedAt, cancellationToken).ConfigureAwait(false);
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

    private async Task<IntegrationSyncResult> TestAsync(IOpenFinanceSource source, IntegrationSyncRequest request, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var check = await source.CheckConnectionAsync(request, cancellationToken).ConfigureAwait(false);
        var diagnostics = Diagnostics(writer =>
        {
            writer.WriteString("check", "connection");
            writer.WriteString("source", source.SourceName);
            writer.WriteString("summary", check.Summary);
            if (check.CredentialExpiresAt is { } expiresAt)
                writer.WriteString("credentialExpiresAtUtc", expiresAt.ToUniversalTime());
            WriteConnector(writer, check.Connector);
            WriteRaw(writer, "sourceDiagnostics", check.DiagnosticsJson);
        });

        return IntegrationSyncResult.Success(startedAt, _timeProvider.GetUtcNow(), 0, 0, 0, providerDiagnosticsJson: diagnostics);
    }

    private async Task<IntegrationSyncResult> ReadAsync(IOpenFinanceSource source, IntegrationSyncRequest request, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var sendFrom = SendFrom(request);
        var snapshot = await source.ReadAsync(request, sendFrom, cancellationToken).ConfigureAwait(false);
        bool IsSent(DateOnly bookingDate) => sendFrom is not { } from || bookingDate >= from;

        // Every posted line and every investment movement the read returned gets its key first: pairing looks at all
        // of them, sending only at those in the window.
        var lines = new List<PreparedLine>();
        DateOnly? latest = null;
        var pending = 0;
        foreach (var (account, transactions) in snapshot.Accounts)
        {
            var posted = transactions.Where(t => !t.IsPending).ToList();
            pending += transactions.Count - posted.Count;
            foreach (var (transaction, ordinal) in OpenFinanceTransactionMapper.AssignOrdinals(account, posted))
            {
                if (latest is null || transaction.BookingDate > latest)
                    latest = transaction.BookingDate;
                lines.Add(new PreparedLine(account, transaction, ordinal,
                    OpenFinanceTransactionMapper.IdempotencyKey(OpenFinanceTransactionMapper.LineIdentity(account, transaction), ordinal),
                    OpenFinanceTransactionMapper.TransferHint(account, transaction),
                    IsSent(transaction.BookingDate)));
            }
        }

        var duplicate = snapshot.Investments.GroupBy(i => i.Investment.ExternalAccountId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new OpenFinanceIdentityException("Two investment positions of the connection have the same identity; their keys would collide.");
        var movements = new List<PreparedMovement>();
        foreach (var (investment, investmentMovements) in snapshot.Investments)
        {
            foreach (var (movement, ordinal) in OpenFinanceInvestmentMapper.AssignOrdinals(investment, investmentMovements))
            {
                movements.Add(new PreparedMovement(investment, movement, ordinal,
                    OpenFinanceInvestmentMapper.CashEventKey(investment, movement, ordinal), IsSent(movement.BookingDate)));
            }
        }

        var investmentPairs = OpenFinancePairing.Match(
            lines.Where(l => l.Account.Kind == OpenFinanceAccountKind.Deposit && l.Hint == OpenFinanceTransactionMapper.InvestmentHint)
                .Select(l => new OpenFinancePairing.Side(l.Key, l.Account.ExternalAccountId, l.Transaction.Direction, l.Transaction.Amount, l.Transaction.BookingDate, l.IsSent)),
            movements.Where(m => m.CashKey is not null)
                .Select(m => new OpenFinancePairing.Side(m.CashKey!, m.Investment.ExternalAccountId,
                    m.Movement.Operation == OpenFinanceInvestmentOperation.Application ? OpenFinanceDirection.Debit : OpenFinanceDirection.Credit,
                    OpenFinanceInvestmentMapper.CashAmount(m.Movement)!.Value, m.Movement.BookingDate, m.IsSent) { Origin = m.Investment }),
            OpenFinancePairing.InvestmentToleranceDays,
            OpenFinancePairing.InvestmentToleranceDays);
        // A card often keeps the payment it received pending after the checking debit has posted: it still pairs, so
        // the checking line names the card it paid, but it has no key yet and gets no pair (and is never sent).
        var pendingCardPayments = snapshot.Accounts
            .Where(a => a.Account.Kind == OpenFinanceAccountKind.CreditCard)
            .SelectMany(a => a.Transactions
                .Where(t => t.IsPending && IsBillPayment(a.Account, t))
                .Select(t => new PreparedLine(a.Account, t, -1, $"pending|{a.Account.ExternalAccountId}|{t.SourceTransactionId}",
                    OpenFinanceTransactionMapper.CardBillPaymentHint, IsSent: false)));
        var cardPairs = OpenFinancePairing.Match(
            lines.Where(l => l.Account.Kind == OpenFinanceAccountKind.Deposit && l.Hint == OpenFinanceTransactionMapper.CardBillPaymentHint && l.Transaction.Direction == OpenFinanceDirection.Debit)
                .Select(l => new OpenFinancePairing.Side(l.Key, l.Account.ExternalAccountId, OpenFinanceDirection.Debit, l.Transaction.Amount, l.Transaction.BookingDate, l.IsSent)),
            lines.Where(l => l.Account.Kind == OpenFinanceAccountKind.CreditCard && l.Hint == OpenFinanceTransactionMapper.CardBillPaymentHint)
                .Concat(pendingCardPayments)
                .Select(l => new OpenFinancePairing.Side(l.Key, l.Account.ExternalAccountId, OpenFinanceDirection.Debit, l.Transaction.Amount, l.Transaction.BookingDate, l.IsSent) { Origin = l }),
            OpenFinancePairing.CardEarlierDays,
            OpenFinancePairing.CardLaterDays);

        // A card's bills by id, so a card line can be dated at the bill it names (installments, #1417).
        var bills = snapshot.Accounts
            .Where(a => a.Account.Kind == OpenFinanceAccountKind.CreditCard)
            .SelectMany(a => a.Bills.Select(b => (Key: $"{a.Account.ExternalAccountId}|{b.Id.Trim()}", Bill: b)))
            .GroupBy(b => b.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Bill, StringComparer.Ordinal);

        var events = new List<IntegrationImportEvent>();
        var sentPerAccount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines.Where(l => l.IsSent))
        {
            var links = Links(line.Key);
            if (line.Account.Kind == OpenFinanceAccountKind.CreditCard
                && line.Transaction.CardBillId?.Trim() is { Length: > 0 } billId
                && bills.TryGetValue($"{line.Account.ExternalAccountId}|{billId}", out var bill))
            {
                links = (links ?? new OpenFinanceLineLinks()) with { Bill = bill };
            }

            events.Add(OpenFinanceTransactionMapper.Map(line.Account, line.Transaction, line.Ordinal, source.SourceName, links));
            sentPerAccount[line.Account.ExternalAccountId] = sentPerAccount.GetValueOrDefault(line.Account.ExternalAccountId) + 1;
        }

        foreach (var movement in movements.Where(m => m.IsSent))
        {
            var pair = movement.CashKey is { } cashKey && investmentPairs.OtherToCash.TryGetValue(cashKey, out var cash)
                ? new OpenFinancePair(cash.ExternalAccountId, cash.Key)
                : null;
            var mapped = OpenFinanceInvestmentMapper.Map(movement.Investment, movement.Movement, movement.Ordinal, source.SourceName, pair);
            events.AddRange(mapped);
            var accountId = movement.Investment.ExternalAccountId;
            sentPerAccount[accountId] = sentPerAccount.GetValueOrDefault(accountId) + mapped.Count;
        }

        OpenFinanceLineLinks? Links(string key)
        {
            if (investmentPairs.CashToOther.TryGetValue(key, out var position))
                return new OpenFinanceLineLinks { Pair = new OpenFinancePair(position.ExternalAccountId, position.Key), Investment = (OpenFinanceInvestment)position.Origin! };
            if (cardPairs.CashToOther.TryGetValue(key, out var cardSide))
            {
                var cardLine = (PreparedLine)cardSide.Origin!;
                var card = cardLine.Account;
                var bill = cardLine.Transaction.CardBillId;
                return new OpenFinanceLineLinks
                {
                    Pair = cardLine.Transaction.IsPending ? null : new OpenFinancePair(cardSide.ExternalAccountId, cardSide.Key),
                    PaidCard = new OpenFinanceCardReference(BrazilianBankAccount.NormalizeCompe(card.IssuerCompe!), card.CardLast4!, bill),
                };
            }

            return cardPairs.OtherToCash.TryGetValue(key, out var checking)
                ? new OpenFinanceLineLinks { Pair = new OpenFinancePair(checking.ExternalAccountId, checking.Key) }
                : null;
        }

        _logger.LogInformation("{Provider} sync for connection {ConnectionId}: {Accounts} account(s), {Investments} investment position(s), {Events} event(s) sent, {Pending} pending line(s) skipped.",
            ProviderId, request.ConnectionId, snapshot.Accounts.Count, snapshot.Investments.Count, events.Count, pending);

        var diagnostics = Diagnostics(writer =>
        {
            writer.WriteString("source", source.SourceName);
            writer.WriteNumber("events", events.Count);
            writer.WriteNumber("pendingSkipped", pending);
            if (sendFrom is { } from)
                writer.WriteString("sentFrom", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (snapshot.CredentialExpiresAt is { } expiresAt)
                writer.WriteString("credentialExpiresAtUtc", expiresAt.ToUniversalTime());
            writer.WriteStartArray("accounts");
            foreach (var accountTransactions in snapshot.Accounts)
                WriteAccountReport(writer, accountTransactions, sentPerAccount.GetValueOrDefault(accountTransactions.Account.ExternalAccountId));
            foreach (var (investment, investmentMovements) in snapshot.Investments)
                WriteInvestmentReport(writer, investment, investmentMovements.Count, sentPerAccount.GetValueOrDefault(investment.ExternalAccountId));
            writer.WriteEndArray();
            writer.WriteStartObject("transferPairs");
            WritePairCounts(writer, "investment", investmentPairs.Counts, "positionSideUnpaired");
            WritePairCounts(writer, "cardBillPayment", cardPairs.Counts, "cardSideUnpaired");
            writer.WriteEndObject();
            WriteConnector(writer, snapshot.Connector);
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
    /// One <c>accounts</c> row. A window of lines proves nothing about a deposit balance, so it is reported for the
    /// host to compare with its ledger. A card's reported balance is split into the settled balance and the pending
    /// amount. The settled balance is what the host's ledger holds: the reported amount minus the pending lines it
    /// includes, except pending bill payments the card has received, which count as applied because the host books
    /// a bill payment from the paying account's debit (<c>pendingBillPayments</c> says how much).
    /// </summary>
    private static void WriteAccountReport(Utf8JsonWriter writer, OpenFinanceAccountTransactions accountTransactions, int sent)
    {
        var (account, transactions) = accountTransactions;
        var isCard = account.Kind == OpenFinanceAccountKind.CreditCard;
        writer.WriteStartObject();
        writer.WriteString("kind", isCard ? "creditCard" : "deposit");
        writer.WriteString("externalAccountId", account.ExternalAccountId);
        writer.WriteNumber("lines", transactions.Count);
        writer.WriteNumber("posted", transactions.Count(t => !t.IsPending));
        writer.WriteNumber("sent", sent);
        if (!isCard && account.Balance is { } balance)
        {
            writer.WriteNumber("balance", balance);
            if (account.BalanceAt is { } balanceAt)
                writer.WriteString("balanceAtUtc", balanceAt.ToUniversalTime());
            writer.WriteString("balanceSide", "asset");
        }
        else if (isCard && account.Card is { } card)
        {
            var pendingLines = transactions.Where(t => t.IsPending).ToList();
            var pendingAmount = pendingLines.Sum(t => t.Direction == OpenFinanceDirection.Debit ? t.Amount : -t.Amount);
            var billPayments = pendingLines.Where(t => IsBillPayment(account, t)).ToList();
            var billPaymentAmount = billPayments.Sum(t => t.Amount);
            var withoutPending = card.IncludesPending ? card.Owed - pendingAmount : card.Owed;
            var settled = withoutPending - billPaymentAmount;
            writer.WriteNumber("balance", settled);
            if (account.BalanceAt is { } balanceAt)
                writer.WriteString("balanceAtUtc", balanceAt.ToUniversalTime());
            writer.WriteString("balanceSide", "liability");
            writer.WriteNumber("pendingAmount", pendingAmount);
            writer.WriteNumber("pendingLines", pendingLines.Count);
            writer.WriteNumber("balanceWithPending", withoutPending + pendingAmount);
            WriteOptionalNumber(writer, "creditLimit", card.CreditLimit);
            WriteOptionalNumber(writer, "availableCreditLimit", card.AvailableCreditLimit);
            WriteOptionalNumber(writer, "minimumPayment", card.MinimumPayment);
            WriteOptionalDate(writer, "billCloseDate", card.BillCloseDate);
            WriteOptionalDate(writer, "billDueDate", card.BillDueDate);
            writer.WriteNumber("pendingBillPayments", billPaymentAmount);
            writer.WriteNumber("pendingBillPaymentLines", billPayments.Count);
        }

        WriteOptionalString(writer, "institutionWebsite", account.InstitutionWebsite);
        // New keys follow the preview.4 ones (#1386).
        if (isCard && accountTransactions.Bills.Count > 0)
            WriteBills(writer, accountTransactions.Bills, transactions);
        writer.WriteEndObject();
    }

    /// <summary>
    /// A card's <c>bills</c> (#1386), oldest due date first: the issuer's numbers for each closed cycle, so the host can
    /// set its ledger against the invoice the holder has in hand. <c>postedLinesRead</c> counts the posted lines of this
    /// read that name the bill; a windowed read sees only part of them, so the host sums its own ledger instead.
    /// </summary>
    private static void WriteBills(Utf8JsonWriter writer, IReadOnlyList<OpenFinanceCardBill> bills, IReadOnlyList<OpenFinanceTransaction> transactions)
    {
        writer.WriteStartArray("bills");
        foreach (var bill in bills.OrderBy(b => b.DueDate ?? DateOnly.MaxValue).ThenBy(b => b.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("billId", bill.Id);
            WriteOptionalDate(writer, "closeDate", bill.CloseDate);
            WriteOptionalDate(writer, "dueDate", bill.DueDate);
            WriteOptionalNumber(writer, "totalAmount", bill.TotalAmount);
            WriteOptionalNumber(writer, "minimumPayment", bill.MinimumPayment);
            WriteOptionalString(writer, "currencyCode", bill.CurrencyCode);
            writer.WriteNumber("postedLinesRead", transactions.Count(t => !t.IsPending && string.Equals(t.CardBillId, bill.Id, StringComparison.Ordinal)));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>One <c>accounts</c> row of kind <c>investment</c>: a position, its balance and what it says about itself.</summary>
    private static void WriteInvestmentReport(Utf8JsonWriter writer, OpenFinanceInvestment investment, int movements, int sent)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "investment");
        writer.WriteString("externalAccountId", investment.ExternalAccountId);
        writer.WriteNumber("lines", movements);
        writer.WriteNumber("posted", movements);
        writer.WriteNumber("sent", sent);
        writer.WriteNumber("balance", investment.Balance);
        writer.WriteString("balanceAtUtc", investment.BalanceDate.ToUniversalTime());
        writer.WriteString("balanceSide", "asset");
        writer.WriteString("externalAccountIdBasis", investment.IdentityBasis);
        writer.WriteString("type", investment.Type);
        WriteOptionalString(writer, "subtype", investment.Subtype);
        WriteOptionalString(writer, "name", investment.Name);
        WriteOptionalString(writer, "issuer", investment.Issuer);
        WriteOptionalString(writer, "status", investment.Status);
        writer.WriteString("currencyCode", investment.CurrencyCode);
        WriteOptionalNumber(writer, "grossAmount", investment.GrossAmount);
        WriteOptionalNumber(writer, "amountInvested", investment.AmountInvested);
        WriteOptionalNumber(writer, "profit", investment.Profit);
        WriteOptionalNumber(writer, "incomeTax", investment.IncomeTax);
        WriteOptionalNumber(writer, "financialTax", investment.FinancialTax);
        WriteOptionalDate(writer, "issueDate", investment.IssueDate);
        WriteOptionalDate(writer, "dueDate", investment.DueDate);
        WriteOptionalString(writer, "institutionWebsite", investment.InstitutionWebsite);
        writer.WriteEndObject();
    }

    private static void WritePairCounts(Utf8JsonWriter writer, string name, OpenFinancePairing.Counts counts, string otherSideName)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("lines", counts.Lines);
        writer.WriteNumber("paired", counts.Paired);
        writer.WriteNumber("ambiguous", counts.Ambiguous);
        writer.WriteNumber("unmatched", counts.Unmatched);
        writer.WriteNumber(otherSideName, counts.OtherSideUnpaired);
        writer.WriteEndObject();
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is { } number)
            writer.WriteNumber(name, number);
    }

    private static void WriteOptionalDate(Utf8JsonWriter writer, string name, DateOnly? value)
    {
        if (value is { } date)
            writer.WriteString(name, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            writer.WriteString(name, value.Trim());
    }

    /// <summary>A bill payment the card received, by the same rule as the bill-payment pairs (<see cref="OpenFinanceTransactionMapper.TransferHint"/>).</summary>
    private static bool IsBillPayment(OpenFinanceAccount card, OpenFinanceTransaction transaction) =>
        card.Kind == OpenFinanceAccountKind.CreditCard
        && transaction.Direction == OpenFinanceDirection.Credit
        && OpenFinanceTransactionMapper.TransferHint(card, transaction) == OpenFinanceTransactionMapper.CardBillPaymentHint;

    private sealed record PreparedLine(OpenFinanceAccount Account, OpenFinanceTransaction Transaction, int Ordinal, string Key, string? Hint, bool IsSent);

    private sealed record PreparedMovement(OpenFinanceInvestment Investment, OpenFinanceInvestmentMovement Movement, int Ordinal, string? CashKey, bool IsSent);

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

    /// <summary>
    /// The connector the item reads through (#1379), as <c>connector</c>: <c>id</c>, <c>name</c>, <c>isBusiness</c>,
    /// <c>iconUrl</c>, <c>institutionWebsite</c>. Hosts read it to name and mark the connection; absent when unknown.
    /// </summary>
    internal static void WriteConnector(Utf8JsonWriter writer, IntegrationConnector? connector)
    {
        if (connector is null)
            return;
        writer.WriteStartObject("connector");
        if (connector.Id is { } id)
            writer.WriteString("id", id);
        writer.WriteString("name", connector.Name);
        writer.WriteBoolean("isBusiness", connector.IsBusiness);
        if (connector.IconUrl is { } icon)
            writer.WriteString("iconUrl", icon.AbsoluteUri);
        if (connector.InstitutionWebsite is { } website)
            writer.WriteString("institutionWebsite", website.AbsoluteUri);
        writer.WriteEndObject();
    }

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
