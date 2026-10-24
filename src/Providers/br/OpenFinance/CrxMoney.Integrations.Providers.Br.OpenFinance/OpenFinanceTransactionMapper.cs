using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

/// <summary>
/// Maps <see cref="OpenFinanceTransaction"/>s to <see cref="IntegrationImportEvent"/>s the same way for
/// every aggregator: BRL <c>deposit</c>/<c>withdrawal</c>, positive amount, the bank's own account
/// identity as external account, the booking date at midnight in Brazil (a card installment: its bill's date, see
/// <see cref="EventDate"/>), and an idempotency key built only from what the bank reports, so the same line read
/// through another aggregator gets the same key.
/// </summary>
public static partial class OpenFinanceTransactionMapper
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
    /// second, direction and amount (<c>{account}|{yyyy-MM-ddTHH:mm:ssZ}|{C or D}|{amount 0.00}</c>). A card
    /// installment (<see cref="Installment"/>, from the source's number and count or the description's <c>dd/dd</c>)
    /// adds <c>|{number}/{count}</c> (<c>|2/6</c>, <c>|10/12</c>, without leading zeros), since every installment of
    /// a purchase carries the purchase instant and, in an equal-amount series, the same amount (#1424). Counterparty
    /// data and the bill dates are deliberately left out: aggregators enrich counterparties after the fact and report
    /// them unevenly, and a bill post date can still be Open Finance's placeholder on a posted line, so either would
    /// change keys. Lines that are still identical (two identical purchases in the same second) are told apart by an
    /// ordinal (see <see cref="IdempotencyKey"/>).
    /// </summary>
    public static string LineIdentity(OpenFinanceAccount account, OpenFinanceTransaction transaction)
    {
        var identity = string.Join('|',
            account.ExternalAccountId,
            transaction.TransactionTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            transaction.Direction == OpenFinanceDirection.Credit ? "C" : "D",
            transaction.Amount.ToString("0.00", CultureInfo.InvariantCulture));
        return Installment(account, transaction) is { } installment
            ? string.Create(CultureInfo.InvariantCulture, $"{identity}|{installment.Number}/{installment.Count}")
            : identity;
    }

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
    /// Maps one posted line with what the plugin found on the other side of a transfer-like line and, on a card line,
    /// the bill it names (<paramref name="links"/>). Links add provider data and may date a card installment at its bill
    /// (<see cref="EventDate"/>); the key is the same as without them.
    /// </summary>
    /// <exception cref="InvalidOperationException">The line is pending or the account has no bank identity.</exception>
    public static IntegrationImportEvent Map(OpenFinanceAccount account, OpenFinanceTransaction transaction, int ordinal, string sourceName, OpenFinanceLineLinks? links)
    {
        if (transaction.IsPending)
            throw new InvalidOperationException("Pending lines are not imported.");

        var isCredit = transaction.Direction == OpenFinanceDirection.Credit;
        var installment = Installment(account, transaction);
        var dating = Dating(transaction, installment, links?.Bill);
        return new IntegrationImportEvent(
            ExternalId: transaction.SourceTransactionId,
            IdempotencyKey: IdempotencyKey(LineIdentity(account, transaction), ordinal),
            Type: isCredit ? DepositType : WithdrawalType,
            Side: "Single",
            OccurredAtUtc: BrazilBankingCalendar.StartOfDay(dating.Date),
            ExternalAccountId: account.ExternalAccountId,
            AssetCode: account.CurrencyCode,
            Quantity: null,
            Amount: transaction.Amount,
            FeeAmount: null,
            FeeAssetCode: null,
            ProviderData: BuildProviderData(account, transaction, sourceName, links, installment, dating.Source),
            ProviderDataSchemaId: isCredit ? OpenFinanceImportSchemas.DepositSchemaId : OpenFinanceImportSchemas.WithdrawalSchemaId,
            ProviderDataSchemaVersion: OpenFinanceImportSchemas.Version1);
    }

    /// <summary>
    /// The date a line's import event gets. A card installment (<see cref="Installment"/>) is dated from its bill, so
    /// the books follow the invoice and a past month stops growing when a later installment arrives. The first of these
    /// that is <b>later than the purchase date</b> wins (an installment is never dated back before its purchase):
    /// <list type="number">
    /// <item><description><c>billPostDate</c>, the date the line was put on its bill (Open Finance's placeholder <c>0001-01-01</c> counts as missing);</description></item>
    /// <item><description>the bill the line names (<paramref name="bill"/>, matched by <see cref="OpenFinanceTransaction.CardBillId"/>): its closing date, else its due date;</description></item>
    /// <item><description>the first day of the month of <c>billForecastDate</c>;</description></item>
    /// <item><description>otherwise the line keeps its booking date.</description></item>
    /// </list>
    /// Every other line, a single purchase included, is dated at its booking date. The idempotency key does not depend
    /// on this date. The shared plugin sends a line when this date is inside the sync window (#1424), so installment 2
    /// and later of an older purchase is sent when its bill arrives.
    /// </summary>
    public static DateOnly EventDate(OpenFinanceAccount account, OpenFinanceTransaction transaction, OpenFinanceCardBill? bill = null) =>
        Dating(transaction, Installment(account, transaction), bill).Date;

    /// <summary>The event's date and, for an installment, what it came from (<c>card.installment.dateSource</c>).</summary>
    private static (DateOnly Date, string? Source) Dating(OpenFinanceTransaction transaction, OpenFinanceInstallment? installment, OpenFinanceCardBill? bill)
    {
        if (installment is null)
            return (transaction.BookingDate, null);

        var purchase = installment.PurchaseDate;
        if (BillPostDate(transaction) is { } posted && posted > purchase)
            return (posted, "billPostDate");
        if (bill is not null && bill.Id.Trim() == transaction.CardBillId?.Trim() && (bill.CloseDate ?? bill.DueDate) is { } billDate && billDate > purchase)
            return (billDate, "bill");
        if (transaction.CardDetails?.BillForecastDate is { } forecast && forecast != NoBillDate
            && new DateOnly(forecast.Year, forecast.Month, 1) is var forecastMonth && forecastMonth > purchase)
            return (forecastMonth, "billForecastDate");
        return (transaction.BookingDate, "purchase");
    }

    /// <summary>
    /// The installment a card line is, or <c>null</c> for a single purchase and any line of a deposit account. A line
    /// whose payment type is <c>SINGLE</c> is never an installment. Otherwise the source's number and count decide
    /// when it reports both: an installment when the count is at least 2 and the number between 1 and the count; the
    /// payment type may be missing (Pluggy sends none on some card lines, and Open Finance requires it only on
    /// purchases, so a fee in installments typed <c>TARIFA</c> often has none). When the source reports no number and
    /// count, the trailing <c>dd/dd</c> of the description is read instead, spaced or not ("LOJA EXEMPLO 01/02",
    /// "LOJA EXEMPLO*DECOR06/06", "ANUIDADE DIFERENCI04/10"), with the same bounds.
    /// </summary>
    public static OpenFinanceInstallment? Installment(OpenFinanceAccount account, OpenFinanceTransaction transaction)
    {
        if (account.Kind != OpenFinanceAccountKind.CreditCard)
            return null;

        var details = transaction.CardDetails;
        if (PaymentType(details) == SinglePayment)
            return null;

        var purchaseDate = details?.PurchaseDate is { } reported && reported != NoBillDate ? reported : transaction.BookingDate;
        if (details is { InstallmentNumber: { } number, InstallmentCount: { } count })
        {
            return IsInstallment(number, count)
                ? new OpenFinanceInstallment
                {
                    Number = number,
                    Count = count,
                    PurchaseDate = purchaseDate,
                    TotalAmount = details.TotalAmount,
                    Source = OpenFinanceInstallmentSource.Metadata,
                }
                : null;
        }

        if (transaction.Description is { } description && InstallmentSuffix().Match(description) is { Success: true } match)
        {
            var fromNumber = int.Parse(match.Groups["number"].ValueSpan, CultureInfo.InvariantCulture);
            var fromCount = int.Parse(match.Groups["count"].ValueSpan, CultureInfo.InvariantCulture);
            if (IsInstallment(fromNumber, fromCount))
            {
                return new OpenFinanceInstallment
                {
                    Number = fromNumber,
                    Count = fromCount,
                    PurchaseDate = purchaseDate,
                    TotalAmount = details?.TotalAmount,
                    Source = OpenFinanceInstallmentSource.Description,
                };
            }
        }

        return null;
    }

    private static bool IsInstallment(int number, int count) => count >= 2 && number >= 1 && number <= count;

    /// <summary>The source's payment type as <c>SINGLE</c> or <c>INSTALLMENT</c> (Open Finance <c>A_VISTA</c>/<c>A_PRAZO</c> read as these).</summary>
    private static string? PaymentType(OpenFinanceCardLineDetails? details) =>
        details?.PaymentType?.Trim().ToUpperInvariant() switch
        {
            null or "" => null,
            "A_VISTA" => SinglePayment,
            "A_PRAZO" => "INSTALLMENT",
            var type => type,
        };

    /// <summary>The bill post date, without Open Finance's placeholder for a line on no bill yet.</summary>
    private static DateOnly? BillPostDate(OpenFinanceTransaction transaction) =>
        transaction.CardDetails?.BillPostDate is { } date && date != NoBillDate ? date : null;

    private const string SinglePayment = "SINGLE";

    /// <summary>Open Finance's placeholder date (<c>0001-01-01</c>): a line on no bill yet, or a date the source does not know.</summary>
    private static readonly DateOnly NoBillDate = new(1, 1, 1);

    /// <summary>"…01/02" at the end of a description, after a space or straight after the merchant's name, never after a digit.</summary>
    [GeneratedRegex(@"(?<![0-9])(?<number>[0-9]{2})/(?<count>[0-9]{2})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallmentSuffix();

    /// <summary>
    /// Numbers lines that share a <see cref="LineIdentity"/> (two identical card purchases in the same
    /// second, say, or the same installment of two identical purchases). Identical lines are interchangeable, so any
    /// order gives the same set of keys; the order is still made deterministic by the source id. The installments of
    /// one purchase never share an identity, so a read that holds only some of them numbers each one 0.
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

    private static string BuildProviderData(OpenFinanceAccount account, OpenFinanceTransaction transaction, string sourceName, OpenFinanceLineLinks? links, OpenFinanceInstallment? installment, string? dateSource)
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
                WriteCardDetails(writer, transaction, installment, dateSource);
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

    /// <summary>
    /// The card-line keys added after preview.8, after the earlier ones: <c>paymentType</c>, <c>billPostDate</c>,
    /// <c>billForecastDate</c>, <c>payeeMcc</c> and, on an installment, <c>installment</c>.
    /// </summary>
    private static void WriteCardDetails(Utf8JsonWriter writer, OpenFinanceTransaction transaction, OpenFinanceInstallment? installment, string? dateSource)
    {
        var details = transaction.CardDetails;
        WriteOptional(writer, "paymentType", PaymentType(details));
        if (BillPostDate(transaction) is { } posted)
            writer.WriteString("billPostDate", IsoDate(posted));
        if (details?.BillForecastDate is { } forecast && forecast != NoBillDate)
            writer.WriteString("billForecastDate", IsoDate(forecast));
        WriteOptional(writer, "payeeMcc", details?.PayeeMcc);

        if (installment is null)
            return;
        writer.WriteStartObject("installment");
        writer.WriteNumber("number", installment.Number);
        writer.WriteNumber("count", installment.Count);
        writer.WriteString("purchaseDate", IsoDate(installment.PurchaseDate));
        if (installment.TotalAmount is { } total)
            writer.WriteNumber("totalAmount", total);
        writer.WriteString("source", installment.Source == OpenFinanceInstallmentSource.Metadata ? "metadata" : "description");
        WriteOptional(writer, "dateSource", dateSource);
        writer.WriteEndObject();
    }

    private static string IsoDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
            "transactionDate": { "type": "string", "format": "date", "description": "Booking date, Brazilian calendar: the date of transactionTime (an installment's event is dated from its bill, see card.installment.dateSource)" },
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
                "externalAccountId": { "type": "string", "description": "The paid card's account (checking side only)" },
                "paymentType": { "type": "string", "description": "Card lines: SINGLE or INSTALLMENT, as the source reports it" },
                "billPostDate": { "type": "string", "format": "date", "description": "Card lines: the date the line was put on its bill; absent while it is on no bill yet" },
                "billForecastDate": { "type": "string", "format": "date", "description": "Card lines: the expected date of the line's bill" },
                "payeeMcc": { "type": "string", "description": "Card lines: the payee's merchant category code, four digits" },
                "installment": {
                  "type": "object",
                  "description": "Card lines that are an installment (parcela) of a purchase or a fee; the event is dated from its bill (see dateSource)",
                  "required": ["number", "count", "purchaseDate", "source", "dateSource"],
                  "properties": {
                    "number": { "type": "integer", "minimum": 1 },
                    "count": { "type": "integer", "minimum": 2 },
                    "purchaseDate": { "type": "string", "format": "date", "description": "The purchase date the source reports, else the line's transactionDate" },
                    "totalAmount": { "type": "number", "description": "The whole purchase's amount, when the source reports it (direct connectors only); never a second movement" },
                    "source": { "enum": ["metadata", "description"], "description": "The source's installment fields, or the trailing dd/dd of the description" },
                    "dateSource": { "enum": ["billPostDate", "bill", "billForecastDate", "purchase"], "description": "What the event's date came from: billPostDate, the named bill's closing date (else its due date), the first day of billForecastDate's month, or the line's own date when none of them is later than purchaseDate" }
                  }
                }
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
