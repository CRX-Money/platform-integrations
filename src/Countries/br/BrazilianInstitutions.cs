using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrxMoney.Integrations.Countries.Br;

/// <summary>A Brazilian financial institution as the Open Finance package knows it.</summary>
/// <param name="Compe">Three-digit COMPE code (<c>077</c>).</param>
/// <param name="Ispb">Eight-digit ISPB.</param>
/// <param name="Name">Display name (<c>Inter</c>).</param>
/// <param name="LegalName">Legal name as the Banco Central lists it.</param>
/// <param name="Website">The institution's official website, a canonical <c>https://host/</c> URL.</param>
public sealed record BrazilianInstitution(string Compe, string Ispb, string Name, string LegalName, Uri Website)
{
    /// <summary>
    /// The embedded resource holding this institution's mark (logo), or <c>null</c> when it has none and is drawn as a
    /// monogram. Open it with <see cref="BrazilianInstitutionMarks.TryOpen"/>.
    /// </summary>
    public string? MarkResourceName => BrazilianInstitutionMarks.ResourceNameOf(Ispb);
}

/// <summary>
/// The institutions of <c>Data/br-institutions.json</c>, shipped inside this package: a COMPE code's
/// display name and official website: the account operators. It is local data of the Brazilian country package, so bank
/// codes stay out of the platform's own database; the host only sees the website, which it can match to an account operator.
/// Codes come from the Banco Central's list of STR participants; the file says where and when.
/// </summary>
public static class BrazilianInstitutions
{
    /// <summary>The embedded resource holding the list.</summary>
    public const string ResourceName = "CrxMoney.Integrations.Countries.Br.br-institutions.json";

    private static readonly Lazy<FrozenDictionary<string, BrazilianInstitution>> ByCompe = new(() => Parse(ReadResource()));

    /// <summary>Every institution in the list, by COMPE code.</summary>
    public static IReadOnlyCollection<BrazilianInstitution> All => ByCompe.Value.Values;

    /// <summary>The institution with COMPE code <paramref name="compe"/> (<c>77</c> or <c>077</c>), or <c>null</c> when the list does not have it.</summary>
    public static BrazilianInstitution? Find(string? compe)
    {
        if (string.IsNullOrWhiteSpace(compe))
            return null;
        try
        {
            return ByCompe.Value.GetValueOrDefault(BrazilianBankAccount.NormalizeCompe(compe));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The official website of the institution with COMPE code <paramref name="compe"/>, when known.</summary>
    public static string? WebsiteOf(string? compe) => Find(compe)?.Website.AbsoluteUri;

    /// <summary>Reads the embedded list as JSON text.</summary>
    internal static string ReadResource()
    {
        using var stream = typeof(BrazilianInstitutions).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Parses and checks a list: COMPE three digits, ISPB eight digits, both unique, a name and a legal name, and a
    /// website that is an absolute <c>https</c> URL of a host only (<c>https://inter.co/</c>).
    /// </summary>
    /// <exception cref="FormatException">An entry breaks one of those rules; the message names it.</exception>
    internal static FrozenDictionary<string, BrazilianInstitution> Parse(string json)
    {
        InstitutionList? list;
        try
        {
            list = JsonSerializer.Deserialize(json, InstitutionsJsonContext.Default.InstitutionList);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The institution list is not valid JSON.", ex);
        }

        if (list?.Institutions is not { Count: > 0 } entries)
            throw new FormatException("The institution list has no institutions.");

        var result = new Dictionary<string, BrazilianInstitution>(StringComparer.Ordinal);
        var ispbs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var label = entry.Compe ?? "(no code)";
            if (entry.Compe is not { Length: 3 } compe || !compe.All(char.IsAsciiDigit))
                throw new FormatException($"Institution {label}: the COMPE code must be three digits.");
            if (entry.Ispb is not { Length: 8 } ispb || !ispb.All(char.IsAsciiDigit))
                throw new FormatException($"Institution {label}: the ISPB must be eight digits.");
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.LegalName))
                throw new FormatException($"Institution {label}: the name and the legal name are required.");
            if (!Uri.TryCreate(entry.Website, UriKind.Absolute, out var website)
                || website.Scheme != Uri.UriSchemeHttps
                || website.AbsolutePath != "/" || website.Query.Length > 0 || website.Fragment.Length > 0
                || !website.IsDefaultPort || website.UserInfo.Length > 0
                || entry.Website != website.AbsoluteUri)
            {
                throw new FormatException($"Institution {label}: the website must be a canonical https://host/ URL.");
            }

            if (!result.TryAdd(compe, new BrazilianInstitution(compe, ispb, entry.Name.Trim(), entry.LegalName.Trim(), website)))
                throw new FormatException($"Institution {label}: the COMPE code appears twice.");
            if (!ispbs.Add(ispb))
                throw new FormatException($"Institution {label}: the ISPB appears twice.");
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal sealed record InstitutionList(
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("retrievedOn")] string? RetrievedOn,
        [property: JsonPropertyName("institutions")] IReadOnlyList<InstitutionEntry>? Institutions);

    internal sealed record InstitutionEntry(
        [property: JsonPropertyName("compe")] string? Compe,
        [property: JsonPropertyName("ispb")] string? Ispb,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("legalName")] string? LegalName,
        [property: JsonPropertyName("website")] string? Website);
}

[JsonSerializable(typeof(BrazilianInstitutions.InstitutionList))]
internal sealed partial class InstitutionsJsonContext : JsonSerializerContext;
