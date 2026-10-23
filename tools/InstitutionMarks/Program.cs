using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Countries.Br;
using CrxMoney.Integrations.Tools.InstitutionMarks;

// Refreshes src/Countries/br/Marks from Pluggy's connector list. Usage:
//   dotnet run --project tools/InstitutionMarks -- [--source auto|public|api] [--out <marks dir>] [--dry-run]
// public: the connector listing of https://status.pluggy.ai/ and the icons on cdn.pluggy.ai, no key (the default
//         when there are no credentials).
// api:    GET https://api.pluggy.ai/connectors, which adds each connector's institutionUrl for matching by website.
//         Needs CRX_PLUGGY_CLIENT_ID and CRX_PLUGGY_CLIENT_SECRET in the environment or in this project's user
//         secrets; they are never printed or written.
// auto:   api when both credentials are present, otherwise public.

var outDirectory = (string?)null;
var dryRun = false;
var sourceChoice = "auto";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length:
            outDirectory = args[++i];
            break;
        case "--source" when i + 1 < args.Length && args[i + 1] is "auto" or "public" or "api":
            sourceChoice = args[++i];
            break;
        case "--dry-run":
            dryRun = true;
            break;
        default:
            Console.Error.WriteLine("Usage: InstitutionMarks [--source auto|public|api] [--out <marks dir>] [--dry-run]");
            return 2;
    }
}

var clientId = Credentials.Read(Credentials.ClientIdName);
var clientSecret = Credentials.Read(Credentials.ClientSecretName);
var hasCredentials = !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
if (sourceChoice == "api" && !hasCredentials)
{
    Console.Error.WriteLine("--source api needs CRX_PLUGGY_CLIENT_ID and CRX_PLUGGY_CLIENT_SECRET in the environment, or set with dotnet user-secrets set <name> <value> --project tools/InstitutionMarks.");
    return 2;
}

var useApi = sourceChoice == "api" || (sourceChoice == "auto" && hasCredentials);
outDirectory ??= Path.Combine(FindRepositoryRoot(), "src", "Countries", "br", "Marks");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("CrxMoney-InstitutionMarks/1.0");

string mappedFrom;
IReadOnlyList<PluggyConnector> connectors;
if (useApi)
{
    mappedFrom = "https://api.pluggy.ai/connectors";
    connectors = await new PluggyClient(http).ListBrazilianConnectorsAsync(clientId!, clientSecret!, CancellationToken.None);
}
else
{
    mappedFrom = PluggyStatusPage.Url.AbsoluteUri;
    connectors = await PluggyStatusPage.FetchAsync(http, CancellationToken.None);
}

Console.WriteLine($"{mappedFrom} lists {connectors.Count} Brazilian connectors.");

var capturedAt = DateTimeOffset.UtcNow;
var fetcher = new MarkFetcher(http);
var institutions = BrazilianInstitutions.All.OrderBy(i => i.Compe, StringComparer.Ordinal).ToList();
var matches = ConnectorMatcher.Match(institutions, connectors);

var marks = new List<(MatchResult Match, MarkImage Image)>();
var monograms = new List<(BrazilianInstitution Institution, string Reason)>();
foreach (var match in matches)
{
    if (match.Connector is null)
    {
        monograms.Add((match.Institution, $"no Pluggy connector matched ({mappedFrom})"));
        continue;
    }

    var (image, failure) = await fetcher.FetchAsync(match.Connector.ImageUrl!, CancellationToken.None);
    if (image is null)
        monograms.Add((match.Institution, $"connector {match.Connector.Id} has no usable image: {failure}"));
    else
        marks.Add((match, image));
}

Console.WriteLine();
Console.WriteLine($"Matched {marks.Count} of {institutions.Count} institutions; {monograms.Count} stay monograms.");
foreach (var (match, image) in marks)
{
    var others = match.Others.Count > 0 ? $" (also: {string.Join(", ", match.Others.Select(o => $"{o.Id} {o.Name}"))})" : string.Empty;
    Console.WriteLine($"  MARK  {match.Institution.Compe} {match.Institution.Name,-28} <- {match.Connector!.Id} {match.Connector.Name} [{match.Method}] {image.Extension} {image.Content.Length} B{others}");
}

