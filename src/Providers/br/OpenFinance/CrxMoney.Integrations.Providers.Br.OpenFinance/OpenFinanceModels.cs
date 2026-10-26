using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Plugins;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Providers.Br.OpenFinance;

/// <summary>What kind of product an Open Finance account is.</summary>
public enum OpenFinanceAccountKind
{
    /// <summary>A deposit account: checking (conta corrente), savings or payment account.</summary>
    Deposit = 0,

    /// <summary>A credit card account.</summary>
    CreditCard = 1
}

/// <summary>Direction of a movement from the account holder's point of view.</summary>
public enum OpenFinanceDirection
{
    /// <summary>Money in: a received transfer, a refund, a card payment received by the card account.</summary>
    Credit = 0,

    /// <summary>Money out: a payment, a transfer sent, a card purchase.</summary>
    Debit = 1
}

/// <summary>
/// One account as every Open Finance source must describe it. The identity is the bank's own
/// (<see cref="BrazilianBankAccount"/> or the issuer plus the card's last four digits), never the
/// aggregator's id, so account maps survive a change of aggregator.
/// </summary>
/// <param name="SourceAccountId">The aggregator's own account id; kept for diagnostics only.</param>
/// <param name="Kind">Deposit or credit card.</param>
/// <param name="BankAccount">Required for deposit accounts.</param>
/// <param name="IssuerCompe">COMPE code of the card issuer; required for cards.</param>
/// <param name="CardLast4">Last four digits of the card; required for cards.</param>
/// <param name="CurrencyCode">ISO 4217 currency of the account.</param>
/// <param name="Balance">
/// The deposit balance the source reports, when it does. It is reported in the run diagnostics with
/// <paramref name="BalanceAt"/> so the host can compare it with its ledger. Card balances go in <see cref="Card"/>.
/// </param>
/// <param name="HolderTaxId">The account holder's CPF/CNPJ, when known: a counterparty equal to it is the holder moving money between its own accounts.</param>
/// <param name="BalanceAt">When the source last read <paramref name="Balance"/> (or <see cref="Card"/>) from the bank, when it says.</param>
public sealed record OpenFinanceAccount(
    string SourceAccountId,
    OpenFinanceAccountKind Kind,
    BrazilianBankAccount? BankAccount,
    string? IssuerCompe,
    string? CardLast4,
    string CurrencyCode,
    decimal? Balance,
    BrazilianTaxId? HolderTaxId = null,
    DateTimeOffset? BalanceAt = null)
{
    /// <summary>
    /// What the issuer reports a card owes, for card accounts whose source reports it. The plugin splits it into
    /// the settled balance and the pending amount (see <see cref="OpenFinanceCardBalance"/>).
    /// </summary>
    public OpenFinanceCardBalance? Card { get; init; }

    /// <summary>The COMPE code of the institution holding the account: the bank of a deposit account, the issuer of a card.</summary>
    public string? InstitutionCompe => Kind == OpenFinanceAccountKind.Deposit ? BankAccount?.Compe : IssuerCompe;

    /// <summary>The institution's official website from <see cref="BrazilianInstitutions"/>, when its code is listed.</summary>
    public string? InstitutionWebsite => BrazilianInstitutions.WebsiteOf(InstitutionCompe);

    /// <summary>The platform's external account id for this account; independent of the aggregator.</summary>
    /// <exception cref="OpenFinanceIdentityException">The account lacks the identity its kind requires.</exception>
    public string ExternalAccountId => Kind switch
    {
        OpenFinanceAccountKind.Deposit => BankAccount?.ExternalAccountId
            ?? throw new OpenFinanceIdentityException("A deposit account needs its COMPE/branch/number identity."),
        OpenFinanceAccountKind.CreditCard when !string.IsNullOrWhiteSpace(IssuerCompe) && CardLast4 is { Length: 4 } last4
            => BrazilianBankAccount.CardExternalAccountId(IssuerCompe, last4),
        _ => throw new OpenFinanceIdentityException("A card account needs its issuer COMPE code and last four digits."),
    };
}

/// <summary>An account cannot be identified the bank's way, so no key can be built for it. The message is safe to show.</summary>
public sealed class OpenFinanceIdentityException(string message) : Exception(message);

/// <summary>The other side of a movement.</summary>
/// <param name="Name">Name as the source reports it; may be absent.</param>
/// <param name="TaxId">CPF/CNPJ, only when its check digits are valid.</param>
/// <param name="Ispb">ISPB of the counterparty's institution, when known.</param>
/// <param name="Branch">Counterparty branch, when known.</param>
/// <param name="Account">Counterparty account, when known.</param>
public sealed record OpenFinanceParty(
    string? Name,
    BrazilianTaxId? TaxId,
    string? Ispb,
    string? Branch,
    string? Account)
{
    /// <summary>True when nothing about the party is known.</summary>
    public bool IsEmpty => Name is null && TaxId is null && Ispb is null && Branch is null && Account is null;
}

