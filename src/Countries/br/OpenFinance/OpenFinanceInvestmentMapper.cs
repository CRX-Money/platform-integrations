using System.Globalization;
using System.Text;
using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Sync;

namespace CrxMoney.Integrations.Countries.Br.OpenFinance;

/// <summary>
/// Maps <see cref="OpenFinanceInvestmentMovement"/>s to <see cref="IntegrationImportEvent"/>s on the position's own
/// account, the same way for every aggregator. Each movement gives one event for the money that moves between the
/// position and the holder's cash (its <see cref="CashAmount"/>), plus one withdrawal per tax or fee, so the events
/// of a movement add up to its gross amount:
/// <list type="bullet">
/// <item>application: deposit of gross + expenses (what left the cash account), then the expenses as withdrawals;</item>
/// <item>redemption, amortization: withdrawal of gross − expenses (what reached the cash account), then the expenses;</item>
/// <item>income: deposit of the gross income, the expenses, and, when paid out, a <c>payout</c> withdrawal of the rest;</item>
/// <item>tax: one withdrawal; transfers: one deposit or withdrawal of the gross amount.</item>
/// </list>
/// </summary>
public static class OpenFinanceInvestmentMapper
{
    /// <summary>Provider-data operation names, in the order of <see cref="OpenFinanceInvestmentOperation"/>.</summary>
    private static readonly string[] OperationNames = ["application", "redemption", "amortization", "income", "tax", "transferIn", "transferOut"];

    /// <summary>Component event for income tax withheld on a movement.</summary>
    public const string IncomeTaxComponent = "incomeTax";

    /// <summary>Component event for IOF charged on a movement.</summary>
    public const string IofComponent = "iof";

    /// <summary>Component event for the other fees charged on a movement.</summary>
    public const string FeeComponent = "fee";

    /// <summary>Component event for income paid out of the position to the holder's cash.</summary>
    public const string PayoutComponent = "payout";

    /// <summary>The provider-data name of an operation (<c>application</c>, <c>redemption</c>, ...).</summary>
    public static string OperationName(OpenFinanceInvestmentOperation operation) =>
        (int)operation is var index && index >= 0 && index < OperationNames.Length
            ? OperationNames[index]
            : throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown investment operation.");

    /// <summary>
    /// The part of the key that identifies a movement at the institution: position, instant to the second,
    /// operation and gross amount. Identical movements are told apart by an ordinal, as for account lines.
    /// </summary>
    public static string LineIdentity(OpenFinanceInvestment investment, OpenFinanceInvestmentMovement movement) =>
        string.Join('|',
            "inv",
            investment.ExternalAccountId,
            movement.MovementTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            OperationName(movement.Operation),
            movement.GrossAmount.ToString("0.00", CultureInfo.InvariantCulture));

    /// <summary>The key of a movement's main event (<paramref name="component"/> <c>null</c>) or of one of its components.</summary>
    public static string IdempotencyKey(string lineIdentity, int ordinal, string? component = null) =>
        OpenFinanceTransactionMapper.IdempotencyKey(component is null ? lineIdentity : $"{lineIdentity}|{component}", ordinal);

