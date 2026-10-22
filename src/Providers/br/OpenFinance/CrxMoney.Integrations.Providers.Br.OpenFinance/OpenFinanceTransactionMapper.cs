using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

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
    /// second, direction and amount. Counterparty data is deliberately left out: aggregators enrich it
    /// after the fact and report it unevenly, so it would change keys. Identical lines are told apart
    /// by an ordinal (see <see cref="IdempotencyKey"/>).
    /// </summary>
    public static string LineIdentity(OpenFinanceAccount account, OpenFinanceTransaction transaction) =>
        string.Join('|',
            account.ExternalAccountId,
            transaction.TransactionTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            transaction.Direction == OpenFinanceDirection.Credit ? "C" : "D",
            transaction.Amount.ToString("0.00", CultureInfo.InvariantCulture));

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
    public static IntegrationImportEvent Map(OpenFinanceAccount account, OpenFinanceTransaction transaction, int ordinal, string sourceName) =>
        Map(account, transaction, ordinal, sourceName, links: null);

    /// <summary>
    /// Maps one posted line with what the plugin found on the other side of a transfer-like line (<paramref name="links"/>).
    /// Links only add provider data; the key is the same as without them.
    /// </summary>
    /// <exception cref="InvalidOperationException">The line is pending or the account has no bank identity.</exception>
    public static IntegrationImportEvent Map(OpenFinanceAccount account, OpenFinanceTransaction transaction, int ordinal, string sourceName, OpenFinanceLineLinks? links)
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
            ProviderData: BuildProviderData(account, transaction, sourceName, links),
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
    /// (aplicação/resgate of CDBs and similar, often typed <c>OUTROS</c>), settlements with the bank's own brokerage,
    /// and card bill payments.
    /// </summary>
    public static string? TransferHint(OpenFinanceAccount account, OpenFinanceTransaction transaction)
    {
        var type = transaction.Type.ToUpperInvariant();
        if (type is "APLICACAO_FINANCEIRA" or "RESGATE_APLIC_FINANCEIRA")
            return InvestmentHint;

        var words = Words($"{transaction.TypeDetail} {transaction.Description}");
        if (account.Kind == OpenFinanceAccountKind.CreditCard)
            return transaction.Direction == OpenFinanceDirection.Credit && words.Overlaps(CardPaymentWords) ? CardBillPaymentHint : null;

        if (words.Contains("FATURA") && transaction.Direction == OpenFinanceDirection.Debit)
            return CardBillPaymentHint;
        // Whole words only: "APLICATIVO" is an app purchase, "APL AUTOMATICA CDB" is a sweep.
        if (type is "OUTROS" or "OTHER" && (words.Overlaps(InvestmentProducts) || words.Overlaps(InvestmentMovements)))
            return InvestmentHint;
        if (IsBrokerSettlement(transaction))
            return InvestmentHint;
        return null;
    }

    /// <summary>
    /// A settlement between a checking account and the bank's own brokerage ("Saída COR ITAUCOR LIQUIDACAO",
    /// "Entrada COR OPERACOES B3"): money moving to or from the holder's investments at the same institution, which
    /// the aggregator often categorises as a card payment or a dividend. Only the brokerage's own shapes count: "Saída
    /// COR …" / "Entrada COR …" and ITAUCOR. Tax the brokerage withholds ("Saída COR IRRF ETF") is not a transfer and
    /// gets no hint.
    /// </summary>
    private static bool IsBrokerSettlement(OpenFinanceTransaction transaction)
    {
        var sequence = OrderedWords($"{transaction.TypeDetail} {transaction.Description}");
        if (sequence.Contains("IRRF"))
            return false;

        for (var i = 0; i < sequence.Count; i++)
        {
            if (sequence[i] == "ITAUCOR")
                return true;
            if (sequence[i] is "SAIDA" or "ENTRADA" && i + 1 < sequence.Count && sequence[i + 1] == "COR")
                return true;
        }

        return false;
    }

    /// <summary>The investment product a sweep line names (<c>CDB</c>, <c>LCI</c>, <c>LCA</c>, <c>POUPANCA</c>), if any.</summary>
    public static string? InvestmentProduct(OpenFinanceTransaction transaction)
    {
        var words = Words($"{transaction.TypeDetail} {transaction.Description}");
        return InvestmentProducts.Order(StringComparer.Ordinal).FirstOrDefault(words.Contains);
    }

    /// <summary>Writes the <c>pair</c> object: the other side of a transfer-like movement found by the plugin.</summary>
    internal static void WritePair(Utf8JsonWriter writer, OpenFinancePair pair)
    {
        writer.WriteStartObject("pair");
        writer.WriteString("externalAccountId", pair.ExternalAccountId);
        writer.WriteString("idempotencyKey", pair.IdempotencyKey);
        writer.WriteString("basis", pair.Basis);
        writer.WriteEndObject();
    }

    private static readonly HashSet<string> CardPaymentWords = new(StringComparer.Ordinal) { "PAGAMENTO", "PAGTO", "FATURA" };
    private static readonly HashSet<string> InvestmentProducts = new(StringComparer.Ordinal) { "CDB", "LCI", "LCA", "POUPANCA" };
    private static readonly HashSet<string> InvestmentMovements = new(StringComparer.Ordinal) { "APLICACAO", "RESGATE" };

    private static string BuildProviderData(OpenFinanceAccount account, OpenFinanceTransaction transaction, string sourceName, OpenFinanceLineLinks? links)
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
            WriteOptional(writer, "institutionWebsite", account.InstitutionWebsite);
            writer.WriteString("type", transaction.Type);
            WriteOptional(writer, "typeDetail", transaction.TypeDetail);
            WriteOptional(writer, "description", transaction.Description);

            var hint = TransferHint(account, transaction);
            if (hint is not null)
                writer.WriteString("transferHint", hint);

            if (account.Kind == OpenFinanceAccountKind.CreditCard)
            {
                writer.WriteStartObject("card");
                WriteOptional(writer, "last4", account.CardLast4);
                WriteOptional(writer, "billId", transaction.CardBillId);
                WriteOptional(writer, "issuerCompe", account.IssuerCompe);
                writer.WriteEndObject();
            }
            else if (links?.PaidCard is { } paid)
            {
                // The checking side of a card bill payment: the card it paid, from the paired card line.
                writer.WriteStartObject("card");
                writer.WriteString("last4", paid.CardLast4);
                WriteOptional(writer, "billId", paid.BillId);
                writer.WriteString("issuerCompe", paid.IssuerCompe);
                writer.WriteString("externalAccountId", BrazilianBankAccount.CardExternalAccountId(paid.IssuerCompe, paid.CardLast4));
                writer.WriteEndObject();
            }

            if (hint == InvestmentHint && account.Kind == OpenFinanceAccountKind.Deposit)
            {
                writer.WriteStartObject("investment");
                if (InvestmentProduct(transaction) is { } product)
                    writer.WriteString("product", product);
                if (links?.Investment is { } position)
                {
                    writer.WriteString("externalAccountId", position.ExternalAccountId);
                    writer.WriteString("externalAccountIdBasis", position.IdentityBasis);
                    writer.WriteString("type", position.Type);
                    WriteOptional(writer, "subtype", position.Subtype);
                    WriteOptional(writer, "name", position.Name);
                }

                writer.WriteEndObject();
            }

            if (links?.Pair is { } pair)
                WritePair(writer, pair);

            if (IsHolder(account, transaction.Counterparty))
            {
                writer.WriteBoolean("counterpartyIsHolder", true);
            }
            else if (transaction.Counterparty is { IsEmpty: false } party)
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

    /// <summary>The holder paying or receiving from itself (sweeps, own transfers) has no counterparty.</summary>
    private static bool IsHolder(OpenFinanceAccount account, OpenFinanceParty? party) =>
        account.HolderTaxId is { } holder && party?.TaxId is { } taxId && taxId.Value == holder.Value;

    /// <summary>Upper-case ASCII words: "Resgate automático CDB" becomes {RESGATE, AUTOMATICO, CDB}.</summary>
    private static HashSet<string> Words(string value) => new(OrderedWords(value), StringComparer.Ordinal);

    /// <summary>The words of a text in order: upper case, without accents, split on anything but ASCII letters and digits.</summary>
    private static List<string> OrderedWords(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }

        return [.. builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

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

    /// <summary>Schema of money into an investment position (applications, income, transfers in).</summary>
    public const string InvestmentDepositSchemaId = "br.openfinance.investment.deposit";

    /// <summary>Schema of money out of an investment position (redemptions, taxes, fees, payouts, transfers out).</summary>
    public const string InvestmentWithdrawalSchemaId = "br.openfinance.investment.withdrawal";

    /// <summary>Current schema version.</summary>
    public const short Version1 = 1;

    /// <summary>The manifest an Open Finance plugin returns.</summary>
    public static IntegrationImportSchemaManifest Manifest { get; } = new(
    [
        new IntegrationImportSchemaManifestItem(DepositSchemaId, Version1, "Open Finance credit",
            "Money in on a Brazilian account or card, read through Open Finance.", JsonSchemaFor("fiat.deposit")),
        new IntegrationImportSchemaManifestItem(WithdrawalSchemaId, Version1, "Open Finance debit",
            "Money out of a Brazilian account or card, read through Open Finance.", JsonSchemaFor("fiat.withdrawal")),
        new IntegrationImportSchemaManifestItem(InvestmentDepositSchemaId, Version1, "Open Finance investment in",
            "Money into a Brazilian investment position (application, income, transfer in), read through Open Finance.", InvestmentJsonSchemaFor("investment.deposit")),
        new IntegrationImportSchemaManifestItem(InvestmentWithdrawalSchemaId, Version1, "Open Finance investment out",
            "Money out of a Brazilian investment position (redemption, tax, fee, payout, transfer out), read through Open Finance.", InvestmentJsonSchemaFor("investment.withdrawal")),
    ]);

    private const string PairSchema = """
        { "type": "object", "description": "The other side of a transfer-like movement, matched by amount and date", "required": ["externalAccountId", "idempotencyKey", "basis"], "properties": { "externalAccountId": { "type": "string" }, "idempotencyKey": { "type": "string" }, "basis": { "const": "amountAndDate" } } }
        """;

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
            "institutionWebsite": { "type": "string", "format": "uri", "description": "Official website of the account's institution, when its COMPE code is in the package's institution list" },
            "type": { "type": "string", "description": "PIX, TED, BOLETO, CARD, OUTROS, ..." },
            "typeDetail": { "type": "string" },
            "description": { "type": "string" },
            "transferHint": { "enum": ["investment", "card_bill_payment"] },
            "card": {
              "type": "object",
              "description": "On card lines: the card itself. On a checking-side card bill payment: the card it paid, when paired",
              "properties": {
                "last4": { "type": "string" },
                "billId": { "type": "string" },
                "issuerCompe": { "type": "string", "description": "COMPE code of the issuer" },
                "externalAccountId": { "type": "string", "description": "The paid card's account (checking side only)" }
              }
            },
            "investment": {
              "type": "object",
              "description": "On a checking-side investment sweep: the product it names and, when paired, the position",
              "properties": {
                "product": { "type": "string", "description": "CDB, LCA, LCI, POUPANCA" },
                "externalAccountId": { "type": "string" },
                "externalAccountIdBasis": { "type": "string" },
                "type": { "type": "string" },
                "subtype": { "type": "string" },
                "name": { "type": "string" }
              }
            },
            "pair": {{PairSchema}},
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

    private static string InvestmentJsonSchemaFor(string kind) => $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["provider", "channel", "kind", "status", "sourceMovementId", "sourceInvestmentId", "movementTime", "movementDate", "accountKind", "operation", "grossAmount", "investment"],
          "properties": {
            "provider": { "type": "string", "description": "The aggregator or channel the movement was read through" },
            "channel": { "const": "openfinance" },
            "kind": { "const": "{{kind}}" },
            "status": { "const": "posted" },
            "sourceMovementId": { "type": "string", "description": "The aggregator's own movement id (the import ExternalId, with #component for components)" },
            "sourceInvestmentId": { "type": "string", "description": "The aggregator's own position id" },
            "movementTime": { "type": "string", "format": "date-time" },
            "movementDate": { "type": "string", "format": "date", "description": "Booking date, Brazilian calendar" },
            "settlementTime": { "type": "string", "format": "date-time" },
            "accountKind": { "const": "investment" },
            "institutionWebsite": { "type": "string", "format": "uri", "description": "Official website of the institution holding the position, when its COMPE code is in the package's institution list" },
            "operation": { "enum": ["application", "redemption", "amortization", "income", "tax", "transferIn", "transferOut"] },
            "component": { "enum": ["incomeTax", "iof", "fee", "payout"], "description": "Absent on the movement's main event" },
            "parentIdempotencyKey": { "type": "string", "description": "The main event of a component" },
            "taxKind": { "enum": ["incomeTax", "iof", "tax"], "description": "On a tax line" },
            "grossAmount": { "type": "number" },
            "cashAmount": { "type": "number", "description": "What moved between the position and the holder's cash" },
            "expenses": { "type": "object", "properties": { "incomeTax": { "type": "number" }, "iof": { "type": "number" }, "fees": { "type": "number" } } },
            "reportedNetAmount": { "type": "number", "description": "The source's own net amount, for reference" },
            "description": { "type": "string" },
            "investment": {
              "type": "object",
              "required": ["externalAccountIdBasis", "type"],
              "properties": {
                "externalAccountIdBasis": { "type": "string", "description": "number, isin, code, or the source name" },
                "type": { "type": "string", "description": "FIXED_INCOME, MUTUAL_FUND, EQUITY, ETF, SECURITY, COE, OTHER" },
                "subtype": { "type": "string", "description": "CDB, LCI, LCA, TREASURY, STOCK, ..." },
                "name": { "type": "string" },
                "issuer": { "type": "string" },
                "issuerTaxId": { "type": "string" },
                "isin": { "type": "string" }
              }
            },
            "transferHint": { "const": "investment", "description": "On the event that moved cash" },
            "pair": {{PairSchema}},
            "sourcePayload": { "description": "The aggregator's raw movement, verbatim (main event only)" }
          }
        }
        """;
}