/// <summary>Pix identifiers, when the source carries them.</summary>
public sealed record OpenFinancePixIds(string? EndToEndId, string? TxId, string? Key);

/// <summary>
/// One movement as every Open Finance source must describe it, close to the Open Finance Brasil
/// account-transaction model. Adapters normalise their aggregator's quirks (sign conventions, field
/// names, statuses) into this record; everything downstream is shared.
/// </summary>
/// <param name="SourceTransactionId">The aggregator's own id: becomes the import's ExternalId, never its idempotency key.</param>
/// <param name="TransactionTime">The instant the bank reports for the movement, to the second when available.</param>
/// <param name="Direction">Money in or out for the account holder.</param>
/// <param name="Amount">
/// Positive amount in the account currency: on a line made in another currency, what the account was charged or
/// credited (see <see cref="OpenFinanceTransaction.Foreign"/>).
/// </param>
/// <param name="Type">Normalised Open Finance type: <c>PIX</c>, <c>TED</c>, <c>BOLETO</c>, <c>CARD</c>, <c>OUTROS</c>, ...</param>
/// <param name="TypeDetail">The source's free-text complement of the type (Pluggy <c>operationTypeAdditionalInfo</c>).</param>
/// <param name="Description">The bank's description line.</param>
/// <param name="Counterparty">The payer of a credit or the payee of a debit; the merchant for a card purchase.</param>
/// <param name="Pix">Pix ids, when present.</param>
/// <param name="IsPending">Not yet posted (a card authorisation): never imported.</param>
/// <param name="CardBillId">The card bill a card line belongs to, when the source says.</param>
/// <param name="SourcePayload">The aggregator's raw line, kept verbatim in the import's provider data.</param>
public sealed record OpenFinanceTransaction(
    string SourceTransactionId,
    DateTimeOffset TransactionTime,
    OpenFinanceDirection Direction,
    decimal Amount,
    string Type,
    string? TypeDetail,
    string? Description,
    OpenFinanceParty? Counterparty,
    OpenFinancePixIds? Pix,
    bool IsPending,
    string? CardBillId,
    JsonElement SourcePayload)
{
    /// <summary>
    /// The booking date in Brazil's calendar: the date of <see cref="TransactionTime"/>. The read window, the
    /// checkpoint and pairing go by it; an installment's import event may be dated later
    /// (<see cref="OpenFinanceTransactionMapper.EventDate"/>).
    /// </summary>
    public DateOnly BookingDate => BrazilBankingCalendar.DateOf(TransactionTime);

    /// <summary>
    /// On a card line: what the source reports beyond the bill (payment type, installment, bill dates, MCC), when it
    /// reports any of it. It never takes part in the idempotency key.
    /// </summary>
    public OpenFinanceCardLineDetails? CardDetails { get; init; }

    /// <summary>
    /// On a line made in a currency other than the account's (an international card purchase, #1440): the original
    /// currency and amount. <see cref="Amount"/> is still the amount in the account's currency, unless
    /// <see cref="OpenFinanceForeignAmount.AccountAmountMissing"/> says the source did not report it. Null on a line in
    /// the account's currency. Its <see cref="OpenFinanceForeignAmount.Amount"/>, not <see cref="Amount"/>, is the amount
    /// in the line identity (<see cref="OpenFinanceTransactionMapper.LineIdentity"/>), so a converted amount filled in or
    /// revised later does not change the key.
    /// </summary>
    public OpenFinanceForeignAmount? Foreign { get; init; }
}

/// <summary>The original currency and amount of a line made in a currency other than the account's (#1440).</summary>
/// <param name="CurrencyCode">The ISO 4217 code of the original currency, upper case.</param>
/// <param name="Amount">The positive amount in that currency.</param>
public sealed record OpenFinanceForeignAmount(string CurrencyCode, decimal Amount)
{
    /// <summary>
    /// True when the source did not report the amount in the account's currency, so the line's
    /// <see cref="OpenFinanceTransaction.Amount"/> is the original amount: no rate is guessed. The run diagnostics count
    /// these lines (<c>foreignLinesWithoutAccountAmount</c>).
    /// </summary>
    public bool AccountAmountMissing { get; init; }
}