    /// <summary>Numbers identical movements of a position (see <see cref="OpenFinanceTransactionMapper.AssignOrdinals"/>).</summary>
    public static IReadOnlyList<(OpenFinanceInvestmentMovement Movement, int Ordinal)> AssignOrdinals(
        OpenFinanceInvestment investment,
        IEnumerable<OpenFinanceInvestmentMovement> movements)
    {
        var result = new List<(OpenFinanceInvestmentMovement, int)>();
        foreach (var group in movements
            .GroupBy(m => LineIdentity(investment, m), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordinal = 0;
            foreach (var movement in group.OrderBy(m => m.SourceMovementId, StringComparer.Ordinal))
                result.Add((movement, ordinal++));
        }

        return result;
    }

    /// <summary>
    /// The money a movement moves between the position and the holder's cash, or <c>null</c> when it moves none
    /// (income kept in the position, taxes, transfers between positions).
    /// </summary>
    public static decimal? CashAmount(OpenFinanceInvestmentMovement movement) => movement.Operation switch
    {
        OpenFinanceInvestmentOperation.Application => movement.GrossAmount + movement.Expenses.Total,
        OpenFinanceInvestmentOperation.Redemption or OpenFinanceInvestmentOperation.Amortization => movement.GrossAmount - movement.Expenses.Total,
        OpenFinanceInvestmentOperation.Income when movement.PaidOut => movement.GrossAmount - movement.Expenses.Total,
        _ => null,
    };

    /// <summary>
    /// The key of the event that carries <see cref="CashAmount"/> (the one a cash-account line pairs with),
    /// or <c>null</c> when the movement moves no cash.
    /// </summary>
    public static string? CashEventKey(OpenFinanceInvestment investment, OpenFinanceInvestmentMovement movement, int ordinal) =>
        CashAmount(movement) is > 0m
            ? IdempotencyKey(LineIdentity(investment, movement), ordinal, movement.Operation == OpenFinanceInvestmentOperation.Income ? PayoutComponent : null)
            : null;

    /// <summary>Maps one movement to its events. <paramref name="cashPair"/> is the cash-account line it was paired with, if any.</summary>
    public static IReadOnlyList<IntegrationImportEvent> Map(
        OpenFinanceInvestment investment,
        OpenFinanceInvestmentMovement movement,
        int ordinal,
        string sourceName,
        OpenFinancePair? cashPair = null)
    {
        var identity = LineIdentity(investment, movement);
        var mainKey = IdempotencyKey(identity, ordinal);
        var cash = CashAmount(movement);
        var events = new List<IntegrationImportEvent>();

        void Add(string? component, bool isDeposit, decimal amount, bool movesCash)
        {
            if (amount <= 0m)
                return;
            var key = IdempotencyKey(identity, ordinal, component);
            var data = BuildProviderData(investment, movement, sourceName, component, isDeposit, amount, cash,
                component is null ? null : mainKey, movesCash ? cashPair : null, movesCash);
            events.Add(new IntegrationImportEvent(
                ExternalId: component is null ? movement.SourceMovementId : $"{movement.SourceMovementId}#{component}",
                IdempotencyKey: key,
                Type: isDeposit ? OpenFinanceTransactionMapper.DepositType : OpenFinanceTransactionMapper.WithdrawalType,
                Side: "Single",
                OccurredAtUtc: BrazilBankingCalendar.StartOfDay(movement.BookingDate),
                ExternalAccountId: investment.ExternalAccountId,
                AssetCode: investment.CurrencyCode,
                Quantity: null,
                Amount: amount,
                FeeAmount: null,
                FeeAssetCode: null,
                ProviderData: data,
                ProviderDataSchemaId: isDeposit ? OpenFinanceImportSchemas.InvestmentDepositSchemaId : OpenFinanceImportSchemas.InvestmentWithdrawalSchemaId,
                ProviderDataSchemaVersion: OpenFinanceImportSchemas.Version1));
        }

        var expenses = movement.Expenses;
        switch (movement.Operation)
        {
            case OpenFinanceInvestmentOperation.Application:
                Add(null, isDeposit: true, cash!.Value, movesCash: true);
                break;
            case OpenFinanceInvestmentOperation.Redemption:
            case OpenFinanceInvestmentOperation.Amortization:
                Add(null, isDeposit: false, cash!.Value, movesCash: true);
                break;
            case OpenFinanceInvestmentOperation.Income:
                Add(null, isDeposit: true, movement.GrossAmount, movesCash: false);
                break;
            case OpenFinanceInvestmentOperation.Tax:
                Add(null, isDeposit: false, movement.GrossAmount, movesCash: false);
                break;
            case OpenFinanceInvestmentOperation.TransferIn:
                Add(null, isDeposit: true, movement.GrossAmount, movesCash: false);
                break;
            case OpenFinanceInvestmentOperation.TransferOut:
                Add(null, isDeposit: false, movement.GrossAmount, movesCash: false);
                break;
        }

        // A tax line is its own tax; on every other movement the expenses are booked on their own.
        if (movement.Operation != OpenFinanceInvestmentOperation.Tax)
        {
            Add(IncomeTaxComponent, isDeposit: false, expenses.IncomeTax, movesCash: false);
            Add(IofComponent, isDeposit: false, expenses.Iof, movesCash: false);
            Add(FeeComponent, isDeposit: false, expenses.Fees, movesCash: false);
        }

        if (movement.Operation == OpenFinanceInvestmentOperation.Income && movement.PaidOut)
            Add(PayoutComponent, isDeposit: false, cash!.Value, movesCash: true);

        return events;
    }

    /// <summary>
    /// What a tax line is: <c>incomeTax</c> or <c>iof</c> when its expenses or its description say so, else <c>tax</c>.
    /// </summary>
    public static string TaxKind(OpenFinanceInvestmentMovement movement)
    {
        if (movement.Expenses is { IncomeTax: > 0m, Iof: 0m })
            return IncomeTaxComponent;
        if (movement.Expenses is { Iof: > 0m, IncomeTax: 0m })
            return IofComponent;
        var words = (movement.Description ?? string.Empty).ToUpperInvariant()
            .Split([' ', '-', '/', '.', ',', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Contains("IOF", StringComparer.Ordinal))
            return IofComponent;
        if (words.Any(w => w is "IR" or "IRRF" or "IRPF"))
            return IncomeTaxComponent;
        return "tax";
    }

    private static string BuildProviderData(
        OpenFinanceInvestment investment,
        OpenFinanceInvestmentMovement movement,
        string sourceName,
        string? component,
        bool isDeposit,
        decimal amount,
        decimal? cash,
        string? parentKey,
        OpenFinancePair? cashPair,
        bool movesCash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("provider", sourceName);
            writer.WriteString("channel", "openfinance");
            writer.WriteString("kind", isDeposit ? "investment.deposit" : "investment.withdrawal");
            writer.WriteString("status", "posted");
            writer.WriteString("sourceMovementId", movement.SourceMovementId);
            writer.WriteString("sourceInvestmentId", investment.SourceInvestmentId);
            writer.WriteString("movementTime", movement.MovementTime.ToUniversalTime());
            writer.WriteString("movementDate", movement.BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (movement.SettlementTime is { } settled)
                writer.WriteString("settlementTime", settled.ToUniversalTime());
            writer.WriteString("accountKind", "investment");
            WriteOptional(writer, "institutionWebsite", investment.InstitutionWebsite);
            writer.WriteString("operation", OperationName(movement.Operation));
            if (component is not null)
            {
                writer.WriteString("component", component);
                writer.WriteString("parentIdempotencyKey", parentKey);
            }
            else if (movement.Operation == OpenFinanceInvestmentOperation.Tax)
            {
                writer.WriteString("taxKind", TaxKind(movement));
            }

            writer.WriteNumber("grossAmount", movement.GrossAmount);
            if (cash is { } cashAmount)
                writer.WriteNumber("cashAmount", cashAmount);
            if (movement.Expenses.Total > 0m)
            {
                writer.WriteStartObject("expenses");
                writer.WriteNumber("incomeTax", movement.Expenses.IncomeTax);
                writer.WriteNumber("iof", movement.Expenses.Iof);
                writer.WriteNumber("fees", movement.Expenses.Fees);
                writer.WriteEndObject();
            }

            if (movement.ReportedNetAmount is { } net)
                writer.WriteNumber("reportedNetAmount", net);
            WriteOptional(writer, "description", movement.Description);

            writer.WriteStartObject("investment");
            writer.WriteString("externalAccountIdBasis", investment.IdentityBasis);
            writer.WriteString("type", investment.Type);
            WriteOptional(writer, "subtype", investment.Subtype);
            WriteOptional(writer, "name", investment.Name);
            WriteOptional(writer, "issuer", investment.Issuer);
            if (investment.IssuerTaxId is { } issuerTaxId)
                writer.WriteString("issuerTaxId", issuerTaxId.Value);
            WriteOptional(writer, "isin", investment.Isin);
            writer.WriteEndObject();

            if (movesCash)
            {
                writer.WriteString("transferHint", OpenFinanceTransactionMapper.InvestmentHint);
                if (cashPair is not null)
                    OpenFinanceTransactionMapper.WritePair(writer, cashPair);
            }

            if (component is null && movement.SourcePayload.ValueKind is JsonValueKind.Object)
            {
                writer.WritePropertyName("sourcePayload");
                movement.SourcePayload.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            writer.WriteString(name, value.Trim());
    }
}
