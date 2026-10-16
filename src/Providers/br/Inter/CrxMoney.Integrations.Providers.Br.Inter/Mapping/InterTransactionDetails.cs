using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CrxMoney.Integrations.Providers.Br.Inter.Mapping;

/// <summary>The counterparty and Pix identifiers found in one transaction's <c>detalhes</c>.</summary>
internal sealed record InterCounterparty(
    string? Name,
    BrazilianTaxId? TaxId,
    string? Bank,
    string? Branch,
    string? Account)
{
    public bool IsEmpty => Name is null && TaxId is null && Bank is null && Branch is null && Account is null;
}

internal sealed record InterPixIdentifiers(string? EndToEndId, string? TxId, string? Key)
{
    public bool IsEmpty => EndToEndId is null && TxId is null && Key is null;
}

/// <summary>
/// Reads the counterparty out of <c>detalhes</c>. Inter's portal and SDKs leave that object untyped and
/// its keys vary with <c>tipoTransacao</c>, so fields are recognised by the words in their key path
/// (<c>nomePagador</c>, <c>pagador.cpfCnpj</c>, <c>chavePixRecebedor</c>, ...) rather than by a fixed list:
/// <list type="bullet">
/// <item>the role is <i>payer</i> (pagador, remetente, origem, depositante, ordenante) or <i>payee</i>
/// (recebedor, beneficiario, favorecido, destinatario, destino); for a credit the counterparty is the
/// payer, for a debit the payee. Fields of the other role are the account holder and are ignored;
/// fields without a role are used only when no field of the counterparty's role exists for that item;</item>
/// <item>a tax id is used only when its CPF/CNPJ check digits are valid, so a masked or misread value never
/// becomes an identifier.</item>
/// </list>
/// The full <c>detalhes</c> object is always kept verbatim in the provider data, so nothing is lost when a
/// key is not recognised.
/// </summary>
internal static class InterTransactionDetailsReader
{
    private const int MaxDepth = 4;
    private const int MaxValueLength = 200;

    private static readonly string[] PayerWords = ["pagador", "remetente", "origem", "depositante", "ordenante"];
    private static readonly string[] PayeeWords = ["recebedor", "beneficiario", "favorecido", "destinatario", "destino"];

    public static (InterCounterparty Counterparty, InterPixIdentifiers Pix) Read(JsonElement details, bool isCredit)
    {
        var fields = new List<Field>();
        if (details.ValueKind == JsonValueKind.Object)
            Collect(details, [], fields, depth: 0);

        var counterpartyRole = isCredit ? Role.Payer : Role.Payee;

        string? Pick(FieldKind kind)
        {
            string? roleless = null;
            foreach (var field in fields)
            {
                if (field.Kind != kind)
                    continue;
                if (field.Role == counterpartyRole)
                    return field.Value;
                if (field.Role == Role.None)
                    roleless ??= field.Value;
            }

            return roleless;
        }

        BrazilianTaxId? PickTaxId()
        {
            BrazilianTaxId? roleless = null;
            foreach (var field in fields)
            {
                if (field.Kind != FieldKind.TaxId || BrazilianTaxId.TryParse(field.Value) is not { } taxId)
                    continue;
                if (field.Role == counterpartyRole)
                    return taxId;
                if (field.Role == Role.None)
                    roleless ??= taxId;
            }

            return roleless;
        }

        var counterparty = new InterCounterparty(
            Pick(FieldKind.Name),
            PickTaxId(),
            Pick(FieldKind.Bank),
            Pick(FieldKind.Branch),
            Pick(FieldKind.Account));

        var pix = new InterPixIdentifiers(
            Pick(FieldKind.EndToEndId),
            Pick(FieldKind.TxId),
            Pick(FieldKind.PixKey));

        return (counterparty, pix);
    }

    private static void Collect(JsonElement element, List<string> path, List<Field> fields, int depth)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = NormalizeKey(property.Name);
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object when depth < MaxDepth:
                    path.Add(key);
                    Collect(property.Value, path, fields, depth + 1);
                    path.RemoveAt(path.Count - 1);
                    break;
                case JsonValueKind.String or JsonValueKind.Number:
                    var value = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()?.Trim()
                        : property.Value.GetRawText();
                    if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
                        break;
                    var kind = KindOf(key);
                    if (kind != FieldKind.Unknown)
                        fields.Add(new Field(kind, RoleOf(key, path), value));
                    break;
            }
        }
    }

    /// <summary>The leaf key decides the kind; the role comes from the leaf key first, then the nearest parent.</summary>
    private static FieldKind KindOf(string key)
    {
        // "tipoChavePix": "EMAIL", "tipoConta": "CORRENTE" describe a value; they are not one.
        if (key.StartsWith("tipo", StringComparison.Ordinal))
            return FieldKind.Unknown;
        if (key.Contains("endtoend", StringComparison.Ordinal) || key.Contains("e2e", StringComparison.Ordinal))
            return FieldKind.EndToEndId;
        if (key.Contains("txid", StringComparison.Ordinal))
            return FieldKind.TxId;
        if (key.Contains("chave", StringComparison.Ordinal))
            return FieldKind.PixKey;
        if (key.Contains("cpf", StringComparison.Ordinal) || key.Contains("cnpj", StringComparison.Ordinal))
            return FieldKind.TaxId;
        // Bank before name: "nomeBanco", "nomeInstituicao" and, in Inter's documented Pix sample,
        // "nomeEmpresaPagador"/"nomeEmpresaRecebedor" ("Banco Inter", "CAIXA ...") name the bank, not the person.
        if (key.Contains("banco", StringComparison.Ordinal)
            || key.Contains("instituicao", StringComparison.Ordinal)
            || key.Contains("ispb", StringComparison.Ordinal)
            || key.Contains("nomeempresa", StringComparison.Ordinal))
            return FieldKind.Bank;
        if (key.Contains("nome", StringComparison.Ordinal) || key.Contains("razaosocial", StringComparison.Ordinal))
            return FieldKind.Name;
        if (key.Contains("agencia", StringComparison.Ordinal))
            return FieldKind.Branch;
        if (key.Contains("conta", StringComparison.Ordinal))
            return FieldKind.Account;
        return FieldKind.Unknown;
    }

    private static Role RoleOf(string key, List<string> path)
    {
        if (RoleIn(key) is var leafRole and not Role.None)
            return leafRole;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (RoleIn(path[i]) is var parentRole and not Role.None)
                return parentRole;
        }

        return Role.None;
    }

    private static Role RoleIn(string key)
    {
        foreach (var word in PayerWords)
        {
            if (key.Contains(word, StringComparison.Ordinal))
                return Role.Payer;
        }

        foreach (var word in PayeeWords)
        {
            if (key.Contains(word, StringComparison.Ordinal))
                return Role.Payee;
        }

        return Role.None;
    }

    /// <summary>Lower-case ASCII letters and digits only: <c>"CPF/CNPJ Beneficiário"</c> becomes <c>"cpfcnpjbeneficiario"</c>.</summary>
    internal static string NormalizeKey(string key)
    {
        var decomposed = key.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsAsciiLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private enum Role
    {
        None,
        Payer,
        Payee,
    }

    private enum FieldKind
    {
        Unknown,
        Name,
        TaxId,
        Bank,
        Branch,
        Account,
        EndToEndId,
        TxId,
        PixKey,
    }

    private sealed record Field(FieldKind Kind, Role Role, string Value);
}
