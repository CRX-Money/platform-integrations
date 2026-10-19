using System.Text.Json;
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
/// <param name="Amount">Positive amount in the account currency.</param>
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
    /// <summary>The booking date in Brazil's calendar.</summary>
    public DateOnly BookingDate => BrazilBankingCalendar.DateOf(TransactionTime);
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

/// <summary>What the plugin found on the other side of an account line; written to its provider data.</summary>
public sealed record OpenFinanceLineLinks
{
    /// <summary>The paired event on the other side.</summary>
    public OpenFinancePair? Pair { get; init; }

    /// <summary>On a checking-side card bill payment: the card that was paid.</summary>
    public OpenFinanceCardReference? PaidCard { get; init; }

    /// <summary>On a checking-side investment sweep: the position it moved money to or from.</summary>
    public OpenFinanceInvestment? Investment { get; init; }
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
}

/// <summary>One account and its transactions from the requested read date on (see <see cref="IOpenFinanceSource.ReadAsync"/>).</summary>
public sealed record OpenFinanceAccountTransactions(OpenFinanceAccount Account, IReadOnlyList<OpenFinanceTransaction> Transactions);

/// <summary>
/// What a card owes as its issuer reports it. Open Finance issuers report the <b>used limit</b>, which includes
/// purchases that are authorised but not yet posted; the plugin subtracts the card's pending lines to get the
/// settled balance, the one its posted lines prove.
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
}
