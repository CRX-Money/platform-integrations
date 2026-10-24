using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br;
using CrxMoney.Integrations.Providers.Br.OpenFinance;
using CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy.Api;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance.Pluggy;

/// <summary>
/// Reads a Pluggy item and normalises it into the Open Finance model. Everything Pluggy-specific lives
/// here: credentials (<c>ApiKey</c> = client id, <c>ApiSecret</c> = client secret, <c>Passphrase</c> = item id),
/// item states, account identities, sign conventions and field names.
/// </summary>
internal sealed class PluggyOpenFinanceSource(PluggyApiClient api, TimeProvider timeProvider) : IOpenFinanceSource
{
    public string SourceName => PluggyAdapter.Name;

    public IntegrationConnectionAdapterSettings? GetConnectionSettings(string? culture) => PluggyConnectionSettings.Adapter(culture);

    public async Task<OpenFinanceConnectionCheck> CheckConnectionAsync(IntegrationSyncRequest request, CancellationToken cancellationToken)
    {
        var (credentials, itemId) = ParseCredentials(request);
        var item = await api.GetItemAsync(credentials, itemId, cancellationToken).ConfigureAwait(false);
        EnsureUsable(item);

        var institution = item.Connector?.Name?.Trim();
        var summary = $"Pluggy item {item.Status}" + (string.IsNullOrEmpty(institution) ? string.Empty : $" via {institution}")
            + (item.LastUpdatedAt is { } updated ? $", last refreshed {updated.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : string.Empty) + ".";
        var accounts = await AccountsForCheckAsync(credentials, itemId, cancellationToken).ConfigureAwait(false);
        var compes = accounts
            .Select(a => BrazilianBankAccount.TryParseTransferNumber(a.BankData?.TransferNumber)?.Compe)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new OpenFinanceConnectionCheck(summary, item.ConsentExpiresAt, ItemDiagnostics(item))
        {
            LastRefreshedAt = item.LastUpdatedAt,
            IsSandbox = item.Connector?.IsSandbox,
            AccountOperatorCompes = compes,
            Connector = ResolveConnector(item.Connector, BankAccounts(accounts)),
        };
    }

    /// <summary>
    /// The item's accounts, for the COMPE codes of its deposit accounts (the account operator) and their holder. With
    /// Meu Pluggy the connector is Meu Pluggy itself, never the bank, so the accounts are the only place the bank shows.
    /// Best effort: a test whose item read worked does not fail because the accounts could not be listed.
    /// </summary>
    private async Task<IReadOnlyList<PluggyAccount>> AccountsForCheckAsync(PluggyCredentials credentials, Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            return await api.GetAccountsAsync(credentials, itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenFinanceSourceException)
        {
            return [];
        }
    }

    /// <summary>An item belongs to one holder; its bank account carries the holder's CPF or CNPJ.</summary>
    private static BrazilianTaxId? HolderOf(IEnumerable<PluggyAccount> accounts) =>
        BankAccounts(accounts)
            .Select(a => BrazilianTaxId.TryParse(a.TaxNumber))
            .FirstOrDefault(t => t is not null);

    /// <summary>The item's deposit accounts: the ones that name the bank (COMPE) and the holder (tax id).</summary>
    private static IReadOnlyList<PluggyAccount> BankAccounts(IEnumerable<PluggyAccount> accounts) =>
        accounts.Where(a => string.Equals(a.Type, "BANK", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The connector the item reads through (#1379). An item connected directly names its bank's connector (Inter or
    /// Inter Empresas, with its type and icon). An item through Meu Pluggy names the MeuPluggy connector, so the bank
    /// of its accounts and their holder pick it from the country package: a company's accounts (CNPJ) read through the
    /// bank's business connector. Only when every bank account agrees on the bank and on the kind of holder (all CPF or
    /// all CNPJ), and the package lists a connector for it; otherwise null, and the host keeps the institution's main
    /// connector: no guess.
    /// </summary>
    internal static IntegrationConnector? ResolveConnector(PluggyConnector? itemConnector, IReadOnlyCollection<PluggyAccount> bankAccounts)
    {
        var compes = bankAccounts
            .Select(a => BrazilianBankAccount.TryParseTransferNumber(a.BankData?.TransferNumber)?.Compe)
            .ToList();
        var holders = bankAccounts.Select(a => BrazilianTaxId.TryParse(a.TaxNumber)).ToList();
        var compe = compes.Count > 0 && compes.All(c => c is not null) && compes.Distinct(StringComparer.Ordinal).Count() == 1 ? compes[0] : null;
        var holderKind = holders.Count > 0 && holders.All(h => h is not null) && holders.Select(h => h!.Value.Kind).Distinct(StringComparer.Ordinal).Count() == 1
            ? holders[0]!.Value.Kind
            : null;
        var institution = BrazilianInstitutions.Find(compe);
        if (itemConnector is { Id: { } id, Name: { } name } && !string.IsNullOrWhiteSpace(name) && !IsMeuPluggy(itemConnector))
        {
            var connectorId = id.ToString(CultureInfo.InvariantCulture);
            var known = BrazilianInstitutionConnectors.Find(connectorId);
            return new IntegrationConnector(name.Trim())
            {
                Id = connectorId,
                IsBusiness = IsBusinessType(itemConnector.Type) || known?.IsBusiness == true,
                IconUrl = Uri.TryCreate(itemConnector.ImageUrl, UriKind.Absolute, out var icon) && icon.Scheme == Uri.UriSchemeHttps ? icon : null,
                InstitutionWebsite = known?.Institution.Website ?? institution?.Website,
            };
        }

        if (institution is null || holderKind is null)
            return null;

        var isBusiness = holderKind == BrazilianTaxId.Cnpj;
        return BrazilianInstitutionConnectors.For(institution.Compe, isBusiness) is { } listed
            ? new IntegrationConnector(listed.Name) { Id = listed.Id, IsBusiness = listed.IsBusiness, InstitutionWebsite = institution.Website }
            : null;
    }

    /// <summary>Pluggy's own connector of the Meu Pluggy consent app (status page type <c>pluggy</c>), not a bank's.</summary>
    private static bool IsMeuPluggy(PluggyConnector connector) =>
        string.Equals(connector.Type, "pluggy", StringComparison.OrdinalIgnoreCase)
        || string.Equals(new string((connector.Name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()), "MeuPluggy", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pluggy's API types business connectors <c>BUSINESS_BANK</c>; its status page, <c>business</c>.</summary>
    private static bool IsBusinessType(string? type) =>
        type is not null && type.Contains("BUSINESS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Days requested before the plugin's read date. Pluggy filters <c>dateFrom</c> on its own <c>date</c> field,
    /// which is a UTC instant (card lines: midnight in Brazil, 03:00Z), while the plugin works in Brazil's
    /// calendar from <c>creditCardMetadata.transactionDateTime</c> when present. One day more covers both
    /// readings; the plugin drops the extra lines.
    /// </summary>
    internal const int DateFromMarginDays = 1;

    /// <remarks>
    /// The window is <c>dateFrom</c>, by transaction date, not <c>createdAtFrom</c>, by when Pluggy stored the line:
    /// a date window always holds every line of a booking date, so lines that are identical but for their ordinal
    /// are numbered as in a full read and keep their keys. A <c>createdAtFrom</c> read can hold only the later of two
    /// identical lines, which would then take the first one's key and be dropped by the host as a duplicate.
    /// </remarks>
    public async Task<OpenFinanceSnapshot> ReadAsync(IntegrationSyncRequest request, DateOnly? readFrom, CancellationToken cancellationToken)
    {
        var (credentials, itemId) = ParseCredentials(request);
        var item = await api.GetItemAsync(credentials, itemId, cancellationToken).ConfigureAwait(false);
        EnsureUsable(item);

        var accounts = await api.GetAccountsAsync(credentials, itemId, cancellationToken).ConfigureAwait(false);
        // Pluggy gives cards no issuer code; a card belongs to the bank of the item's deposit account.
        var bankAccounts = accounts.Where(a => string.Equals(a.Type, "BANK", StringComparison.OrdinalIgnoreCase)).ToList();
        var issuerCompe = bankAccounts
            .Select(a => BrazilianBankAccount.TryParseTransferNumber(a.BankData?.TransferNumber)?.Compe)
            .FirstOrDefault(c => c is not null);
        var holder = HolderOf(bankAccounts);

        DateOnly? dateFrom = readFrom?.AddDays(-DateFromMarginDays);
        // A card's balance includes its pending purchases, which can be as old as the open bill: read them all.
        DateOnly? cardDateFrom = dateFrom is { } windowFrom
            ? Min(windowFrom, BrazilBankingCalendar.DateOf(timeProvider.GetUtcNow()).AddDays(-CardPendingLookbackDays))
            : null;
        var result = new List<OpenFinanceAccountTransactions>();
        var pages = 0;
        int billPages = 0, billsUnread = 0;
        foreach (var account in accounts)
        {
            var normalized = NormalizeAccount(account, issuerCompe);
            // Balances are as of Pluggy's last refresh of the item from the bank.
            var hasBalance = normalized.Balance is not null || normalized.Card is not null;
            normalized = normalized with { HolderTaxId = holder, BalanceAt = hasBalance ? item.LastUpdatedAt : null };
            var from = normalized.Kind == OpenFinanceAccountKind.CreditCard ? cardDateFrom : dateFrom;
            var read = await api.GetTransactionsAsync(credentials, account.Id!, from, cancellationToken).ConfigureAwait(false);
            pages += read.Pages;
            IReadOnlyList<OpenFinanceCardBill> bills = [];
            if (normalized.Kind == OpenFinanceAccountKind.CreditCard)
            {
                var billRead = await ReadBillsAsync(credentials, account.Id!, cancellationToken).ConfigureAwait(false);
                billPages += billRead.Pages;
                if (billRead.Bills is null)
                    billsUnread++;
                bills = billRead.Bills ?? [];
            }

            result.Add(new OpenFinanceAccountTransactions(normalized, read.Lines.Select(l => NormalizeTransaction(normalized, l)).ToList())
            {
                Bills = bills
            });
        }

        var investments = await ReadInvestmentsAsync(credentials, itemId, item, issuerCompe, cancellationToken).ConfigureAwait(false);

        return new OpenFinanceSnapshot(result, item.ConsentExpiresAt, ItemDiagnostics(item, writer =>
        {
            if (dateFrom is { } windowFrom)
                writer.WriteString("transactionsDateFrom", windowFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (cardDateFrom is { } cardFrom && result.Any(a => a.Account.Kind == OpenFinanceAccountKind.CreditCard))
                writer.WriteString("cardTransactionsDateFrom", cardFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteNumber("transactionPages", pages);
            if (result.Any(a => a.Account.Kind == OpenFinanceAccountKind.CreditCard))
            {
                writer.WriteNumber("billPages", billPages);
                writer.WriteNumber("cardsWithoutBills", billsUnread);
            }
            investments.WriteDiagnostics(writer);
        }))
        {
            Investments = investments.Positions,
            Connector = ResolveConnector(item.Connector, bankAccounts),
        };
    }

    /// <summary>
    /// Days of card lines read before today on a windowed sync, so every pending purchase of the open bill is seen.
    /// Pluggy marks a card purchase pending until a bill closes on it; two cycles leave room for a late closing.
    /// </summary>
    internal const int CardPendingLookbackDays = 62;

    /// <summary>The identity basis of a position known only by Pluggy's id.</summary>
    internal const string SourceIdBasis = "pluggy";

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    /// <summary>
    /// A card's bills from <c>GET /bills?accountId=</c> (#1386). Best effort: a bills read that fails (the product
    /// missing from the item, an upstream error) leaves the card without bills (<c>null</c>) instead of failing the
    /// sync; a bill without an id is skipped.
    /// </summary>
    private async Task<(IReadOnlyList<OpenFinanceCardBill>? Bills, int Pages)> ReadBillsAsync(PluggyCredentials credentials, string accountId, CancellationToken cancellationToken)
    {
        PluggyNumberedRead read;
        try
        {
            read = await api.GetBillsAsync(credentials, accountId, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenFinanceSourceException)
        {
            return (null, 0);
        }

        return (read.Results.Select(NormalizeBill).OfType<OpenFinanceCardBill>().ToList(), read.Pages);
    }

    /// <summary>
    /// One Pluggy bill. Due and closing dates are calendar dates Pluggy sends as midnight UTC instants, so the date part
    /// is the date. Pluggy documents no closing date; one is read when a bill carries it.
    /// </summary>
    internal static OpenFinanceCardBill? NormalizeBill(JsonElement bill)
    {
        if (bill.ValueKind != JsonValueKind.Object || FirstNonEmpty(BillString(bill, "id")) is not { } id)
            return null;

        return new OpenFinanceCardBill(
            id,
            BillDate(bill, "dueDate"),
            BillDecimal(bill, "totalAmount") is { } total ? Math.Abs(total) : null,
            BillDecimal(bill, "minimumPaymentAmount") is { } minimum ? Math.Abs(minimum) : null,
            BillDate(bill, "closeDate") ?? BillDate(bill, "closingDate") ?? BillDate(bill, "billCloseDate"),
            FirstNonEmpty(BillString(bill, "totalAmountCurrencyCode"))?.ToUpperInvariant());
    }

    private static string? BillString(JsonElement bill, string name) =>
        bill.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? BillDecimal(JsonElement bill, string name)
    {
        if (!bill.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
               && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static DateOnly? BillDate(JsonElement bill, string name) =>
        BillString(bill, name) is { Length: >= 10 } text
        && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>The positions of the item and what reading them took, for the diagnostics.</summary>
    private sealed record InvestmentsRead(IReadOnlyList<OpenFinanceInvestmentMovements> Positions, string? SkippedReason, int Pages, int MovementPages, int MovementsSkipped)
    {
        public void WriteDiagnostics(Utf8JsonWriter writer)
        {
            writer.WriteNumber("investments", Positions.Count);
            if (SkippedReason is not null)
                writer.WriteString("investmentsSkipped", SkippedReason);
            writer.WriteNumber("investmentPages", Pages);
            writer.WriteNumber("investmentTransactionPages", MovementPages);
            writer.WriteNumber("investmentMovementsSkipped", MovementsSkipped);
            // Flat like the other facts: "number:2,pluggy:1".
            writer.WriteString("investmentIdBases", string.Join(',', Positions
                .GroupBy(p => p.Investment.IdentityBasis)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}:{g.Count()}")));
        }
    }

    /// <summary>
    /// Reads <c>/investments</c> and each position's <c>/investments/{id}/transactions</c> when the item has the
    /// INVESTMENTS (and INVESTMENTS_TRANSACTIONS) products. A position is held at the item's bank, so its id needs that
    /// bank's COMPE; without a deposit account in the item, positions are skipped and the diagnostics say why.
    /// </summary>
    private async Task<InvestmentsRead> ReadInvestmentsAsync(PluggyCredentials credentials, Guid itemId, PluggyItem item, string? compe, CancellationToken cancellationToken)
    {
        var products = item.Products ?? [];
        bool Has(string product) => products.Contains(product, StringComparer.OrdinalIgnoreCase);
        if (!Has("INVESTMENTS"))
            return new InvestmentsRead([], "not an item product", 0, 0, 0);
        if (compe is null)
            return new InvestmentsRead([], "no deposit account to identify the institution", 0, 0, 0);

        PluggyNumberedRead listing;
        try
        {
            listing = await api.GetInvestmentsAsync(credentials, itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenFinanceSourceException ex) when (ex.Kind == IntegrationErrorKind.Permission)
        {
            return new InvestmentsRead([], "not allowed for this application", 0, 0, 0);
        }

        var positions = listing.Results.Select(r => (Raw: r, Wire: Deserialize(r, PluggyJsonContext.Default.PluggyInvestment, "investment"))).ToList();
        var identities = InvestmentIdentities(positions.Select(p => p.Wire).ToList(), compe);
        var readMovements = Has("INVESTMENTS_TRANSACTIONS");
        var result = new List<OpenFinanceInvestmentMovements>();
        int movementPages = 0, skipped = 0;
        foreach (var (raw, wire) in positions)
        {
            var investment = NormalizeInvestment(wire, raw, compe, identities[wire.Id!.Trim()]);
            var movements = new List<OpenFinanceInvestmentMovement>();
            if (readMovements)
            {
                var read = await api.GetInvestmentTransactionsAsync(credentials, investment.SourceInvestmentId, cancellationToken).ConfigureAwait(false);
                movementPages += read.Pages;
                foreach (var line in read.Results)
                {
                    if (NormalizeInvestmentMovement(line) is { } movement)
                        movements.Add(movement);
                    else
                        skipped++;
                }
            }

            result.Add(new OpenFinanceInvestmentMovements(investment, movements));
        }

        return new InvestmentsRead(result, readMovements ? null : "movements: not an item product", listing.Pages, movementPages, skipped);
    }

    /// <summary>
    /// The identity of each position, best first: the institution's reference for the holder's position
    /// (<c>number</c>), the <c>isin</c>, the <c>code</c> of a fund, ETF or stock (its CNPJ or ticker), and only then
    /// Pluggy's own id. Positions that would share an identity fall back to Pluggy's id.
    /// </summary>
    internal static IReadOnlyDictionary<string, (string Basis, string Key)> InvestmentIdentities(IReadOnlyList<PluggyInvestment> investments, string compe)
    {
        static bool Usable(string? value) => value is not null && value.Any(char.IsAsciiLetterOrDigit);

        var chosen = new Dictionary<string, (string Basis, string Key)>(StringComparer.Ordinal);
        foreach (var investment in investments)
        {
            var id = investment.Id?.Trim();
            if (string.IsNullOrEmpty(id))
                throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, "Pluggy returned an investment without an id.");
            if (chosen.ContainsKey(id))
                throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, "Pluggy returned the same investment twice.");

            var type = investment.Type?.Trim().ToUpperInvariant();
            chosen[id] = Usable(investment.Number) ? ("number", investment.Number!)
                : investment.Isin?.Trim() is { Length: 12 } isin && Usable(isin) ? ("isin", isin)
                : type is "MUTUAL_FUND" or "ETF" or "EQUITY" && Usable(investment.Code) ? ("code", investment.Code!)
                : (SourceIdBasis, id);
        }

        var collisions = chosen
            .GroupBy(c => BrazilianBankAccount.InvestmentExternalAccountId(compe, c.Value.Basis, c.Value.Key), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(c => c.Key))
            .ToList();
        foreach (var id in collisions)
            chosen[id] = (SourceIdBasis, id);
        return chosen;
    }

    internal static OpenFinanceInvestment NormalizeInvestment(PluggyInvestment investment, JsonElement raw, string compe, (string Basis, string Key) identity)
    {
        var id = investment.Id!.Trim();
        var balance = investment.Balance
            ?? throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy investment {id} has no balance.");
        var date = investment.Date
            ?? throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy investment {id} has no balance date.");
        var currency = string.IsNullOrWhiteSpace(investment.CurrencyCode) ? "BRL" : investment.CurrencyCode.Trim().ToUpperInvariant();
        return new OpenFinanceInvestment(
            id,
            compe,
            identity.Basis,
            identity.Key,
            FirstNonEmpty(investment.Type)?.ToUpperInvariant() ?? "OTHER",
            FirstNonEmpty(investment.Subtype)?.ToUpperInvariant(),
            FirstNonEmpty(investment.Name) ?? "Investment",
            currency,
            balance,
            date)
        {
            Issuer = FirstNonEmpty(investment.Issuer),
            IssuerTaxId = BrazilianTaxId.TryParse(investment.IssuerCnpj),
            Isin = FirstNonEmpty(investment.Isin),
            Status = FirstNonEmpty(investment.Status)?.ToUpperInvariant(),
            GrossAmount = investment.Amount,
            AmountInvested = investment.AmountOriginal,
            Profit = investment.AmountProfit,
            IncomeTax = investment.Taxes,
            FinancialTax = investment.Taxes2,
            IssueDate = investment.IssueDate is { } issued ? BrazilBankingCalendar.DateOf(issued) : null,
            DueDate = investment.DueDate is { } due ? BrazilBankingCalendar.DateOf(due) : null,
            RateType = FirstNonEmpty(investment.RateType),
            Rate = investment.Rate,
            SourcePayload = raw,
        };
    }

    /// <summary>
    /// Normalises one investment transaction, or returns <c>null</c> for one that moves no known amount (a
    /// <c>TRANSFER</c> without amount, which Pluggy allows, or a type it does not document).
    /// </summary>
    internal static OpenFinanceInvestmentMovement? NormalizeInvestmentMovement(JsonElement line)
    {
        var movement = Deserialize(line, PluggyJsonContext.Default.PluggyInvestmentTransaction, "investment transaction");
        var id = movement.Id?.Trim();
        if (string.IsNullOrEmpty(id))
            throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, "Pluggy returned an investment transaction without an id.");
        var time = movement.Date
            ?? throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy investment transaction {id} has no date.");

        var credit = string.Equals(movement.MovementType, "CREDIT", StringComparison.OrdinalIgnoreCase);
        OpenFinanceInvestmentOperation? operation = movement.Type?.Trim().ToUpperInvariant() switch
        {
            "BUY" => OpenFinanceInvestmentOperation.Application,
            "SELL" => OpenFinanceInvestmentOperation.Redemption,
            "AMORTIZATION" => OpenFinanceInvestmentOperation.Amortization,
            "INTEREST" => OpenFinanceInvestmentOperation.Income,
            "TAX" => OpenFinanceInvestmentOperation.Tax,
            "TRANSFER" => credit ? OpenFinanceInvestmentOperation.TransferIn : OpenFinanceInvestmentOperation.TransferOut,
            _ => null,
        };
        if (operation is null || movement.Amount is not { } amount || amount == 0m)
            return null;

        var e = movement.Expenses;
        static decimal Abs(decimal? value) => Math.Abs(value ?? 0m);
        var expenses = e is null
            ? OpenFinanceInvestmentExpenses.None
            : new OpenFinanceInvestmentExpenses(
                Abs(e.IncomeTax),
                Abs(e.Iof),
                Abs(e.ServiceTax) + Abs(e.BrokerageFee) + Abs(e.TradingAssetsNoticeFee) + Abs(e.MaintenanceFee) + Abs(e.SettlementFee)
                    + Abs(e.ClearingFee) + Abs(e.StockExchangeFee) + Abs(e.CustodyFee) + Abs(e.OperatingFee) + Abs(e.Other));

        return new OpenFinanceInvestmentMovement(id, time, operation.Value, Math.Abs(amount), expenses, FirstNonEmpty(movement.Description), line)
        {
            // Pluggy: movementType DEBIT (its default for INTEREST) is money leaving the position, i.e. income paid out.
            PaidOut = operation == OpenFinanceInvestmentOperation.Income && !credit,
            SettlementTime = movement.TradeDate,
            ReportedNetAmount = movement.NetAmount,
        };
    }

    private static T Deserialize<T>(JsonElement element, JsonTypeInfo<T> typeInfo, string what)
        where T : class
    {
        try
        {
            return element.Deserialize(typeInfo)
                ?? throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy returned an empty {what}.");
        }
        catch (JsonException ex)
        {
            throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy returned an {what} in an unexpected shape.", ex);
        }
    }

    internal static (PluggyCredentials Credentials, Guid ItemId) ParseCredentials(IntegrationSyncRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey) || string.IsNullOrWhiteSpace(request.ApiSecret))
            throw new OpenFinanceSourceException(IntegrationErrorKind.Configuration, "The Pluggy client id and client secret are required.");
        if (!Guid.TryParse(request.Passphrase?.Trim(), out var itemId))
            throw new OpenFinanceSourceException(IntegrationErrorKind.Configuration, "The Pluggy item id is missing or is not a UUID.");
        return (new PluggyCredentials(request.ApiKey.Trim(), request.ApiSecret.Trim()), itemId);
    }

    /// <summary>Fails fast on item states whose data cannot be trusted or read.</summary>
    private void EnsureUsable(PluggyItem item)
    {
        if (item.ConsentExpiresAt is { } expiresAt && expiresAt <= timeProvider.GetUtcNow())
        {
            throw new OpenFinanceSourceException(IntegrationErrorKind.Authentication,
                $"The Open Finance consent expired on {expiresAt:yyyy-MM-dd}. Reconnect the bank in Meu Pluggy to renew it.");
        }

        switch (item.Status?.ToUpperInvariant())
        {
            case "UPDATED":
            case "UPDATING":
                return;
            case "LOGIN_ERROR":
                throw new OpenFinanceSourceException(IntegrationErrorKind.Authentication,
                    "Pluggy cannot log in to the bank for this item. Reconnect the bank to renew the consent.");
            case "WAITING_USER_INPUT":
            case "WAITING_USER_ACTION":
                throw new OpenFinanceSourceException(IntegrationErrorKind.Configuration,
                    "The Pluggy item is waiting for the account holder to complete the connection.");
            case "OUTDATED":
                throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream,
                    $"Pluggy could not refresh this item{(item.LastUpdatedAt is { } at ? $" since {at:yyyy-MM-dd}" : string.Empty)}{(item.Error?.Code is { } code ? $" ({code})" : string.Empty)}; its data is stale.");
            default:
                throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"The Pluggy item is in an unexpected state '{item.Status}'.");
        }
    }

    internal static OpenFinanceAccount NormalizeAccount(PluggyAccount account, string? issuerCompe)
    {
        if (string.IsNullOrWhiteSpace(account.Id))
            throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, "Pluggy returned an account without an id.");

        var currency = string.IsNullOrWhiteSpace(account.CurrencyCode) ? "BRL" : account.CurrencyCode.Trim().ToUpperInvariant();
        switch (account.Type?.ToUpperInvariant())
        {
            case "BANK":
                var bankAccount = BrazilianBankAccount.TryParseTransferNumber(account.BankData?.TransferNumber)
                    ?? throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream,
                        "Pluggy returned a bank account without its COMPE/branch/account identity (bankData.transferNumber).");
                return new OpenFinanceAccount(account.Id, OpenFinanceAccountKind.Deposit, bankAccount, bankAccount.Compe, null, currency, account.Balance);
            case "CREDIT":
                var last4 = new string((account.Number ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
                if (last4.Length < 4 || issuerCompe is null)
                {
                    throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream,
                        "Pluggy returned a card that cannot be identified (no last four digits, or no deposit account of the same bank in the item).");
                }

                // On Open Finance connectors Pluggy's card balance is the used limit: posted and pending purchases.
                return new OpenFinanceAccount(account.Id, OpenFinanceAccountKind.CreditCard, null, issuerCompe, last4[^4..], currency, null)
                {
                    Card = account.Balance is { } owed
                        ? new OpenFinanceCardBalance(
                            owed,
                            IncludesPending: true,
                            account.CreditData?.CreditLimit,
                            account.CreditData?.AvailableCreditLimit,
                            account.CreditData?.MinimumPayment,
                            DatePart(account.CreditData?.BalanceCloseDate),
                            DatePart(account.CreditData?.BalanceDueDate))
                        : null,
                };
            default:
                throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy returned an account of unknown type '{account.Type}'.");
        }
    }

    internal static OpenFinanceTransaction NormalizeTransaction(OpenFinanceAccount account, JsonElement line)
    {
        PluggyTransaction transaction;
        try
        {
            transaction = line.Deserialize(PluggyJsonContext.Default.PluggyTransaction)
                ?? throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, "Pluggy returned an empty transaction.");
        }
        catch (JsonException ex)
        {
            throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, "Pluggy returned a transaction in an unexpected shape.", ex);
        }

        var id = transaction.Id?.Trim();
        if (string.IsNullOrEmpty(id))
            throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, "Pluggy returned a transaction without an id.");

        // Pluggy's sign depends on the product (card purchases are positive); the type is reliable.
        var direction = transaction.Type?.ToUpperInvariant() switch
        {
            "CREDIT" => OpenFinanceDirection.Credit,
            "DEBIT" => OpenFinanceDirection.Debit,
            _ => throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy transaction {id} has type '{transaction.Type}', expected CREDIT or DEBIT."),
        };
        var amount = transaction.Amount is { } value
            ? Math.Abs(value)
            : throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy transaction {id} has no amount.");
        var time = transaction.CreditCardMetadata?.TransactionDateTime ?? transaction.Date
            ?? throw new OpenFinanceSourceException(IntegrationErrorKind.Upstream, $"Pluggy transaction {id} has no date.");

        var isCard = account.Kind == OpenFinanceAccountKind.CreditCard;
        var type = isCard ? "CARD" : NormalizeType(transaction.OperationType, transaction.PaymentData?.PaymentMethod);
        var description = FirstNonEmpty(transaction.DescriptionRaw, transaction.Description);

        return new OpenFinanceTransaction(
            SourceTransactionId: id,
            TransactionTime: time,
            Direction: direction,
            Amount: amount,
            Type: type,
            TypeDetail: FirstNonEmpty(transaction.OperationTypeAdditionalInfo),
            Description: description,
            Counterparty: Counterparty(transaction, direction, isCard),
            Pix: null, // Pluggy's v2 lines carry no Pix end-to-end id or key.
            IsPending: string.Equals(transaction.Status, "PENDING", StringComparison.OrdinalIgnoreCase),
            CardBillId: FirstNonEmpty(transaction.CreditCardMetadata?.BillId),
            SourcePayload: line)
        {
            CardDetails = isCard ? NormalizeCardDetails(transaction.CreditCardMetadata) : null,
        };
    }

    /// <summary>
    /// The card line's fields beyond the bill: <c>paymentType</c> (<c>SINGLE</c>/<c>INSTALLMENT</c>, Pluggy's names for
    /// Open Finance's <c>A_VISTA</c>/<c>A_PRAZO</c>), <c>installmentNumber</c> and <c>totalInstallments</c>
    /// (<c>chargeIdentificator</c>/<c>chargeNumber</c>), <c>purchaseDate</c>, <c>totalAmount</c> (direct connectors only),
    /// <c>billPostDate</c>, <c>billForecastDate</c> and <c>payeeMCC</c>. Null when the line has none of them.
    /// </summary>
    internal static OpenFinanceCardLineDetails? NormalizeCardDetails(PluggyCardMetadata? metadata)
    {
        if (metadata is null)
            return null;

        var details = new OpenFinanceCardLineDetails
        {
            PaymentType = FirstNonEmpty(metadata.PaymentType)?.ToUpperInvariant(),
            InstallmentNumber = metadata.InstallmentNumber,
            InstallmentCount = metadata.TotalInstallments,
            PurchaseDate = CalendarDate(metadata.PurchaseDate),
            TotalAmount = metadata.TotalAmount is { } total ? Math.Abs(total) : null,
            BillPostDate = DatePart(metadata.BillPostDate),
            BillForecastDate = DatePart(metadata.BillForecastDate),
            PayeeMcc = MerchantCategoryCode(metadata.PayeeMcc),
        };
        return details == new OpenFinanceCardLineDetails() ? null : details;
    }

    /// <summary>
    /// A date Pluggy sends as <c>yyyy-mm-dd</c> or as a date-time: a date-time at midnight UTC is a date written as a
    /// date-time and keeps its date part; any other instant is dated in Brazil's calendar, like <c>transactionDateTime</c>.
    /// </summary>
    internal static DateOnly? CalendarDate(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length == 10)
            return DatePart(text);
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            return null;
        return instant.UtcDateTime.TimeOfDay == TimeSpan.Zero
            ? DateOnly.FromDateTime(instant.UtcDateTime)
            : BrazilBankingCalendar.DateOf(instant);
    }

    /// <summary>The payee's MCC as four digits, from a number or a string; null when it is not one.</summary>
    internal static string? MerchantCategoryCode(JsonElement? value)
    {
        string? digits = value?.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetInt32(out var number) && number is >= 0 and <= 9999 => number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.Value.GetString()?.Trim(),
            _ => null,
        };
        return digits is { Length: >= 1 and <= 4 } && digits.All(char.IsAsciiDigit) ? digits.PadLeft(4, '0') : null;
    }

    private static string NormalizeType(string? operationType, string? paymentMethod)
    {
        var type = FirstNonEmpty(operationType, paymentMethod)?.ToUpperInvariant();
        return type switch
        {
            null => "OUTROS",
            "OTHER" => "OUTROS",
            _ => type,
        };
    }

    /// <summary>The payer of a credit or the payee of a debit; for a card, the merchant.</summary>
    private static OpenFinanceParty? Counterparty(PluggyTransaction transaction, OpenFinanceDirection direction, bool isCard)
    {
        var merchant = transaction.Merchant;
        if (isCard)
        {
            return merchant is null
                ? null
                : Party(FirstNonEmpty(merchant.BusinessName, merchant.Name), merchant.Cnpj, null, null, null);
        }

        var party = direction == OpenFinanceDirection.Credit ? transaction.PaymentData?.Payer : transaction.PaymentData?.Receiver;
        var result = party is null
            ? null
            : Party(party.Name, party.DocumentNumber?.Value, party.RoutingNumberIspb, party.BranchNumber, party.AccountNumber);
        if (result is { Name: null } && merchant is not null)
            result = result with { Name = FirstNonEmpty(merchant.BusinessName, merchant.Name) };
        return result ?? (merchant is null ? null : Party(FirstNonEmpty(merchant.BusinessName, merchant.Name), merchant.Cnpj, null, null, null));
    }

    private static OpenFinanceParty? Party(string? name, string? document, string? ispb, string? branch, string? account)
    {
        var party = new OpenFinanceParty(FirstNonEmpty(name), BrazilianTaxId.TryParse(document), FirstNonEmpty(ispb), FirstNonEmpty(branch), FirstNonEmpty(account));
        return party.IsEmpty ? null : party;
    }

    /// <summary>The <c>yyyy-mm-dd</c> part of a date Pluggy sends either as a date or as a date-time.</summary>
    internal static DateOnly? DatePart(string? value) =>
        value?.Trim() is { Length: >= 10 } text
        && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string ItemDiagnostics(PluggyItem item, Action<Utf8JsonWriter>? readFacts = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("itemStatus", item.Status);
            writer.WriteString("executionStatus", item.ExecutionStatus);
            if (item.LastUpdatedAt is { } updated)
                writer.WriteString("lastUpdatedAtUtc", updated.ToUniversalTime());
            if (item.Connector?.IsOpenFinance is { } isOpenFinance)
                writer.WriteBoolean("isOpenFinance", isOpenFinance);
            if (item.Connector?.IsSandbox is { } isSandbox)
                writer.WriteBoolean("isSandbox", isSandbox);
            writer.WriteString("products", string.Join(',', item.Products ?? []));
            readFacts?.Invoke(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
