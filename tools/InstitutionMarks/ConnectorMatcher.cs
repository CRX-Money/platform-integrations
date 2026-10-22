using System.Globalization;
using System.Text;
using CrxMoney.Integrations.Countries.Br;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>How an institution was tied to a Pluggy connector.</summary>
internal enum MatchMethod
{
    None,
    /// <summary>The connector's <c>institutionUrl</c> host is the institution's website host.</summary>
    Website,
    /// <summary>The connector's name, without accents, case or punctuation, is the institution's name.</summary>
    Name,
    /// <summary>The connector's name starts with the institution's name as whole words (<c>Inter Empresas</c> for <c>Inter</c>).</summary>
    NamePrefix,
}

internal sealed record MatchResult(BrazilianInstitution Institution, PluggyConnector? Connector, MatchMethod Method, IReadOnlyList<PluggyConnector> Others);

/// <summary>Matches the institutions of the Brazilian package to Pluggy connectors by website host, then by name.</summary>
internal static class ConnectorMatcher
{
    private static readonly HashSet<string> PublicSuffixes = new(StringComparer.Ordinal) { "com.br", "gov.br", "org.br", "net.br", "edu.br", "coop.br" };

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

    public static IReadOnlyList<MatchResult> Match(IEnumerable<BrazilianInstitution> institutions, IReadOnlyList<PluggyConnector> connectors)
    {
        var candidates = connectors.Where(c => !string.IsNullOrWhiteSpace(c.ImageUrl)).ToList();
        return institutions.Select(i => Match(i, candidates)).ToList();
    }

    private static MatchResult Match(BrazilianInstitution institution, IReadOnlyList<PluggyConnector> connectors)
    {
        var host = NormalizeHost(institution.Website.AbsoluteUri);
        var name = NormalizeName(institution.Name);

        var byWebsite = connectors.Where(c => SameSite(host, NormalizeHost(c.InstitutionUrl))).ToList();
        if (byWebsite.Count > 0)
            return Pick(institution, byWebsite, MatchMethod.Website, name);

        var byName = connectors.Where(c => NormalizeName(c.Name) == name).ToList();
        if (byName.Count > 0)
            return Pick(institution, byName, MatchMethod.Name, name);

        var prefixed = connectors.Where(c => NormalizeName(c.Name).StartsWith(name + " ", StringComparison.Ordinal)).ToList();
        if (prefixed.Count > 0)
            return Pick(institution, prefixed, MatchMethod.NamePrefix, name);

        return new MatchResult(institution, null, MatchMethod.None, []);
    }

    /// <summary>The connector whose name is exactly the institution's, else the shortest name (a personal connector over "... Empresas"), then the lowest id.</summary>
    private static MatchResult Pick(BrazilianInstitution institution, List<PluggyConnector> found, MatchMethod method, string name)
    {
        var ordered = found
            .OrderByDescending(c => NormalizeName(c.Name) == name)
            .ThenBy(c => NormalizeName(c.Name).Length)
            .ThenBy(c => c.Id)
            .ToList();
        return new MatchResult(institution, ordered[0], method, ordered.Skip(1).ToList());
    }
}