foreach (var (institution, reason) in monograms)
    Console.WriteLine($"  MONO  {institution.Compe} {institution.Name,-28} {reason}");

// #1379: the business (PJ) connectors next to each institution's main one, with their own icons when usable.
var variants = ConnectorMatcher.MatchBusiness(matches, connectors);
var businessMarks = new List<(BusinessVariant Variant, MarkImage? Image, string? Failure)>();
foreach (var variant in variants)
{
    var (image, failure) = string.IsNullOrWhiteSpace(variant.Business.ImageUrl)
        ? (null, "no icon listed")
        : await fetcher.FetchAsync(variant.Business.ImageUrl!, CancellationToken.None);
    businessMarks.Add((variant, image, image is null ? failure : null));
}

Console.WriteLine();
Console.WriteLine($"Business connectors: {businessMarks.Count} ({businessMarks.Count(b => b.Image is not null)} with a mark).");
foreach (var (variant, image, failure) in businessMarks)
    Console.WriteLine($"  PJ    {variant.Institution.Compe} {variant.Institution.Name,-28} <- {variant.Business.Id} {variant.Business.Name}"
        + (image is null ? $" (no mark: {failure})" : $" {image.Extension} {image.Content.Length} B"));

var totalBytes = marks.Sum(m => (long)m.Image.Content.Length) + businessMarks.Sum(b => (long)(b.Image?.Content.Length ?? 0));
Console.WriteLine();
Console.WriteLine($"Assets: {marks.Count + businessMarks.Count(b => b.Image is not null)} files, {totalBytes} bytes.");

if (dryRun)
{
    Console.WriteLine("Dry run: nothing written.");
    return 0;
}

// A mark whose bytes did not change keeps the capture time the manifest already records for it.
var manifestPath = Path.Combine(outDirectory, "marks-manifest.json");
var previousCapture = PreviousCaptureTimes(manifestPath);
string CapturedAt(string key, byte[] content, string now) =>
    previousCapture.TryGetValue(key, out var previous) && previous.Sha256 == Convert.ToHexStringLower(SHA256.HashData(content))
        ? previous.CapturedAt
        : now;

Directory.CreateDirectory(outDirectory);
foreach (var stale in Directory.EnumerateFiles(outDirectory).Where(f => Path.GetExtension(f) is ".svg" or ".png"))
    File.Delete(stale);
var connectorsDirectory = Path.Combine(outDirectory, "connectors");
Directory.CreateDirectory(connectorsDirectory);
foreach (var stale in Directory.EnumerateFiles(connectorsDirectory).Where(f => Path.GetExtension(f) is ".svg" or ".png"))
    File.Delete(stale);