/// <summary>
/// The Open Finance Brasil credit-card transaction fields beyond the bill id, as the source reports them. Every field
/// is optional; the shared mapper decides what an installment is and which date its event gets.
/// </summary>
public sealed record OpenFinanceCardLineDetails
{
    /// <summary>
    /// The payment type: <c>SINGLE</c> (à vista) or <c>INSTALLMENT</c> (parcelado). Open Finance's <c>A_VISTA</c> and
    /// <c>A_PRAZO</c> are accepted and read as these.
    /// </summary>
    public string? PaymentType { get; init; }

    /// <summary>The installment number, from 1 (Open Finance <c>chargeIdentificator</c>).</summary>
    public int? InstallmentNumber { get; init; }

    /// <summary>The number of installments (Open Finance <c>chargeNumber</c>).</summary>
    public int? InstallmentCount { get; init; }

    /// <summary>The purchase date in Brazil's calendar, when the source reports it.</summary>
    public DateOnly? PurchaseDate { get; init; }

    /// <summary>The whole purchase's amount, when the source reports it (direct connectors; Open Finance has none).</summary>
    public decimal? TotalAmount { get; init; }

    /// <summary>
    /// The date the line was put on its bill (Open Finance <c>billPostDate</c>). Open Finance sends <c>0001-01-01</c>
    /// while the line is on no bill yet; the mapper treats that date as absent.
    /// </summary>
    public DateOnly? BillPostDate { get; init; }

    /// <summary>The expected date of the bill the line is on, when the source reports it.</summary>
    public DateOnly? BillForecastDate { get; init; }

    /// <summary>The merchant category code (MCC) of the payee, four digits.</summary>
    public string? PayeeMcc { get; init; }
}

/// <summary>Where the plugin found that a card line is an installment.</summary>
public enum OpenFinanceInstallmentSource
{
    /// <summary>The source's installment fields (number and count).</summary>
    Metadata = 0,

    /// <summary>The trailing <c>dd/dd</c> of the description ("LOJA EXEMPLO 01/02", "LOJA EXEMPLO*DECOR06/06").</summary>
    Description = 1
}

/// <summary>One installment (parcela) of a card purchase or fee, as the shared mapper reads it.</summary>
public sealed record OpenFinanceInstallment
{
    /// <summary>The installment number, from 1.</summary>
    public required int Number { get; init; }

    /// <summary>The number of installments, at least 2.</summary>
    public required int Count { get; init; }

    /// <summary>The purchase date the source reports (its placeholder <c>0001-01-01</c> counts as missing), else the line's booking date.</summary>
    public required DateOnly PurchaseDate { get; init; }

    /// <summary>The whole purchase's amount, when the source reports it.</summary>
    public decimal? TotalAmount { get; init; }

    /// <summary>Where the number and count came from.</summary>
    public required OpenFinanceInstallmentSource Source { get; init; }
}

/// <summary>A card as the other side of a bill payment knows it.</summary>
/// <param name="IssuerCompe">COMPE code of the issuer.</param>
/// <param name="CardLast4">Last four digits.</param>
/// <param name="BillId">The bill the payment was booked on, when the card line says.</param>
public sealed record OpenFinanceCardReference(string IssuerCompe, string CardLast4, string? BillId);

/// <summary>The other side of a transfer-like movement, found by matching amounts and dates.</summary>
/// <param name="ExternalAccountId">The other side's account (a position, a card, a deposit account).</param>
/// <param name="IdempotencyKey">The other side's import event.</param>
/// <param name="Basis">How the pair was found: <c>amountAndDate</c>.</param>
public sealed record OpenFinancePair(string ExternalAccountId, string IdempotencyKey, string Basis = OpenFinancePair.AmountAndDate)
{
    /// <summary>Same amount, booking dates within the plugin's tolerance, and a single candidate account.</summary>
    public const string AmountAndDate = "amountAndDate";
}

/// <summary>
/// What the plugin found around an account line: the other side of a transfer-like line (written to its provider
/// data) and, on a card line, the bill it names.
/// </summary>
public sealed record OpenFinanceLineLinks
{
    /// <summary>The paired event on the other side.</summary>
    public OpenFinancePair? Pair { get; init; }

    /// <summary>On a checking-side card bill payment: the card that was paid.</summary>
    public OpenFinanceCardReference? PaidCard { get; init; }

    /// <summary>On a checking-side investment sweep: the position it moved money to or from.</summary>
    public OpenFinanceInvestment? Investment { get; init; }

    /// <summary>
    /// On a card line: the bill the line names (<see cref="OpenFinanceTransaction.CardBillId"/>), when the read listed
    /// it. It may date an installment (<see cref="OpenFinanceTransactionMapper.EventDate"/>) and adds no provider data.
    /// </summary>
    public OpenFinanceCardBill? Bill { get; init; }
}

