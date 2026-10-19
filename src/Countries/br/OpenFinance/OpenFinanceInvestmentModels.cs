using System.Text.Json;

namespace CrxMoney.Integrations.Countries.Br.OpenFinance;

/// <summary>What happened to an investment position, close to the Open Finance Brasil investment operations.</summary>
public enum OpenFinanceInvestmentOperation
{
    /// <summary>Money applied to the position (aplicação, subscription, purchase).</summary>
    Application = 0,

    /// <summary>Money redeemed from the position (resgate, sale).</summary>
    Redemption = 1,

    /// <summary>Principal paid back by the issuer before or at maturity (amortização).</summary>
    Amortization = 2,

    /// <summary>Income credited: interest, coupons, dividends, rental income.</summary>
    Income = 3,

    /// <summary>A tax withheld from the position on its own line (IR, IOF).</summary>
    Tax = 4,

    /// <summary>A transfer into the position from another one (portability, custody transfer).</summary>
    TransferIn = 5,

    /// <summary>A transfer out of the position to another one.</summary>
    TransferOut = 6
}

/// <summary>
/// One investment position as every Open Finance source must describe it. Its identity is the institution's
/// (<see cref="IdentityBasis"/> + <see cref="IdentityKey"/>), so a change of aggregator keeps account links when
/// the institution's reference is known.
/// </summary>
/// <param name="SourceInvestmentId">The aggregator's own position id; kept for diagnostics.</param>
/// <param name="InstitutionCompe">COMPE code of the institution holding the position (the item's bank).</param>
/// <param name="IdentityBasis">
/// What <paramref name="IdentityKey"/> is: <c>number</c> (the institution's reference for this holder's position),
/// <c>isin</c>, <c>code</c> (a fund's CNPJ or a ticker), or the source's name when only the aggregator's id is known.
/// </param>
/// <param name="IdentityKey">The identifying value.</param>
/// <param name="Type">Asset class: <c>FIXED_INCOME</c>, <c>MUTUAL_FUND</c>, <c>EQUITY</c>, <c>ETF</c>, <c>SECURITY</c>, <c>COE</c>, <c>OTHER</c>.</param>
/// <param name="Subtype">Instrument: <c>CDB</c>, <c>LCI</c>, <c>LCA</c>, <c>TREASURY</c>, <c>STOCK</c>, ...</param>
/// <param name="Name">The product name as the institution reports it.</param>
/// <param name="CurrencyCode">ISO 4217 currency of the amounts.</param>
/// <param name="Balance">Current net balance (after the taxes and fees a redemption would cost).</param>
/// <param name="BalanceDate">The reference date of <paramref name="Balance"/>.</param>
public sealed record OpenFinanceInvestment(
    string SourceInvestmentId,
    string InstitutionCompe,
    string IdentityBasis,
    string IdentityKey,
    string Type,
    string? Subtype,
    string Name,
    string CurrencyCode,
    decimal Balance,
    DateTimeOffset BalanceDate)
{
    /// <summary>The issuer of the paper (the bank of a CDB, the fund manager), when reported.</summary>
    public string? Issuer { get; init; }

    /// <summary>The issuer's CNPJ, when reported with valid check digits.</summary>
    public BrazilianTaxId? IssuerTaxId { get; init; }

    /// <summary>The ISIN, when reported.</summary>
    public string? Isin { get; init; }

    /// <summary>Lifecycle status as reported: <c>ACTIVE</c>, <c>PENDING</c>, <c>TOTAL_WITHDRAWAL</c>.</summary>
    public string? Status { get; init; }

    /// <summary>Gross amount (before taxes), when reported.</summary>
    public decimal? GrossAmount { get; init; }

    /// <summary>The amount originally invested, when reported.</summary>
    public decimal? AmountInvested { get; init; }

    /// <summary>Profit (negative: loss) to date, when reported.</summary>
    public decimal? Profit { get; init; }

    /// <summary>Income tax a redemption would withhold now, when reported.</summary>
    public decimal? IncomeTax { get; init; }

    /// <summary>Financial taxes (IOF) a redemption would withhold now, when reported.</summary>
    public decimal? FinancialTax { get; init; }

    /// <summary>Issue date, when reported.</summary>
    public DateOnly? IssueDate { get; init; }

    /// <summary>Maturity, when reported.</summary>
    public DateOnly? DueDate { get; init; }

    /// <summary>Rate index (<c>CDI</c>, <c>IPCA</c>, ...), when reported.</summary>
    public string? RateType { get; init; }

    /// <summary>Rate (for example 100 for 100% of CDI), when reported.</summary>
    public decimal? Rate { get; init; }

    /// <summary>The aggregator's raw position, kept verbatim in provider data.</summary>
    public JsonElement SourcePayload { get; init; }

    /// <summary>The official website of the institution holding the position, when its code is listed (<see cref="BrazilianInstitutions"/>).</summary>
    public string? InstitutionWebsite => BrazilianInstitutions.WebsiteOf(InstitutionCompe);

    /// <summary>The platform's external account id for the position: <c>br:{compe}:inv:{basis}:{key}</c>.</summary>
    /// <exception cref="OpenFinanceIdentityException">The identity is empty.</exception>
    public string ExternalAccountId
    {
        get
        {
            try
            {
                return BrazilianBankAccount.InvestmentExternalAccountId(InstitutionCompe, IdentityBasis, IdentityKey);
            }
            catch (FormatException ex)
            {
                throw new OpenFinanceIdentityException($"An investment position cannot be identified: {ex.Message}");
            }
        }
    }
}

