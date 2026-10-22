using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrxMoney.Integrations.Countries.Br;
using CrxMoney.Integrations.Tools.InstitutionMarks;

// Refreshes src/Countries/br/Marks from Pluggy's connector list. Usage:
//   dotnet run --project tools/InstitutionMarks -- [--source auto|public|api] [--out <marks dir>] [--review <review dir>] [--no-normalise] [--dry-run]
// public: the connector listing of https://status.pluggy.ai/ and the icons on cdn.pluggy.ai, no key (the default
//         when there are no credentials).
// api:    GET https://api.pluggy.ai/connectors, which adds each connector's institutionUrl for matching by website.
//         Needs CRX_PLUGGY_CLIENT_ID and CRX_PLUGGY_CLIENT_SECRET in the environment or in this project's user
//         secrets; they are never printed or written.
// auto:   api when both credentials are present, otherwise public.
// Each SVG is normalised after the download (#1378): a full-canvas background tile is dropped when the mark still reads
// on white and on dark, and the viewBox is tightened to the content plus a 2% margin. The step is recorded per mark in
// the manifest (normalised), and --review's directory gets a before/after contact sheet and a report of the marks the
// tool left for manual review.

var outDirectory = (string?)null;
var reviewDirectory = (string?)null;
var normalise = true;
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
        case "--review" when i + 1 < args.Length:
            reviewDirectory = args[++i];
            break;
        case "--no-normalise":
            normalise = false;
            break;
        case "--dry-run":
            dryRun = true;
            break;
        default:
            Console.Error.WriteLine("Usage: InstitutionMarks [--source auto|public|api] [--out <marks dir>] [--review <review dir>] [--no-normalise] [--dry-run]");
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
reviewDirectory ??= Path.Combine(FindRepositoryRoot(), "tools", "InstitutionMarks", "review");
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

var marks = new List<(MatchResult Match, MarkImage Image, Normalisation Normalised)>();
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
    {
        // Normalised after the download; the manifest keeps the hash of what was downloaded.
        var normalised = normalise
            ? MarkNormaliser.Normalise(image.Content, image.Extension)
            : new Normalisation(image.Content, NormaliseMethod.Unchanged, false, null, null, "normalisation switched off");
        marks.Add((match, image, normalised));
    }
}

Console.WriteLine();
Console.WriteLine($"Matched {marks.Count} of {institutions.Count} institutions; {monograms.Count} stay monograms.");
foreach (var (match, image, normalised) in marks)
{
    var others = match.Others.Count > 0 ? $" (also: {string.Join(", ", match.Others.Select(o => $"{o.Id} {o.Name}"))})" : string.Empty;
    Console.WriteLine($"  MARK  {match.Institution.Compe} {match.Institution.Name,-28} <- {match.Connector!.Id} {match.Connector.Name} [{match.Method}] {image.Extension} {image.Content.Length} -> {normalised.Content.Length} B  {normalised.Method}{others}");
}

foreach (var (institution, reason) in monograms)
    Console.WriteLine($"  MONO  {institution.Compe} {institution.Name,-28} {reason}");

var totalBytes = marks.Sum(m => (long)m.Normalised.Content.Length);
var needReview = marks.Where(m => m.Normalised.NeedsReview).ToList();
Console.WriteLine();
Console.WriteLine($"Normalised: {marks.Count(m => m.Normalised.Method is NormaliseMethod.ViewBoxTightened or NormaliseMethod.BackgroundRemovedAndTightened)}; tile dropped: {marks.Count(m => m.Normalised.BackgroundRemoved)}; kept as they are: {marks.Count(m => m.Normalised.Method is NormaliseMethod.Unchanged or NormaliseMethod.NotApplicable)}; for manual review: {needReview.Count}.");
foreach (var (match, _, normalised) in needReview)
    Console.WriteLine($"  REVIEW  {match.Institution.Compe} {match.Institution.Name,-28} {normalised.Note}");
Console.WriteLine();
Console.WriteLine($"Assets: {marks.Count} files, {totalBytes} bytes.");

