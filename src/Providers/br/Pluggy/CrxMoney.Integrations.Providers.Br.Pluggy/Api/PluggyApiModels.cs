using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrxMoney.Integrations.Providers.Br.Pluggy.Api;

// Wire models for the Pluggy API (api.pluggy.ai), limited to the fields the adapter reads.
// Field names were checked against a live lab item; values are never committed.

internal sealed record PluggyAuthRequest(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("clientSecret")] string ClientSecret);

internal sealed record PluggyAuthResponse([property: JsonPropertyName("apiKey")] string? ApiKey);

internal sealed record PluggyItem(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("executionStatus")] string? ExecutionStatus,
    [property: JsonPropertyName("lastUpdatedAt")] DateTimeOffset? LastUpdatedAt,
    [property: JsonPropertyName("consentExpiresAt")] DateTimeOffset? ConsentExpiresAt,
    [property: JsonPropertyName("products")] IReadOnlyList<string>? Products,
    [property: JsonPropertyName("connector")] PluggyConnector? Connector,
    [property: JsonPropertyName("error")] PluggyItemError? Error);

internal sealed record PluggyConnector(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("isOpenFinance")] bool? IsOpenFinance,
    [property: JsonPropertyName("isSandbox")] bool? IsSandbox);

internal sealed record PluggyItemError(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record PluggyAccountsPage([property: JsonPropertyName("results")] IReadOnlyList<PluggyAccount>? Results);

internal sealed record PluggyAccount(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("balance")] decimal? Balance,
    [property: JsonPropertyName("currencyCode")] string? CurrencyCode,
    [property: JsonPropertyName("bankData")] PluggyBankData? BankData,
    [property: JsonPropertyName("taxNumber")] string? TaxNumber = null,
    [property: JsonPropertyName("creditData")] PluggyCreditData? CreditData = null);

internal sealed record PluggyBankData([property: JsonPropertyName("transferNumber")] string? TransferNumber);

// Dates are documented as yyyy-mm-dd and typed date-time in the OpenAPI file: read as text, the date part is used.
internal sealed record PluggyCreditData(
    [property: JsonPropertyName("creditLimit")] decimal? CreditLimit,
    [property: JsonPropertyName("availableCreditLimit")] decimal? AvailableCreditLimit,
    [property: JsonPropertyName("minimumPayment")] decimal? MinimumPayment,
    [property: JsonPropertyName("balanceCloseDate")] string? BalanceCloseDate,
    [property: JsonPropertyName("balanceDueDate")] string? BalanceDueDate);

/// <summary>The page-numbered envelope of <c>/investments</c> and <c>/investments/{id}/transactions</c>.</summary>
internal sealed record PluggyNumberedPage(
    [property: JsonPropertyName("page")] double? Page,
    [property: JsonPropertyName("total")] double? Total,
    [property: JsonPropertyName("totalPages")] double? TotalPages,
    [property: JsonPropertyName("results")] IReadOnlyList<JsonElement>? Results);

internal sealed record PluggyInvestment(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("isin")] string? Isin,
    [property: JsonPropertyName("currencyCode")] string? CurrencyCode,
    [property: JsonPropertyName("balance")] decimal? Balance,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("amountOriginal")] decimal? AmountOriginal,
    [property: JsonPropertyName("amountProfit")] decimal? AmountProfit,
    [property: JsonPropertyName("taxes")] decimal? Taxes,
    [property: JsonPropertyName("taxes2")] decimal? Taxes2,
    [property: JsonPropertyName("date")] DateTimeOffset? Date,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("issuerCNPJ")] string? IssuerCnpj,
    [property: JsonPropertyName("issueDate")] DateTimeOffset? IssueDate,
    [property: JsonPropertyName("dueDate")] DateTimeOffset? DueDate,
    [property: JsonPropertyName("rate")] decimal? Rate,
    [property: JsonPropertyName("rateType")] string? RateType,
    [property: JsonPropertyName("status")] string? Status);

