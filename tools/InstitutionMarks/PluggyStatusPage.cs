using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>
/// Pluggy's public status page, <c>https://status.pluggy.ai/</c>, lists every Brazilian connector with its id, name,
/// type and the CDN address of its icon, with no key. It carries no institution website, so institutions are matched
/// to it by name (and by the alias table of <see cref="ConnectorMatcher"/>).
/// The page is a Next.js app: its data travels as JSON strings inside <c>self.__next_f.push([1,"…"])</c> scripts.
/// </summary>
internal static partial class PluggyStatusPage
{
    public static readonly Uri Url = new("https://status.pluggy.ai/");

    /// <summary>The connectors the page's data lists, in page order. Empty when the page has no recognisable data.</summary>
    public static IReadOnlyList<PluggyConnector> Parse(string html)
    {
        var data = new System.Text.StringBuilder();
        foreach (Match chunk in FlightChunk().Matches(html))
        {
            try
            {
                data.Append(JsonSerializer.Deserialize<string>($"\"{chunk.Groups[1].Value}\""));
            }
            catch (JsonException)
            {
                // A chunk that is not a JSON string is not page data.
            }
        }

        var seen = new HashSet<int>();
        var connectors = new List<PluggyConnector>();
        foreach (Match item in Institution().Matches(data.ToString()))
        {
            if (!int.TryParse(item.Groups["id"].Value, out var id) || !seen.Add(id))
                continue;
            var image = item.Groups["logo"].Value;
            // The sandbox connector (MeuPluggy) is not an institution.
            if (image.Contains("/sandbox.", StringComparison.Ordinal))
                continue;
            connectors.Add(new PluggyConnector(id, item.Groups["name"].Value, InstitutionUrl: null, image, "BR", item.Groups["type"].Value, IsSandbox: false));
        }

        return connectors;
    }

    /// <summary>Reads the page; fails loudly when its shape changed and no connector can be found.</summary>
    public static async Task<IReadOnlyList<PluggyConnector>> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var html = await http.GetStringAsync(Url, ct);
        var connectors = Parse(html);
        if (connectors.Count == 0)
            throw new InvalidOperationException($"{Url} lists no connectors; the page's format may have changed.");
        return connectors;
    }

    [GeneratedRegex(@"self\.__next_f\.push\(\[1,""((?:[^""\\]|\\.)*)""\]\)")]
    private static partial Regex FlightChunk();

    [GeneratedRegex(@"\{""id"":""(?<id>\d+)"",""pluggyId"":""\d+"",""name"":""(?<name>[^""]*)"",""type"":""(?<type>[^""]*)"",""isOpenFinance"":(?:true|false),""logoUrl"":""(?<logo>https://[^""]*)""")]
    private static partial Regex Institution();
}
