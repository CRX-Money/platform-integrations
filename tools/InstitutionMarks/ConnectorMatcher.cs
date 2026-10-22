using System.Globalization;
using System.Text;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>How an institution was tied to a Pluggy connector.</summary>
internal enum MatchMethod
{
    None,
    /// <summary>The connector's <c>institutionUrl</c> host is the institution's website host (API listing only).</summary>
    Website,
    /// <summary>A reviewed entry of <see cref="ConnectorMatcher.Aliases"/> names the connector.</summary>
    Alias,
    /// <summary>The connector's name, without accents, case or punctuation, is the institution's name.</summary>
    Name,
    /// <summary>The same, ignoring spaces (<c>BTGPactual</c> for <c>BTG Pactual</c>).</summary>
    NameCompact,
    /// <summary>The connector's name starts with the institution's name as whole words (<c>Inter Empresas</c> for <c>Inter</c>).</summary>
    NamePrefix,
}

internal sealed record MatchResult(BrazilianInstitution Institution, PluggyConnector? Connector, MatchMethod Method, IReadOnlyList<PluggyConnector> Others);

/// <summary>Matches the institutions of the Brazilian package to Pluggy connectors by website host, alias and name.</summary>
internal static class ConnectorMatcher
{
    private static readonly HashSet<string> PublicSuffixes = new(StringComparer.Ordinal) { "com.br", "gov.br", "org.br", "net.br", "edu.br", "coop.br" };

    /// <summary>
    /// Institutions whose Pluggy connector does not carry their name, by COMPE code to connector id, reviewed by hand
    /// against the connector's public listing. A name rule that would pick the wrong connector (Caixa matching
    /// "Caixa Tem") is pinned here too.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Aliases = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["104"] = 619, // Caixa: "Caixa Econômica Federal"
        ["121"] = 678, // Agibank: Pluggy lists it as "Agi"
        ["329"] = 694, // QI Tech: "QI SCD" (QI Sociedade de Crédito Direto)
        ["413"] = 716, // Banco BV: "BV - Pessoa Física - APP"
        ["655"] = 716, // Banco BV (Votorantim): the same BV connector
        ["637"] = 714, // Sofisa: "Banco Sofisa"
    };

    /// <summary>The host of a URL in lower case without a leading <c>www.</c>, or <c>null</c> when it is not an absolute URL.</summary>
    public static string? NormalizeHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>
    /// True when both hosts are the same site: equal, or one a subdomain of the other
    /// (<c>internetbanking.caixa.gov.br</c> of <c>caixa.gov.br</c>), never through a public suffix.
    /// </summary>
    public static bool SameSite(string? a, string? b)
    {
        if (a is null || b is null)
            return false;
        if (a == b)
            return true;
        var (longer, shorter) = a.Length >= b.Length ? (a, b) : (b, a);
        return shorter.Contains('.') && !PublicSuffixes.Contains(shorter) && longer.EndsWith("." + shorter, StringComparison.Ordinal);
    }

    /// <summary>Lower case, no accents, letters and digits only, single spaces (<c>Itaú Unibanco</c> becomes <c>itau unibanco</c>).</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The institution's name without a trailing explanation: <c>Toro (Santander Corretora)</c> is <c>Toro</c>.</summary>
    public static string InstitutionName(string name)
    {
        var parenthesis = name.IndexOf('(', StringComparison.Ordinal);
        return NormalizeName(parenthesis > 0 ? name[..parenthesis] : name);
    }

    public static IReadOnlyList<MatchResult> Match(IEnumerable<BrazilianInstitution> institutions, IReadOnlyList<PluggyConnector> connectors, IReadOnlyDictionary<string, int>? aliases = null)
    {
        var candidates = connectors.Where(c => !string.IsNullOrWhiteSpace(c.ImageUrl) && !c.IsSandbox).ToList();
        return institutions.Select(i => Match(i, candidates, aliases ?? Aliases)).ToList();
    }

    private static MatchResult Match(BrazilianInstitution institution, IReadOnlyList<PluggyConnector> connectors, IReadOnlyDictionary<string, int> aliases)
    {
        var host = NormalizeHost(institution.Website.AbsoluteUri);
        var name = InstitutionName(institution.Name);

        var byWebsite = connectors.Where(c => SameSite(host, NormalizeHost(c.InstitutionUrl))).ToList();
        if (byWebsite.Count > 0)
            return Pick(institution, byWebsite, MatchMethod.Website, name);

        if (aliases.TryGetValue(institution.Compe, out var aliasId))
        {
            var aliased = connectors.Where(c => c.Id == aliasId).ToList();
            // An alias whose connector has left the listing is reported, not silently replaced by a guess.
            return aliased.Count > 0
                ? new MatchResult(institution, aliased[0], MatchMethod.Alias, [])
                : new MatchResult(institution, null, MatchMethod.None, []);
        }

        var byName = connectors.Where(c => NormalizeName(c.Name) == name).ToList();
        if (byName.Count > 0)
            return Pick(institution, byName, MatchMethod.Name, name);

        var compact = name.Replace(" ", string.Empty, StringComparison.Ordinal);
        var byCompact = connectors.Where(c => NormalizeName(c.Name).Replace(" ", string.Empty, StringComparison.Ordinal) == compact).ToList();
        if (byCompact.Count > 0)
            return Pick(institution, byCompact, MatchMethod.NameCompact, name);

        var prefixed = connectors.Where(c => NormalizeName(c.Name).StartsWith(name + " ", StringComparison.Ordinal)).ToList();
        if (prefixed.Count > 0)
            return Pick(institution, prefixed, MatchMethod.NamePrefix, name);

        return new MatchResult(institution, null, MatchMethod.None, []);
    }

    /// <summary>
    /// The connector whose name is exactly the institution's, else a personal one over a business one, else the
    /// shortest name ("Inter" over "Inter Empresas"), then the lowest id.
    /// </summary>
    private static MatchResult Pick(BrazilianInstitution institution, List<PluggyConnector> found, MatchMethod method, string name)
    {
        var ordered = found
            .OrderByDescending(c => NormalizeName(c.Name) == name)
            .ThenBy(c => string.Equals(c.Type, "business", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => NormalizeName(c.Name).Length)
            .ThenBy(c => c.Id)
            .ToList();
        return new MatchResult(institution, ordered[0], method, ordered.Skip(1).ToList());
    }
}