internal sealed record PluggyInvestmentTransaction(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("movementType")] string? MovementType,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("netAmount")] decimal? NetAmount,
    [property: JsonPropertyName("date")] DateTimeOffset? Date,
    [property: JsonPropertyName("tradeDate")] DateTimeOffset? TradeDate,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("expenses")] PluggyInvestmentExpenses? Expenses);

// Every field Pluggy documents; iofProvision (accrued, not paid) is deliberately not read.
internal sealed record PluggyInvestmentExpenses(
    [property: JsonPropertyName("incomeTax")] decimal? IncomeTax,
    [property: JsonPropertyName("iof")] decimal? Iof,
    [property: JsonPropertyName("serviceTax")] decimal? ServiceTax,
    [property: JsonPropertyName("brokerageFee")] decimal? BrokerageFee,
    [property: JsonPropertyName("tradingAssetsNoticeFee")] decimal? TradingAssetsNoticeFee,
    [property: JsonPropertyName("maintenanceFee")] decimal? MaintenanceFee,
    [property: JsonPropertyName("settlementFee")] decimal? SettlementFee,
    [property: JsonPropertyName("clearingFee")] decimal? ClearingFee,
    [property: JsonPropertyName("stockExchangeFee")] decimal? StockExchangeFee,
    [property: JsonPropertyName("custodyFee")] decimal? CustodyFee,
    [property: JsonPropertyName("operatingFee")] decimal? OperatingFee,
    [property: JsonPropertyName("other")] decimal? Other);

internal sealed record PluggyTransactionsPage(
    [property: JsonPropertyName("results")] IReadOnlyList<JsonElement>? Results,
    [property: JsonPropertyName("next")] string? Next);

internal sealed record PluggyTransaction(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("date")] DateTimeOffset? Date,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("descriptionRaw")] string? DescriptionRaw,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("operationType")] string? OperationType,
    [property: JsonPropertyName("operationTypeAdditionalInfo")] string? OperationTypeAdditionalInfo,
    [property: JsonPropertyName("paymentData")] PluggyPaymentData? PaymentData,
    [property: JsonPropertyName("merchant")] PluggyMerchant? Merchant,
    [property: JsonPropertyName("creditCardMetadata")] PluggyCardMetadata? CreditCardMetadata);

internal sealed record PluggyPaymentData(
    [property: JsonPropertyName("payer")] PluggyParty? Payer,
    [property: JsonPropertyName("receiver")] PluggyParty? Receiver,
    [property: JsonPropertyName("paymentMethod")] string? PaymentMethod);

internal sealed record PluggyParty(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("documentNumber")] PluggyDocument? DocumentNumber,
    [property: JsonPropertyName("routingNumberISPB")] string? RoutingNumberIspb,
    [property: JsonPropertyName("branchNumber")] string? BranchNumber,
    [property: JsonPropertyName("accountNumber")] string? AccountNumber);

internal sealed record PluggyDocument(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("value")] string? Value);

internal sealed record PluggyMerchant(
    [property: JsonPropertyName("businessName")] string? BusinessName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("cnpj")] string? Cnpj);

internal sealed record PluggyCardMetadata(
    [property: JsonPropertyName("cardNumber")] string? CardNumber,
    [property: JsonPropertyName("billId")] string? BillId,
    [property: JsonPropertyName("transactionDateTime")] DateTimeOffset? TransactionDateTime);

internal sealed record PluggyErrorResponse(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("code")] int? Code);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(PluggyAuthRequest))]
[JsonSerializable(typeof(PluggyAuthResponse))]
[JsonSerializable(typeof(PluggyItem))]
[JsonSerializable(typeof(PluggyAccountsPage))]
[JsonSerializable(typeof(PluggyTransactionsPage))]
[JsonSerializable(typeof(PluggyTransaction))]
[JsonSerializable(typeof(PluggyErrorResponse))]
[JsonSerializable(typeof(PluggyNumberedPage))]
[JsonSerializable(typeof(PluggyInvestment))]
[JsonSerializable(typeof(PluggyInvestmentTransaction))]
internal sealed partial class PluggyJsonContext : JsonSerializerContext;
