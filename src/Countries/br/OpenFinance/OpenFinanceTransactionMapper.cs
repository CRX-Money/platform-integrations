using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;

namespace CrxMoney.Integrations.Countries.Br.OpenFinance;

/// <summary>
/// Maps <see cref="OpenFinanceTransaction"/>s to <see cref="IntegrationImportEvent"/>s the same way for
/// every aggregator: BRL <c>deposit</c>/<c>withdrawal</c>, positive amount, the bank's own account
/// identity as external account, the booking date at midnight in Brazil, and an idempotency key built
/// only from what the bank reports, so the same line read through another aggregator gets the same key.
/// </summary>
public static class OpenFinanceTransactionMapper
{
    /// <summary>Import event type for money in.</summary>
    public const string DepositType = "deposit";

    /// <summary>Import event type for money out.</summary>
    public const string WithdrawalType = "withdrawal";

    /// <summary>Provider-data transfer hint for moves between the account and its own investments.</summary>
    public const string InvestmentHint = "investment";

    /// <summary>Provider-data transfer hint for paying a card bill from an account, or receiving that payment on the card.</summary>
    public const string CardBillPaymentHint = "card_bill_payment";

    /// <summary>
    /// The part of the idempotency key that identifies a line at the bank: account, instant to the
    /// second, direction, amount and counterparty document. Identical lines are told apart by an
    /// ordinal (see <see cref="IdempotencyKey"/>).
    /// </summary>
    public static string LineIdentity(OpenFinanceAccount account, OpenFinanceTransaction transaction) =>
        string.Join('|',
            account.ExternalAccountId,
            transaction.TransactionTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            transaction.Direction == OpenFinanceDirection.Credit ? "C" : "D",
            transaction.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            transaction.Counterparty?.TaxId?.Value ?? "-");

    /// <summary>
    /// The idempotency recipe: <c>brof:</c> + the first 32 hex digits of SHA-256(line identity + <c>|</c> + ordinal),
    /// where the ordinal numbers otherwise identical lines (0, 1, ...). It does not depend on the aggregator's ids,
    /// so switching aggregators on an existing connection re-imports nothing. Changing it is a breaking change
    /// (docs/versioning.md, "Provider packages").
    /// </summary>
    public static string IdempotencyKey(string lineIdentity, int ordinal)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{lineIdentity}|{ordinal}")));
        return "brof:" + Convert.ToHexStringLower(hash)[..32];
    }

    /// <summary>Maps one posted line. <paramref name="ordinal"/> comes from <see cref="AssignOrdinals"/>.</summary>
    /// <exception cref="InvalidOperationException">The line is pending or the account has no bank identity.</exception>
    public static IntegrationImportEvent Map(OpenFinanceAccount account, OpenFinanceTransaction transaction, int ordinal, string sourceName)
    {
        if (transaction.IsPending)
            throw new InvalidOperationException("Pending lines are not imported.");

        var isCredit = transaction.Direction == OpenFinanceDirection.Credit;
        return new IntegrationImportEvent(
            ExternalId: transaction.SourceTransactionId,
            IdempotencyKey: IdempotencyKey(LineIdentity(account, transaction), ordinal),
            Type: isCredit ? DepositType : WithdrawalType,
            Side: "Single",
            OccurredAtUtc: BrazilBankingCalendar.StartOfDay(transaction.BookingDate),
            ExternalAccountId: account.ExternalAccountId,
            AssetCode: account.CurrencyCode,
            Quantity: null,
            Amount: transaction.Amount,
            FeeAmount: null,
            FeeAssetCode: null,
            ProviderData: BuildProviderData(account, transaction, sourceName),
            ProviderDataSchemaId: isCredit ? OpenFinanceImportSchemas.DepositSchemaId : OpenFinanceImportSchemas.WithdrawalSchemaId,
            ProviderDataSchemaVersion: OpenFinanceImportSchemas.Version1);
    }

    /// <summary>
    /// Numbers lines that share a <see cref="LineIdentity"/> (two identical card purchases in the same
    /// second, say). Identical lines are interchangeable, so any order gives the same set of keys; the
    /// order is still made deterministic by the source id.
    /// </summary>
    public static IReadOnlyList<(OpenFinanceTransaction Transaction, int Ordinal)> AssignOrdinals(
        OpenFinanceAccount account,
        IEnumerable<OpenFinanceTransaction> postedTransactions)
    {
        var result = new List<(OpenFinanceTransaction, int)>();
        foreach (var group in postedTransactions
            .GroupBy(t => LineIdentity(account, t), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordinal = 0;
            foreach (var transaction in group.OrderBy(t => t.SourceTransactionId, StringComparer.Ordinal))
                result.Add((transaction, ordinal++));
        }

        return result;
    }

    /// <summary>
    /// Recognises transfer-like lines the bank does not type as transfers: automatic investment sweeps
    /// (aplicação/resgate of CDBs and similar, often typed <c>OUTROS</c>) and card bill payments.
    /// </summary>
    public static string? TransferHint(OpenFinanceAccount account, OpenFinanceTransaction transaction)
    {
        var type = transaction.Type.ToUpperInvariant();
        if (type is "APLICACAO_FINANCEIRA" or "RESGATE_APLIC_FINANCEIRA")
            return InvestmentHint;

        var text = Normalize($"{transaction.TypeDetail} {transaction.Description}");
        if (account.Kind == OpenFinanceAccountKind.CreditCard)
            return transaction.Direction == OpenFinanceDirection.Credit && ContainsAny(text, "PAGAMENTO", "PAGTO", "FATURA") ? CardBillPaymentHint : null;

        if (ContainsAny(text, "FATURA") && transaction.Direction == OpenFinanceDirection.Debit)
            return CardBillPaymentHint;
        if (type is "OUTROS" or "OTHER" && ContainsAny(text, "APLIC", "RESG", "CDB", "LCI ", "LCA ", "POUPANCA"))
            return InvestmentHint;
        return null;
    }

    private static string BuildProviderData(OpenFinanceAccount account, OpenFinanceTransaction transaction, string sourceName)
    {
        var isCredit = transaction.Direction == OpenFinanceDirection.Credit;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("provider", sourceName);
            writer.WriteString("channel", "openfinance");
            writer.WriteString("kind", isCredit ? "fiat.deposit" : "fiat.withdrawal");
            writer.WriteString("status", "posted");
            writer.WriteString("sourceTransactionId", transaction.SourceTransactionId);
            writer.WriteString("transactionTime", transaction.TransactionTime.ToUniversalTime());
            writer.WriteString("transactionDate", transaction.BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString("accountKind", account.Kind == OpenFinanceAccountKind.CreditCard ? "creditCard" : "deposit");
            writer.WriteString("type", transaction.Type);
            WriteOptional(writer, "typeDetail", transaction.TypeDetail);
            WriteOptional(writer, "description", transaction.Description);

            if (TransferHint(account, transaction) is { } hint)
                writer.WriteString("transferHint", hint);

            if (account.Kind == OpenFinanceAccountKind.CreditCard)
            {
                writer.WriteStartObject("card");
                WriteOptional(writer, "last4", account.CardLast4);
                WriteOptional(writer, "billId", transaction.CardBillId);
                writer.WriteEndObject();
            }

            if (transaction.Counterparty is { IsEmpty: false } party)
            {
                writer.WriteStartObject("counterparty");
                WriteOptional(writer, "name", party.Name);
                if (party.TaxId is { } taxId)
                {
                    writer.WriteString("taxId", taxId.Value);
                    writer.WriteString("taxIdKind", taxId.Kind);
                    writer.WriteString("taxIdCountry", "BR");
                }

                WriteOptional(writer, "ispb", party.Ispb);
                WriteOptional(writer, "branch", party.Branch);
                WriteOptional(writer, "account", party.Account);
                writer.WriteEndObject();
            }

            if (transaction.Pix is { } pix && (pix.EndToEndId ?? pix.TxId ?? pix.Key) is not null)
            {
                writer.WriteStartObject("pix");
                WriteOptional(writer, "endToEndId", pix.EndToEndId);
                WriteOptional(writer, "txId", pix.TxId);
                WriteOptional(writer, "key", pix.Key);
                writer.WriteEndObject();
            }

            if (transaction.SourcePayload.ValueKind is JsonValueKind.Object)
            {
                writer.WritePropertyName("sourcePayload");
                transaction.SourcePayload.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length + 1);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToUpperInvariant(c));
        }

        return builder.Append(' ').ToString();
    }

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(w => text.Contains(w, StringComparison.Ordinal));

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            writer.WriteString(name, value.Trim());
    }
}

