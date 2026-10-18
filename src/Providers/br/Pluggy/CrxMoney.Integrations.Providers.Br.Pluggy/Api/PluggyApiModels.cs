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
    [property: JsonPropertyName("taxNumber")] string? TaxNumber = null);

internal sealed record PluggyBankData([property: JsonPropertyName("transferNumber")] string? TransferNumber);

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
internal sealed partial class PluggyJsonContext : JsonSerializerContext;