/// <summary>Taxes and fees charged on one investment movement.</summary>
/// <param name="IncomeTax">Income tax withheld (IR, IRRF).</param>
/// <param name="Iof">IOF charged.</param>
/// <param name="Fees">Every other fee or tax (brokerage, custody, exchange fees, ...), summed.</param>
public sealed record OpenFinanceInvestmentExpenses(decimal IncomeTax, decimal Iof, decimal Fees)
{
    /// <summary>No expenses.</summary>
    public static OpenFinanceInvestmentExpenses None { get; } = new(0m, 0m, 0m);

    /// <summary>Everything charged.</summary>
    public decimal Total => IncomeTax + Iof + Fees;
}

/// <summary>
/// One movement of a position, as every Open Finance source must describe it. The plugin turns it into
/// import events on the position's account: the movement itself for its cash amount, and one event per
/// tax or fee, so that the events of a position add up to its gross movement.
/// </summary>
/// <param name="SourceMovementId">The aggregator's own id: becomes the import's ExternalId, never its idempotency key.</param>
/// <param name="MovementTime">The instant the institution reports for the movement.</param>
/// <param name="Operation">What happened.</param>
/// <param name="GrossAmount">Positive gross amount of the operation.</param>
/// <param name="Expenses">Taxes and fees charged on it.</param>
/// <param name="Description">The institution's description, when present.</param>
/// <param name="SourcePayload">The aggregator's raw movement, kept verbatim in provider data.</param>
public sealed record OpenFinanceInvestmentMovement(
    string SourceMovementId,
    DateTimeOffset MovementTime,
    OpenFinanceInvestmentOperation Operation,
    decimal GrossAmount,
    OpenFinanceInvestmentExpenses Expenses,
    string? Description,
    JsonElement SourcePayload)
{
    /// <summary>For <see cref="OpenFinanceInvestmentOperation.Income"/>: the income was paid out to the holder's cash rather than kept in the position.</summary>
    public bool PaidOut { get; init; }

    /// <summary>The date the movement was settled, when the source reports it apart from <see cref="MovementTime"/>.</summary>
    public DateTimeOffset? SettlementTime { get; init; }

    /// <summary>The amount after expenses as the source reports it, kept for reference only (sources disagree on its meaning).</summary>
    public decimal? ReportedNetAmount { get; init; }

    /// <summary>The booking date in Brazil's calendar.</summary>
    public DateOnly BookingDate => BrazilBankingCalendar.DateOf(MovementTime);
}

/// <summary>A position and its movements. Sources return every movement they have: the plugin applies the sync window.</summary>
public sealed record OpenFinanceInvestmentMovements(OpenFinanceInvestment Investment, IReadOnlyList<OpenFinanceInvestmentMovement> Movements);
