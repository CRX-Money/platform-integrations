using System.Text;
using System.Text.RegularExpressions;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>A downloaded mark: the bytes, the file extension they get in the package and the media type they are served as.</summary>
internal sealed record MarkImage(byte[] Content, string Extension, string MediaType, Uri SourceUrl);

/// <summary>Recognises and vets the image a connector points to. Only SVG and PNG are kept; anything else stays a monogram.</summary>
internal static partial class MarkImages
{
    public const long MaxBytes = 256 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The content's kind decided from its bytes, not from the server's headers; <c>null</c> when it is neither PNG nor SVG.</summary>
    public static (string Extension, string MediaType)? Sniff(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(PngSignature))
            return ("png", "image/png");
        var head = Encoding.UTF8.GetString(content[..Math.Min(content.Length, 2048)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            || (head.StartsWith("<!--", StringComparison.Ordinal) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            ? ("svg", "image/svg+xml")
            : null;
    }

    /// <summary>
    /// Why an SVG is not safe to ship, or <c>null</c> when it is: no script, event handlers, foreign objects,
    /// external references or entity declarations. The host serves marks as images with a sandboxing policy as well;
    /// this keeps the package itself clean.
    /// </summary>
    public static string? UnsafeSvgReason(string svg)
    {
        if (ScriptPattern().IsMatch(svg))
            return "contains a script";
        if (EventHandlerPattern().IsMatch(svg))
            return "contains an event handler";
        if (svg.Contains("<foreignObject", StringComparison.OrdinalIgnoreCase))
            return "contains a foreignObject";
        if (svg.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
            return "declares entities";
        if (ExternalReferencePattern().IsMatch(svg))
            return "references an external resource";
        return null;
    }

    /// <summary>Why <paramref name="content"/> cannot be kept, or <c>null</c> when it can.</summary>
    public static string? Reject(byte[] content)
    {
        if (content.Length == 0)
            return "the image is empty";
        if (content.Length > MaxBytes)
            return $"the image is {content.Length} bytes, over the {MaxBytes} byte limit";
        var kind = Sniff(content);
        if (kind is null)
            return "the image is neither SVG nor PNG";
        return kind.Value.Extension == "svg" ? UnsafeSvgReason(Encoding.UTF8.GetString(content)) : null;
    }

    [GeneratedRegex(@"<\s*script\b|javascript:", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPattern();

    [GeneratedRegex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerPattern();

    // href / src / url(...) that point anywhere but inside the document (#id) or at an embedded data: image.
    [GeneratedRegex(@"(?:href|src)\s*=\s*[""'](?!#|data:image/)|url\(\s*[""']?(?!#|data:image/)", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalReferencePattern();
}

/// <summary>Downloads the image of a connector, trying an SVG sibling first when the connector points at a PNG.</summary>
internal sealed class MarkFetcher(HttpClient http)
{
    public async Task<(MarkImage? Image, string? Failure)> FetchAsync(string imageUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return (null, $"the image URL is not an https URL ({imageUrl})");

        var attempts = new List<Uri>();
        if (url.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            attempts.Add(new UriBuilder(url) { Path = url.AbsolutePath[..^4] + ".svg" }.Uri);
        attempts.Add(url);

        string? failure = null;
        foreach (var attempt in attempts)
        {
            try
            {
                using var response = await http.GetAsync(attempt, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    failure = $"{attempt} answered HTTP {(int)response.StatusCode}";
                    continue;
                }

                var content = await response.Content.ReadAsByteArrayAsync(ct);
                if (MarkImages.Reject(content) is { } reason)
                {
                    failure = $"{attempt}: {reason}";
                    continue;
                }

                var (extension, mediaType) = MarkImages.Sniff(content)!.Value;
                return (new MarkImage(content, extension, mediaType, attempt), null);
            }
            catch (HttpRequestException ex)
            {
                failure = $"{attempt}: {ex.Message}";
            }
        }

        return (null, failure);
    }
}
