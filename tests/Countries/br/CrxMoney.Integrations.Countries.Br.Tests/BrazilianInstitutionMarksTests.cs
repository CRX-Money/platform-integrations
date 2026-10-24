using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CrxMoney.Integrations.Countries.Br.Tests;

public sealed class BrazilianInstitutionMarksTests
{
    private static JsonNode Manifest() => JsonNode.Parse(BrazilianInstitutionMarks.ReadManifest())!;

    private static List<JsonNode> Marks() => Manifest()["marks"]!.AsArray().Select(m => m!).ToList();

    private static List<JsonNode> Monograms() => Manifest()["monogram"]!.AsArray().Select(m => m!).ToList();

    private static byte[] Read(string resourceName)
    {
        using var stream = typeof(BrazilianInstitutionMarks).Assembly.GetManifestResourceStream(resourceName)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public void Every_institution_has_a_mark_or_is_in_the_documented_monogram_list()
    {
        var withMark = Marks().Select(m => m["ispb"]!.GetValue<string>()).ToList();
        var monogram = Monograms().Select(m => m["ispb"]!.GetValue<string>()).ToList();

        Assert.Empty(withMark.Intersect(monogram));
        Assert.Equal(withMark.Count, withMark.Distinct().Count());
        Assert.Equal(monogram.Count, monogram.Distinct().Count());
        Assert.Equal(
            BrazilianInstitutions.All.Select(i => i.Ispb).Order(StringComparer.Ordinal),
            withMark.Concat(monogram).Order(StringComparer.Ordinal));
        Assert.All(Monograms(), m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m["reason"]!.GetValue<string>()));
            Assert.Equal(BrazilianInstitutions.Find(m["compe"]!.GetValue<string>())!.Ispb, m["ispb"]!.GetValue<string>());
        });
    }

    [Fact]
    public void The_manifest_and_the_embedded_marks_describe_the_same_files()
    {
        var marks = Marks();

        Assert.Equal(
            marks.Select(m => m["ispb"]!.GetValue<string>()).Order(StringComparer.Ordinal),
            BrazilianInstitutionMarks.Keys.Order(StringComparer.Ordinal));
        Assert.All(marks, m =>
        {
            var ispb = m["ispb"]!.GetValue<string>();
            var institution = BrazilianInstitutions.Find(m["compe"]!.GetValue<string>());
            Assert.NotNull(institution);
            Assert.Equal(ispb, institution.Ispb);

            var resource = BrazilianInstitutionMarks.ResourceNameOf(ispb);
            Assert.NotNull(resource);
            Assert.Equal(BrazilianInstitutionMarks.ResourcePrefix + m["file"]!.GetValue<string>(), resource);
            Assert.Equal(resource, institution.MarkResourceName);

            var content = Read(resource);
            Assert.Equal(m["bytes"]!.GetValue<int>(), content.Length);
            Assert.Equal(m["sha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(content)));
            Assert.Equal(m["mediaType"]!.GetValue<string>(), BrazilianInstitutionMarks.MediaTypeOf(resource));
            Assert.Contains(m["mediaType"]!.GetValue<string>(), new[] { "image/svg+xml", "image/png" });
        });
    }

    [Fact]
    public void Every_mark_records_where_and_when_it_was_captured()
    {
        var manifest = Manifest();
        var marks = Marks();
        if (marks.Count == 0)
            return; // Nothing captured yet; the monogram list covers everyone.

        Assert.True(DateTimeOffset.TryParse(manifest["capturedAt"]!.GetValue<string>(), out _));
        Assert.All(marks, m =>
        {
            // The public page (or API listing) the mapping came from, and the CDN file it points to.
            var mappedFrom = new Uri(m["mappedFrom"]!.GetValue<string>(), UriKind.Absolute);
            Assert.Equal(Uri.UriSchemeHttps, mappedFrom.Scheme);
            Assert.Contains(m["mappedFrom"]!.GetValue<string>(), new[] { "https://status.pluggy.ai/", "https://api.pluggy.ai/connectors" });
            Assert.True(m["connectorId"]!.GetValue<int>() > 0);
            Assert.False(string.IsNullOrWhiteSpace(m["connectorName"]!.GetValue<string>()));
            var source = new Uri(m["sourceUrl"]!.GetValue<string>(), UriKind.Absolute);
            Assert.Equal(Uri.UriSchemeHttps, source.Scheme);
            Assert.True(DateTimeOffset.TryParseExact(m["capturedAt"]!.GetValue<string>(), "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _));
            Assert.Contains(m["matchedBy"]!.GetValue<string>(), new[] { "website", "alias", "name", "namecompact", "nameprefix" });
        });
    }

    [Fact]
    public void Every_mark_records_how_it_was_normalised_and_the_file_carries_the_viewBox_it_records()
    {
        var methods = new[] { "viewbox-tightened", "background-removed+viewbox-tightened", "unchanged", "manual-review", "not-applicable" };
        Assert.All(Marks(), m =>
        {
            var normalised = m["normalised"];
            Assert.NotNull(normalised);
            var method = normalised["method"]!.GetValue<string>();
            Assert.Contains(method, methods);
            var removed = normalised["backgroundRemoved"]!.GetValue<bool>();
            Assert.Equal(method == "background-removed+viewbox-tightened", removed);
            Assert.Matches("^[0-9a-f]{64}$", normalised["originalSha256"]!.GetValue<string>());
            Assert.True(normalised["originalBytes"]!.GetValue<int>() > 0);

            // A file that was not changed is the file as downloaded; one that was changed has its own hash.
            var changed = method is "viewbox-tightened" or "background-removed+viewbox-tightened";
            Assert.Equal(!changed, normalised["originalSha256"]!.GetValue<string>() == m["sha256"]!.GetValue<string>());

            if (m["mediaType"]!.GetValue<string>() != "image/svg+xml")
                return;

            var svg = Encoding.UTF8.GetString(Read(BrazilianInstitutionMarks.ResourceNameOf(m["ispb"]!.GetValue<string>())!));
            var viewBox = System.Text.RegularExpressions.Regex.Match(svg, """<svg\b[^>]*?\sviewBox\s*=\s*["']([^"']+)["']""");
            Assert.True(viewBox.Success, "an SVG mark keeps a viewBox");
            Assert.Equal(normalised["viewBoxAfter"]!.GetValue<string>(), viewBox.Groups[1].Value.Trim());
            Assert.Equal(changed, normalised["viewBoxBefore"]!.GetValue<string>() != normalised["viewBoxAfter"]!.GetValue<string>());
        });
    }

    [Fact]
    public void Embedded_marks_are_real_png_or_clean_svg_images()
    {
        foreach (var key in BrazilianInstitutionMarks.Keys)
        {
            Assert.True(BrazilianInstitutionMarks.TryOpen(key, out var stream, out var mediaType));
            using (stream)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                Assert.InRange(bytes.Length, 1, 256 * 1024);
                if (mediaType == "image/png")
                {
                    Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
                }
                else
                {
                    var svg = Encoding.UTF8.GetString(bytes);
                    Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("javascript:", svg, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("<foreignObject", svg, StringComparison.OrdinalIgnoreCase);
                    Assert.Matches("^(?s)(?!.*\\son[a-z]+\\s*=).*$", svg);
                }
            }
        }
    }

    [Fact]
    public void A_mark_is_found_by_ispb_or_by_compe_and_an_unknown_key_has_none()
    {
        foreach (var institution in BrazilianInstitutions.All)
        {
            var expected = BrazilianInstitutionMarks.Keys.Contains(institution.Ispb);
            Assert.Equal(expected, institution.MarkResourceName is not null);
            Assert.Equal(expected, BrazilianInstitutionMarks.TryOpen(institution.Ispb, out var byIspb, out _));
            Assert.Equal(expected, BrazilianInstitutionMarks.TryOpen(institution.Compe, out var byCompe, out _));
            byIspb?.Dispose();
            byCompe?.Dispose();
        }

        Assert.False(BrazilianInstitutionMarks.TryOpen("99999999", out var none, out var type));
        Assert.Null(none);
        Assert.Null(type);
        Assert.False(BrazilianInstitutionMarks.TryOpen(null, out _, out _));
        Assert.False(BrazilianInstitutionMarks.TryOpen("  ", out _, out _));
        Assert.False(BrazilianInstitutionMarks.TryOpen("../../etc/passwd", out _, out _));
        Assert.Null(BrazilianInstitutionMarks.ResourceNameOf("not-a-key"));
    }
}
