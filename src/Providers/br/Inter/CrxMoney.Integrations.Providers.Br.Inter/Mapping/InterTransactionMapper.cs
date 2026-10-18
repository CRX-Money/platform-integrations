using System.Globalization;
using System.Text;
using System.Text.Json;
using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.Inter.Api;

namespace CrxMoney.Integrations.Providers.Br.Inter.Mapping;

/// <summary>
/// Turns enriched statement entries into <see cref="IntegrationImportEvent"/>s, the same shape the
/// platform's bank-statement (OFX) import produces: <c>deposit</c>/<c>withdrawal</c>, a positive BRL amount,
/// the account number as external account, and the booking date at midnight in Brazil.
/// </summary>
internal static class InterTransactionMapper
{
    public const string AssetCode = "BRL";
    public const string DepositType = "deposit";
    public const string WithdrawalType = "withdrawal";
    public const string Side = "Single";
    public const string InvestmentTransactionType = "INVESTIMENTO";

    /// <summary>The time zone whose calendar Inter books in.</summary>
    public static readonly TimeZoneInfo BankingTimeZone = FindBankingTimeZone();

    /// <summary>
    /// The idempotency recipe: connection + Inter's transaction id. Changing it in a released version
    /// would import every movement again (docs/versioning.md, "Provider packages").
    /// </summary>
    public static string IdempotencyKey(long connectionId, string transactionId) =>
        string.Create(CultureInfo.InvariantCulture, $"inter:{connectionId}:{transactionId}");

    /// <exception cref="InterMappingException">The entry lacks a field the import cannot do without.</exception>
    public static IntegrationImportEvent Map(InterTransaction transaction, long connectionId, string accountNumber, InterEnvironment environment)
    {
        var transactionId = transaction.IdTransacao?.Trim();
        if (string.IsNullOrEmpty(transactionId))
            throw new InterMappingException("An Inter statement entry has no idTransacao.");

        var bookingDate = ParseBookingDate(transaction.DataTransacao)
            ?? throw new InterMappingException($"Inter statement entry {transactionId} has no valid dataTransacao.");

        var isCredit = transaction.TipoOperacao?.Trim().ToUpperInvariant() switch
        {
            "C" => true,
            "D" => false,
            _ => throw new InterMappingException($"Inter statement entry {transactionId} has tipoOperacao '{transaction.TipoOperacao}', expected C or D."),
        };

        var amount = ParseAmount(transaction.Valor)
            ?? throw new InterMappingException($"Inter statement entry {transactionId} has no valid valor.");

        var type = isCredit ? DepositType : WithdrawalType;
        var (counterparty, pix) = InterTransactionDetailsReader.Read(transaction.Detalhes, isCredit);

        return new IntegrationImportEvent(
            ExternalId: transactionId,
            IdempotencyKey: IdempotencyKey(connectionId, transactionId),
            Type: type,
            Side: Side,
            OccurredAtUtc: StartOfBankingDay(bookingDate),
            ExternalAccountId: accountNumber,
            AssetCode: AssetCode,
            Quantity: null,
            Amount: Math.Abs(amount),
            FeeAmount: null,
            FeeAssetCode: null,
            ProviderData: BuildProviderData(transaction, transactionId, bookingDate, isCredit, environment, counterparty, pix),
            ProviderDataSchemaId: isCredit ? InterImportSchemas.DepositSchemaId : InterImportSchemas.WithdrawalSchemaId,
            ProviderDataSchemaVersion: InterImportSchemas.Version1);
    }

    /// <summary>
    /// Midnight of the booking date in Brazil, as an instant: <c>2026-03-10</c> becomes
    /// <c>2026-03-10T00:00-03:00</c>, so the date reads the same in the account's own calendar.
    /// </summary>
    public static DateTimeOffset StartOfBankingDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, BankingTimeZone.GetUtcOffset(local));
    }

    /// <summary>The Brazilian calendar date of an instant (pass the current time for "today").</summary>
    public static DateOnly BankingDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, BankingTimeZone).DateTime);

    // IANA ids resolve on Linux, macOS and Windows with ICU; Windows in NLS mode only knows its own id.
    private static TimeZoneInfo FindBankingTimeZone() =>
        TimeZoneInfo.TryFindSystemTimeZoneById("America/Sao_Paulo", out var zone)
            ? zone
            : TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");

    internal static DateOnly? ParseBookingDate(string? value)
    {
        // Documented as YYYY-MM-DD; tolerate a trailing time ("2026-03-10T14:00:00" or "2026-03-10 14:00:00").
        if (value is null || value.Length < 10)
            return null;
        return DateOnly.TryParseExact(value.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    internal static decimal? ParseAmount(JsonElement value)
    {
        // The portal documents valor as a string ("1234.56"); accept a JSON number too.
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string BuildProviderData(
        InterTransaction transaction,
        string transactionId,
        DateOnly bookingDate,
        bool isCredit,
        InterEnvironment environment,
        InterCounterparty counterparty,
        InterPixIdentifiers pix)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("provider", "Inter");
            writer.WriteString("kind", isCredit ? "fiat.deposit" : "fiat.withdrawal");
            writer.WriteString("status", "posted");
            writer.WriteString("environment", environment == InterEnvironment.Production ? "production" : "sandbox");
            writer.WriteString("transactionId", transactionId);
            writer.WriteString("transactionDate", bookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            WriteOptional(writer, "inclusionDate", transaction.DataInclusao);
            WriteOptional(writer, "tipoOperacao", transaction.TipoOperacao?.Trim().ToUpperInvariant());
            WriteOptional(writer, "tipoTransacao", transaction.TipoTransacao);
            WriteOptional(writer, "titulo", transaction.Titulo);
            WriteOptional(writer, "descricao", transaction.Descricao);
            WriteOptional(writer, "numeroDocumento", ScalarText(transaction.NumeroDocumento));

            if (string.Equals(transaction.TipoTransacao?.Trim(), InvestmentTransactionType, StringComparison.OrdinalIgnoreCase))
                writer.WriteString("transferHint", "investment");

            if (!counterparty.IsEmpty)
            {
                writer.WriteStartObject("counterparty");
                WriteOptional(writer, "name", counterparty.Name);
                if (counterparty.TaxId is { } taxId)
                {
                    writer.WriteString("taxId", taxId.Value);
                    writer.WriteString("taxIdKind", taxId.Kind);
                    writer.WriteString("taxIdCountry", "BR");
                }

                WriteOptional(writer, "bank", counterparty.Bank);
                WriteOptional(writer, "branch", counterparty.Branch);
                WriteOptional(writer, "account", counterparty.Account);
                writer.WriteEndObject();
            }

            if (!pix.IsEmpty)
            {
                writer.WriteStartObject("pix");
                WriteOptional(writer, "endToEndId", pix.EndToEndId);
                WriteOptional(writer, "txId", pix.TxId);
                WriteOptional(writer, "key", pix.Key);
                writer.WriteEndObject();
            }

            if (transaction.Detalhes.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                writer.WritePropertyName("detalhes");
                transaction.Detalhes.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            writer.WriteString(name, value.Trim());
    }
}

/// <summary>An entry cannot be mapped. The message names the entry by Inter's id only, never by personal data.</summary>
internal sealed class InterMappingException(string message) : Exception(message);