/// <summary>Everything one read of a connection returned.</summary>
/// <param name="Accounts">The connection's accounts with their transactions.</param>
/// <param name="CredentialExpiresAt">When the source says the consent (or credential) expires, if it does.</param>
/// <param name="DiagnosticsJson">Optional source-specific diagnostics (no personal data, no secrets).</param>
public sealed record OpenFinanceSnapshot(
    IReadOnlyList<OpenFinanceAccountTransactions> Accounts,
    DateTimeOffset? CredentialExpiresAt,
    string? DiagnosticsJson = null)
{
    /// <summary>The connection's investment positions with their movements; empty when the source reads none.</summary>
    public IReadOnlyList<OpenFinanceInvestmentMovements> Investments { get; init; } = [];

    /// <summary>The connector the item reads through, when the source can tell (Inter or Inter Empresas, #1379).</summary>
    public IntegrationConnector? Connector { get; init; }
}

/// <summary>One account and its transactions from the requested read date on (see <see cref="IOpenFinanceSource.ReadAsync"/>).</summary>
public sealed record OpenFinanceAccountTransactions(OpenFinanceAccount Account, IReadOnlyList<OpenFinanceTransaction> Transactions)
{
    /// <summary>
    /// A card's bills as the source lists them, whatever the read window; empty for a deposit account, and for a card
    /// when the source lists none or could not read them. A card line names its bill in
    /// <see cref="OpenFinanceTransaction.CardBillId"/>.
    /// </summary>
    public IReadOnlyList<OpenFinanceCardBill> Bills { get; init; } = [];
}

/// <summary>
/// One bill (fatura) of a card as the issuer reports it: what the holder had to pay for one closed cycle.
/// </summary>
/// <param name="Id">The source's bill id, the one card lines carry.</param>
/// <param name="DueDate">The due date, when reported.</param>
/// <param name="TotalAmount">The bill's total (positive = owed by the holder), when reported.</param>
/// <param name="MinimumPayment">The minimum payment, when reported.</param>
/// <param name="CloseDate">The closing date of the cycle, when the source reports one.</param>
/// <param name="CurrencyCode">The total's currency (ISO 4217), when reported.</param>
public sealed record OpenFinanceCardBill(
    string Id,
    DateOnly? DueDate,
    decimal? TotalAmount,
    decimal? MinimumPayment = null,
    DateOnly? CloseDate = null,
    string? CurrencyCode = null);

/// <summary>
/// What a card owes as its issuer reports it. Open Finance issuers report the <b>used limit</b>, which includes
/// purchases that are authorised but not yet posted; the plugin subtracts the card's pending lines to get the
/// settled balance, the one the host's ledger holds. A bill payment the card still shows pending is the exception:
/// the host books it from the paying account's debit, so it counts as applied.
/// </summary>
/// <param name="Owed">The amount the issuer reports as owed (positive = owed by the holder).</param>
/// <param name="IncludesPending">True when <paramref name="Owed"/> already contains the pending lines (the used limit).</param>
/// <param name="CreditLimit">The card's credit limit, when reported.</param>
/// <param name="AvailableCreditLimit">The limit still available, when reported.</param>
/// <param name="MinimumPayment">The minimum payment of the current bill, when reported.</param>
/// <param name="BillCloseDate">The closing date of the current bill, when reported.</param>
/// <param name="BillDueDate">The due date of the current bill, when reported.</param>
public sealed record OpenFinanceCardBalance(
    decimal Owed,
    bool IncludesPending,
    decimal? CreditLimit = null,
    decimal? AvailableCreditLimit = null,
    decimal? MinimumPayment = null,
    DateOnly? BillCloseDate = null,
    DateOnly? BillDueDate = null);

/// <summary>The outcome of a connection test.</summary>
/// <param name="Summary">A short, user-facing description (institution, consent state). No secrets.</param>
/// <param name="CredentialExpiresAt">When the consent or credential expires, if known.</param>
/// <param name="DiagnosticsJson">Optional diagnostics (no personal data, no secrets).</param>
public sealed record OpenFinanceConnectionCheck(string Summary, DateTimeOffset? CredentialExpiresAt, string? DiagnosticsJson = null)
{
    /// <summary>When the source last refreshed the data from the bank, if it says.</summary>
    public DateTimeOffset? LastRefreshedAt { get; init; }

    /// <summary>Whether the connection reaches a sandbox, if the source says.</summary>
    public bool? IsSandbox { get; init; }

    /// <summary>COMPE codes of the institutions holding the connection's accounts, when the check read them.</summary>
    public IReadOnlyList<string> AccountOperatorCompes { get; init; } = [];

    /// <summary>The connector the item reads through, when the source can tell (Inter or Inter Empresas, #1379).</summary>
    public IntegrationConnector? Connector { get; init; }
}