var timestamp = capturedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
var manifest = new JsonObject
{
    ["description"] = "Marks (logos) that identify the account operators of BrazilianInstitutions in a connect list, and (connectors) the business (PJ) connectors next to an institution's main one, with their own marks in connectors/. Each file is the icon of the matching Pluggy connector, downloaded from Pluggy's public CDN; mappedFrom is the public page (or API listing) that gave the connector, sourceUrl the file's address and capturedAt when it was downloaded. An institution without a mark is in the monogram list and is drawn from its name and colours. Regenerated by tools/InstitutionMarks; never edited by hand.",
    ["capturedAt"] = timestamp,
    ["marks"] = new JsonArray(marks.Select(m =>
    {
        var file = $"{m.Match.Institution.Ispb}.{m.Image.Extension}";
        File.WriteAllBytes(Path.Combine(outDirectory, file), m.Image.Content);
        return (JsonNode)new JsonObject
        {
            ["ispb"] = m.Match.Institution.Ispb,
            ["compe"] = m.Match.Institution.Compe,
            ["institution"] = m.Match.Institution.Name,
            ["file"] = file,
            ["mediaType"] = m.Image.MediaType,
            ["bytes"] = m.Image.Content.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(m.Image.Content)),
            ["connectorId"] = m.Match.Connector!.Id,
            ["connectorName"] = m.Match.Connector.Name,
            ["matchedBy"] = m.Match.Method.ToString().ToLowerInvariant(),
            ["mappedFrom"] = mappedFrom,
            ["sourceUrl"] = m.Image.SourceUrl.AbsoluteUri,
            ["capturedAt"] = CapturedAt($"mark:{m.Match.Institution.Ispb}", m.Image.Content, timestamp),
        };
    }).ToArray()),
    ["connectors"] = new JsonArray(businessMarks.Select(b =>
    {
        var entry = new JsonObject
        {
            ["connectorId"] = b.Variant.Business.Id,
            ["connectorName"] = b.Variant.Business.Name,
            ["kind"] = "business",
            ["mainConnectorId"] = b.Variant.Main.Id,
            ["ispb"] = b.Variant.Institution.Ispb,
            ["compe"] = b.Variant.Institution.Compe,
            ["institution"] = b.Variant.Institution.Name,
            ["mappedFrom"] = mappedFrom,
            ["capturedAt"] = timestamp,
        };
        if (b.Image is { } image)
        {
            var file = $"{b.Variant.Business.Id}.{image.Extension}";
            File.WriteAllBytes(Path.Combine(connectorsDirectory, file), image.Content);
            entry["file"] = $"connectors/{file}";
            entry["mediaType"] = image.MediaType;
            entry["bytes"] = image.Content.Length;
            entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(image.Content));
            entry["sourceUrl"] = image.SourceUrl.AbsoluteUri;
            entry["capturedAt"] = CapturedAt($"connector:{b.Variant.Business.Id}", image.Content, timestamp);
        }
        else
        {
            entry["reason"] = UnusableIconReason(b.Variant.Business, b.Failure);
        }

        return (JsonNode)entry;
    }).ToArray()),
    ["monogram"] = new JsonArray(monograms.Select(m => (JsonNode)new JsonObject
    {
        ["ispb"] = m.Institution.Ispb,
        ["compe"] = m.Institution.Compe,
        ["institution"] = m.Institution.Name,
        ["reason"] = m.Reason,
    }).ToArray()),
};

var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
File.WriteAllText(manifestPath, manifest.ToJsonString(options) + "\n");
Console.WriteLine($"Wrote {outDirectory}");
return 0;

// "connector 781 lists the icon 620.svg, which is not usable: contains a foreignObject": the icon Pluggy lists for the
// connector, by its own file name, since a business connector may list its main connector's icon.
static string UnusableIconReason(PluggyConnector connector, string? failure)
{
    var icon = connector.ImageUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : null;
    var detail = failure is null ? "unknown" : failure.Contains(": ", StringComparison.Ordinal) && failure.StartsWith("http", StringComparison.Ordinal)
        ? failure[(failure.IndexOf(": ", StringComparison.Ordinal) + 2)..]
        : failure;
    return icon is null
        ? $"connector {connector.Id} lists no icon"
        : $"connector {connector.Id} lists the icon {icon}, which is not usable: {detail}";
}

// The capture time and hash of each mark the manifest already records ("mark:{ispb}", "connector:{id}").
static Dictionary<string, (string Sha256, string CapturedAt)> PreviousCaptureTimes(string manifestPath)
{
    var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
    if (!File.Exists(manifestPath))
        return result;
    var root = JsonNode.Parse(File.ReadAllText(manifestPath));
    void Read(string section, string keyProperty, string prefix)
    {
        foreach (var entry in root?[section]?.AsArray() ?? [])
        {
            var key = entry?[keyProperty]?.ToJsonString().Trim('"');
            var sha = entry?["sha256"]?.GetValue<string>();
            var at = entry?["capturedAt"]?.GetValue<string>();
            if (key is not null && sha is not null && at is not null)
                result[$"{prefix}:{key}"] = (sha, at);
        }
    }

    Read("marks", "ispb", "mark");
    Read("connectors", "connectorId", "connector");
    return result;
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "CrxMoney.Integrations.slnx")))
            return directory.FullName;
    }

    throw new InvalidOperationException("Run the tool from inside the repository, or pass --out.");
}