/// <summary>The provider-data schemas every Open Finance source declares.</summary>
public static class OpenFinanceImportSchemas
{
    /// <summary>Schema of money-in lines.</summary>
    public const string DepositSchemaId = "br.openfinance.transaction.deposit";

    /// <summary>Schema of money-out lines.</summary>
    public const string WithdrawalSchemaId = "br.openfinance.transaction.withdrawal";

    /// <summary>Current schema version.</summary>
    public const short Version1 = 1;

    /// <summary>The manifest an Open Finance plugin returns.</summary>
    public static IntegrationImportSchemaManifest Manifest { get; } = new(
    [
        new IntegrationImportSchemaManifestItem(DepositSchemaId, Version1, "Open Finance credit",
            "Money in on a Brazilian account or card, read through Open Finance.", JsonSchemaFor("fiat.deposit")),
        new IntegrationImportSchemaManifestItem(WithdrawalSchemaId, Version1, "Open Finance debit",
            "Money out of a Brazilian account or card, read through Open Finance.", JsonSchemaFor("fiat.withdrawal")),
    ]);

    private static string JsonSchemaFor(string kind) => $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["provider", "channel", "kind", "status", "sourceTransactionId", "transactionTime", "transactionDate", "accountKind", "type"],
          "properties": {
            "provider": { "type": "string", "description": "The aggregator or channel the line was read through" },
            "channel": { "const": "openfinance" },
            "kind": { "const": "{{kind}}" },
            "status": { "const": "posted" },
            "sourceTransactionId": { "type": "string", "description": "The aggregator's own id (also the import ExternalId)" },
            "transactionTime": { "type": "string", "format": "date-time" },
            "transactionDate": { "type": "string", "format": "date", "description": "Booking date, Brazilian calendar" },
            "accountKind": { "enum": ["deposit", "creditCard"] },
            "type": { "type": "string", "description": "PIX, TED, BOLETO, CARD, OUTROS, ..." },
            "typeDetail": { "type": "string" },
            "description": { "type": "string" },
            "transferHint": { "enum": ["investment", "card_bill_payment"] },
            "card": { "type": "object", "properties": { "last4": { "type": "string" }, "billId": { "type": "string" } } },
            "counterparty": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "taxId": { "type": "string", "description": "CPF (11) or CNPJ (14) with valid check digits, unformatted" },
                "taxIdKind": { "enum": ["CPF", "CNPJ"] },
                "taxIdCountry": { "const": "BR" },
                "ispb": { "type": "string" },
                "branch": { "type": "string" },
                "account": { "type": "string" }
              }
            },
            "pix": { "type": "object", "properties": { "endToEndId": { "type": "string" }, "txId": { "type": "string" }, "key": { "type": "string" } } },
            "sourcePayload": { "description": "The aggregator's raw line, verbatim" }
          }
        }
        """;
}
