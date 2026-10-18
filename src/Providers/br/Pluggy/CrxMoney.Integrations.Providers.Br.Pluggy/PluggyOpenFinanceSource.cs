using System.Text;
using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br;
using CrxMoney.Integrations.Countries.Br.OpenFinance;
using CrxMoney.Integrations.Providers.Br.Pluggy.Api;

namespace CrxMoney.Integrations.Providers.Br.Pluggy;

/// <summary>
/// Reads a Pluggy item and normalises it into the Open Finance model. Everything Pluggy-specific lives
/// here: credentials (<c>ApiKey</c> = client id, <c>ApiSecret</c> = client secret, <c>Passphrase</c> = item id),
/// item states, account identities, sign conventions and field names.
/// </summary>
internal sealed class PluggyOpenFinanceSource(PluggyApiClient api, TimeProvider timeProvider) : IOpenFinanceSource
{
    public string SourceName => "Pluggy";

    public async Task<OpenFinanceConnectionCheck> CheckConnectionAsync(IntegrationSyncRequest request, CancellationToken cancellationToken)
    {
        var (credentials, itemId) = ParseCredentials(request);
        var item = await api.GetItemAsync(credentials, itemId, cancellationToken).ConfigureAwait(false);
        EnsureUsable(item);

        var institution = item.Connector?.Name?.Trim();
        var summary = $"Pluggy item {item.Status}" + (string.IsNullOrEmpty(institution) ? string.Empty : $" via {institution}")
            + (item.LastUpdatedAt is { } updated ? $", last refreshed {updated.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : string.Empty) + ".";
        return new OpenFinanceConnectionCheck(summary, item.ConsentExpiresAt, ItemDiagnostics(item));
    }

    public async Task<OpenFinanceSnapshot> ReadAsync(IntegrationSyncRequest request, CancellationToken cancellationToken)
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
        // An item belongs to one holder; its bank account carries the holder's CPF/CNPJ.
        var holder = bankAccounts.Select(a => BrazilianTaxId.TryParse(a.TaxNumber)).FirstOrDefault(t => t is not null);

        var result = new List<OpenFinanceAccountTransactions>();
        foreach (var account in accounts)
        {
            var normalized = NormalizeAccount(account, issuerCompe) with { HolderTaxId = holder };
            var lines = await api.GetAllTransactionsAsync(credentials, account.Id!, cancellationToken).ConfigureAwait(false);
            result.Add(new OpenFinanceAccountTransactions(normalized, lines.Select(l => NormalizeTransaction(normalized, l)).ToList()));
        }

        return new OpenFinanceSnapshot(result, item.ConsentExpiresAt, ItemDiagnostics(item));
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

                // Pluggy reports a card's balance as the amount owed; the reconciliation check covers deposit accounts only.
                return new OpenFinanceAccount(account.Id, OpenFinanceAccountKind.CreditCard, null, issuerCompe, last4[^4..], currency, null);
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
            SourcePayload: line);
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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string ItemDiagnostics(PluggyItem item)
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
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
