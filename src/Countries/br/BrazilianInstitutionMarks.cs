using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace CrxMoney.Integrations.Countries.Br;

/// <summary>
/// The marks (logos) that identify the institutions of <see cref="BrazilianInstitutions"/> in a connect list, shipped
/// inside this package as embedded resources next to <c>marks-manifest.json</c>, which records where and when each
/// one was captured. An institution without a mark is in the manifest's <c>monogram</c> list: the host draws it from
/// its name and colours instead. A mark is identified by its institution's ISPB (<c>00416968</c>); a COMPE code
/// (<c>077</c>) is accepted as well and resolved through <see cref="BrazilianInstitutions"/>.
/// </summary>
public static class BrazilianInstitutionMarks
{
    /// <summary>The prefix of every mark resource: <c>{prefix}{ispb}.svg</c> or <c>{prefix}{ispb}.png</c>.</summary>
    public const string ResourcePrefix = "CrxMoney.Integrations.Countries.Br.marks.";

    /// <summary>The embedded resource holding the provenance manifest.</summary>
    public const string ManifestResourceName = ResourcePrefix + "marks-manifest.json";

    private static readonly Lazy<FrozenDictionary<string, string>> ResourceByIspb = new(Discover);

    /// <summary>The ISPBs that have a mark.</summary>
    public static IReadOnlyCollection<string> Keys => ResourceByIspb.Value.Keys;

    /// <summary>The embedded resource name of the mark of <paramref name="key"/> (an ISPB or a COMPE code), or <c>null</c> when there is none.</summary>
    public static string? ResourceNameOf(string? key)
    {
        var ispb = ResolveIspb(key);
        return ispb is not null && ResourceByIspb.Value.TryGetValue(ispb, out var name) ? name : null;
    }

    /// <summary>
    /// Opens the mark of <paramref name="key"/> (an ISPB or a COMPE code). The caller disposes <paramref name="content"/>.
    /// Returns <c>false</c> when the institution has no mark and is drawn as a monogram.
    /// </summary>
    public static bool TryOpen(string? key, [NotNullWhen(true)] out Stream? content, [NotNullWhen(true)] out string? mediaType)
    {
        content = null;
        mediaType = null;
        if (ResourceNameOf(key) is not { } resource)
            return false;
        content = typeof(BrazilianInstitutionMarks).Assembly.GetManifestResourceStream(resource);
        if (content is null)
            return false;
        mediaType = MediaTypeOf(resource);
        return true;
    }

    /// <summary>The prefix of a connector's own mark: <c>{prefix}{connector id}.svg</c> (Pluggy's business connectors, #1379).</summary>
    public const string ConnectorResourcePrefix = ResourcePrefix + "connectors.";

    /// <summary>
    /// Opens the mark of an aggregator's connector by its id (<see cref="BrazilianInstitutionConnectors"/>): the
    /// business (PJ) variants carry their own icon (Inter Empresas). Returns <c>false</c> when the connector has none,
    /// and the host shows the institution's mark instead.
    /// </summary>
    public static bool TryOpenConnector(string? connectorId, [NotNullWhen(true)] out Stream? content, [NotNullWhen(true)] out string? mediaType)
    {
        content = null;
        mediaType = null;
        if (string.IsNullOrWhiteSpace(connectorId) || !connectorId.Trim().All(char.IsAsciiDigit))
            return false;
        foreach (var extension in new[] { ".svg", ".png" })
        {
            var resource = ConnectorResourcePrefix + connectorId.Trim() + extension;
            content = typeof(BrazilianInstitutionMarks).Assembly.GetManifestResourceStream(resource);
            if (content is not null)
            {
                mediaType = MediaTypeOf(resource);
                return true;
            }
        }

        return false;
    }

    /// <summary>The media type of a mark resource by its extension.</summary>
    internal static string MediaTypeOf(string resourceName) =>
        resourceName.EndsWith(".svg", StringComparison.Ordinal) ? "image/svg+xml" : "image/png";

    /// <summary>Reads the provenance manifest as JSON text.</summary>
    internal static string ReadManifest()
    {
        using var stream = typeof(BrazilianInstitutionMarks).Assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ManifestResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? ResolveIspb(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        key = key.Trim();
        return key.Length == 8 && key.All(char.IsAsciiDigit) ? key : BrazilianInstitutions.Find(key)?.Ispb;
    }

    private static FrozenDictionary<string, string> Discover()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in typeof(BrazilianInstitutionMarks).Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;
            var file = resource[ResourcePrefix.Length..];
            var dot = file.LastIndexOf('.');
            if (dot != 8 || file[dot..] is not (".svg" or ".png") || !file[..dot].All(char.IsAsciiDigit))
                continue;
            result[file[..dot]] = resource;
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