if (dryRun)
{
    Console.WriteLine("Dry run: nothing written.");
    return 0;
}

Directory.CreateDirectory(outDirectory);
Directory.CreateDirectory(reviewDirectory);
foreach (var stale in Directory.EnumerateFiles(outDirectory).Where(f => Path.GetExtension(f) is ".svg" or ".png"))
    File.Delete(stale);

var timestamp = capturedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
var manifest = new JsonObject
{
    ["description"] = "Marks (logos) that identify the account operators of BrazilianInstitutions in a connect list. Each file is the icon of the matching Pluggy connector, downloaded from Pluggy's public CDN and then normalised by the tool (a full-canvas background tile dropped where the mark reads without it, the viewBox tightened to the content plus a 2% margin; shapes, fills and text are never changed); mappedFrom is the public page (or API listing) that gave the connector, sourceUrl the file's address, capturedAt when it was downloaded, bytes and sha256 describe the shipped file and normalised.originalSha256 the file as downloaded. An institution without a mark is in the monogram list and is drawn from its name and colours. Regenerated by tools/InstitutionMarks; never edited by hand.",
    ["capturedAt"] = timestamp,
    ["marks"] = new JsonArray(marks.Select(m =>
    {
        var file = $"{m.Match.Institution.Ispb}.{m.Image.Extension}";
        File.WriteAllBytes(Path.Combine(outDirectory, file), m.Normalised.Content);
        return (JsonNode)new JsonObject
        {
            ["ispb"] = m.Match.Institution.Ispb,
            ["compe"] = m.Match.Institution.Compe,
            ["institution"] = m.Match.Institution.Name,
            ["file"] = file,
            ["mediaType"] = m.Image.MediaType,
            ["bytes"] = m.Normalised.Content.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(m.Normalised.Content)),
            ["normalised"] = new JsonObject
            {
                ["viewBoxBefore"] = m.Normalised.ViewBoxBefore,
                ["viewBoxAfter"] = m.Normalised.ViewBoxAfter,
                ["backgroundRemoved"] = m.Normalised.BackgroundRemoved,
                ["method"] = m.Normalised.Method,
                ["originalBytes"] = m.Image.Content.Length,
                ["originalSha256"] = Convert.ToHexStringLower(SHA256.HashData(m.Image.Content)),
                ["backgroundFill"] = m.Normalised.BackgroundFill,
                ["note"] = m.Normalised.Note,
            },
            ["connectorId"] = m.Match.Connector!.Id,
            ["connectorName"] = m.Match.Connector.Name,
            ["matchedBy"] = m.Match.Method.ToString().ToLowerInvariant(),
            ["mappedFrom"] = mappedFrom,
            ["sourceUrl"] = m.Image.SourceUrl.AbsoluteUri,
            ["capturedAt"] = timestamp,
        };
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
File.WriteAllText(Path.Combine(outDirectory, "marks-manifest.json"), manifest.ToJsonString(options) + "\n");

if (normalise)
{
    var sheet = ContactSheet.Render(marks
        .Select(m => new SheetEntry(m.Match.Institution.Compe, m.Match.Institution.Name, m.Image.Extension, m.Image.Content, m.Normalised.Content, m.Normalised.Method))
        .ToList());
    File.WriteAllBytes(Path.Combine(reviewDirectory, "br-marks-contact-sheet.png"), sheet);
    File.WriteAllText(Path.Combine(reviewDirectory, "br-marks-normalisation.md"), NormalisationReport.Build(marks.Select(m => (m.Match.Institution, m.Normalised, MarkNormaliser.Legibility(m.Normalised.Content, m.Image.Extension))).ToList()));
    Console.WriteLine($"Wrote {reviewDirectory} (contact sheet and report)");
}

Console.WriteLine($"Wrote {outDirectory}");
return 0;

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "CrxMoney.Integrations.slnx")))
            return directory.FullName;
    }

    throw new InvalidOperationException("Run the tool from inside the repository, or pass --out.");
}
