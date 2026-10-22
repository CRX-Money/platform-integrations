using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace CrxMoney.Integrations.Countries.Br;

/// <summary>An aggregator's connector to one institution of <see cref="BrazilianInstitutions"/>.</summary>
/// <param name="Id">The aggregator's connector id (Pluggy <c>225</c>).</param>
/// <param name="Name">The connector's name (<c>Inter Empresas</c>).</param>
/// <param name="IsBusiness">The connector serves companies' accounts (PJ).</param>
/// <param name="Institution">The institution it belongs to.</param>
public sealed record BrazilianInstitutionConnector(string Id, string Name, bool IsBusiness, BrazilianInstitution Institution);

/// <summary>
/// The connectors an aggregator offers for the institutions of <see cref="BrazilianInstitutions"/>: the main (personal)
/// one, whose icon is the institution's mark, and the business (PJ) variants, which have marks of their own
/// (<see cref="BrazilianInstitutionMarks.TryOpenConnector"/>). Read from <c>marks-manifest.json</c>, which
/// tools/InstitutionMarks writes from the aggregator's public connector list (Pluggy's status page); the aggregator is
/// Pluggy today, the only one whose connectors CRX distinguishes.
/// </summary>
/// <remarks>
/// A plugin uses it when the aggregator does not say which connector an item reads: through Meu Pluggy every item
/// names the MeuPluggy connector, so the bank (COMPE of its accounts) and whether its accounts belong to a company (a
/// CNPJ) pick the connector here.
/// </remarks>
public static class BrazilianInstitutionConnectors
{
    private static readonly Lazy<Catalog> Data = new(Load);

    /// <summary>Every connector the manifest lists, main ones first.</summary>
    public static IReadOnlyList<BrazilianInstitutionConnector> All => Data.Value.All;

    /// <summary>The connector with this aggregator id, or <c>null</c>.</summary>
    public static BrazilianInstitutionConnector? Find(string? id) =>
        id is not null && Data.Value.ById.TryGetValue(id.Trim(), out var connector) ? connector : null;

    /// <summary>
    /// The connector of the institution with this COMPE code or ISPB for personal or business accounts, or <c>null</c>
    /// when the manifest has none (the institution has no business connector, or the list was not captured yet).
    /// </summary>
    public static BrazilianInstitutionConnector? For(string? institutionKey, bool isBusiness)
    {
        if (FindInstitution(institutionKey) is not { } institution)
            return null;
        return Data.Value.All.FirstOrDefault(c => c.Institution.Ispb == institution.Ispb && c.IsBusiness == isBusiness);
    }

    /// <summary>The business (PJ) connector of an institution, when there is one.</summary>
    public static bool TryGetBusiness(string? institutionKey, [NotNullWhen(true)] out BrazilianInstitutionConnector? connector)
    {
        connector = For(institutionKey, isBusiness: true);
        return connector is not null;
    }

    /// <summary>An institution by COMPE code (<c>077</c>) or ISPB (<c>00416968</c>).</summary>
    private static BrazilianInstitution? FindInstitution(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        key = key.Trim();
        return key.Length == 8 && key.All(char.IsAsciiDigit)
            ? BrazilianInstitutions.All.FirstOrDefault(i => i.Ispb == key)
            : BrazilianInstitutions.Find(key);
    }

    private sealed record Catalog(IReadOnlyList<BrazilianInstitutionConnector> All, FrozenDictionary<string, BrazilianInstitutionConnector> ById);

    private static Catalog Load()
    {
        using var document = JsonDocument.Parse(BrazilianInstitutionMarks.ReadManifest());
        var all = new List<BrazilianInstitutionConnector>();
        Read(document.RootElement, "marks", isBusiness: false, all);
        Read(document.RootElement, "connectors", isBusiness: null, all);
        var byId = all.GroupBy(c => c.Id, StringComparer.Ordinal).ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return new Catalog(all.DistinctBy(c => c.Id).ToList(), byId);
    }

    private static void Read(JsonElement root, string section, bool? isBusiness, List<BrazilianInstitutionConnector> into)
    {
        if (!root.TryGetProperty(section, out var entries) || entries.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in entries.EnumerateArray())
        {
            var id = entry.TryGetProperty("connectorId", out var idValue)
                ? idValue.ValueKind == JsonValueKind.Number ? idValue.GetRawText() : idValue.GetString()
                : null;
            var name = entry.TryGetProperty("connectorName", out var nameValue) ? nameValue.GetString() : null;
            var compe = entry.TryGetProperty("compe", out var compeValue) ? compeValue.GetString() : null;
            var business = isBusiness ?? (entry.TryGetProperty("kind", out var kind) && kind.GetString() == "business");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || BrazilianInstitutions.Find(compe) is not { } institution)
                continue;
            into.Add(new BrazilianInstitutionConnector(id.Trim(), name.Trim(), business, institution));
        }
    }
}
