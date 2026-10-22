using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>How a mark was handled by <see cref="MarkNormaliser"/>; the value is the manifest's <c>normalised.method</c>.</summary>
internal static class NormaliseMethod
{
    /// <summary>A full-canvas background was dropped and the viewBox tightened to the content.</summary>
    public const string BackgroundRemovedAndTightened = "background-removed+viewbox-tightened";

    /// <summary>The viewBox was tightened to the content; there was no background to drop.</summary>
    public const string ViewBoxTightened = "viewbox-tightened";

    /// <summary>Already tight (or its coloured tile is the mark): the file is kept byte for byte.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>The tool could not normalise it safely: the file is kept byte for byte and listed in the review report.</summary>
    public const string ManualReview = "manual-review";

    /// <summary>Not an SVG (a PNG would need pixel work): kept as it is.</summary>
    public const string NotApplicable = "not-applicable";
}

/// <summary>What <see cref="MarkNormaliser"/> did to one mark, as the manifest records it.</summary>
/// <param name="Content">The bytes to ship: the original ones unless the mark was changed.</param>
/// <param name="ViewBoxBefore">The file's viewBox, or the one its width and height imply; null when it has none.</param>
/// <param name="ViewBoxAfter">The viewBox of <paramref name="Content"/>.</param>
/// <param name="Note">Why a mark was kept or left for review, or what its background was.</param>
internal sealed record Normalisation(
    byte[] Content,
    string Method,
    bool BackgroundRemoved,
    string? ViewBoxBefore,
    string? ViewBoxAfter,
    string? Note = null,
    string? BackgroundFill = null)
{
    public bool NeedsReview => Method == NormaliseMethod.ManualReview;
}

/// <summary>
/// Cleans the canvas of an institution mark (#1378): drops a full-canvas background tile when the mark still reads on
/// white and on dark without it, then tightens the viewBox to what is drawn plus a 2% margin, so every mark fills its
/// box the same way. The marks are the institutions' trademarks and are used for identification only: paths, shapes,
/// fills and gradients are never touched, only the viewBox (with the width and height that follow it) and the one
/// background element. What the tool cannot do safely it leaves as it is and reports.
/// </summary>
internal static class MarkNormaliser
{
    /// <summary>The margin around the content, as a share of its longer side.</summary>
    public const double MarginShare = 0.02;

    /// <summary>A tile covers the canvas when this share of both its sides, and of its area, is covered.</summary>
    private const double TileCoverage = 0.9;

    private const double TileExtent = 0.98;

    /// <summary>A mark reads on a background when this share of what is drawn has contrast ≥ <see cref="MinimumContrast"/> against it.</summary>
    private const double ReadsShare = 0.7;

    private const double MinimumContrast = 1.6;

    /// <summary>The viewBox moves less than this share of its size: the file is already tight.</summary>
    private const double Tolerance = 0.015;

    private const int BoundsPixels = 1024;
    private const int ProbePixels = 256;

    private static readonly SKColor White = new(255, 255, 255);

    /// <summary>The dark card the host draws marks on, near enough: #14161a.</summary>
    private static readonly SKColor Dark = new(0x14, 0x16, 0x1A);

    private static readonly HashSet<string> NotDrawn = new(StringComparer.Ordinal)
    {
        "defs", "clipPath", "mask", "pattern", "symbol", "marker", "style", "title", "desc", "metadata", "linearGradient",
        "radialGradient", "filter", "script", "font", "view", "switch"
    };

    private static readonly HashSet<string> Shapes = new(StringComparer.Ordinal)
    {
        "rect", "path", "polygon", "polyline", "circle", "ellipse", "line", "use", "image", "text"
    };

    public static Normalisation Normalise(byte[] content, string extension)
    {
        if (!string.Equals(extension, "svg", StringComparison.OrdinalIgnoreCase))
            return Keep(content, NormaliseMethod.NotApplicable, null, "only SVG marks are normalised");

        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(content), LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            return Keep(content, NormaliseMethod.ManualReview, null, $"the SVG is not well-formed XML ({ex.Message})");
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg")
            return Keep(content, NormaliseMethod.ManualReview, null, "the document has no svg root");

        var viewBox = ReadViewBox(root);
        var before = viewBox?.ToAttribute();
        if (viewBox is not { } box)
            return Keep(content, NormaliseMethod.ManualReview, before, "the SVG has no viewBox and no usable width and height");

        if (root.Descendants().Any(e => e.Name.LocalName == "text"))
            return Keep(content, NormaliseMethod.ManualReview, before, "contains text: its bounds depend on the fonts of the machine that draws it");

        // 1. The full-canvas background: the bottom layer, when it paints the whole canvas on its own.
        var candidates = FindTiles(document, box);
        if (candidates.Count > 1)
            return Keep(content, NormaliseMethod.ManualReview, before, $"{candidates.Count} elements cover the whole canvas: several candidate backgrounds");

        var working = document;
        XElement? tile = null;
        string? tileFill = null;
        if (candidates.Count == 1)
        {
            var candidate = candidates[0];
            tileFill = candidate.Fill;
            var without = new XDocument(document);
            var removable = without.Root!.DescendantsAndSelf().First(e => Path(e) == Path(candidate.Element));
            removable.Remove();
            using (var remaining = SvgRaster.Render(without, box, ProbePixels))
            {
                if (remaining is null || remaining.ContentBounds() is null)
                    return Keep(content, NormaliseMethod.Unchanged, before, "the mark is its coloured tile: nothing else is drawn", tileFill);

                var onWhite = remaining.ShareVisibleOn(White, MinimumContrast);
                var onDark = remaining.ShareVisibleOn(Dark, MinimumContrast);
                var reads = onWhite >= ReadsShare && onDark >= ReadsShare;
                var detail = string.Create(CultureInfo.InvariantCulture, $"without the tile the mark reads on white {onWhite:P0} and on dark {onDark:P0}");
                if (!reads)
                {
                    // A white tile that cannot go is a white box on the card: a person decides. A coloured tile is the mark itself.
                    return !candidate.IsFlat || !IsNearWhite(candidate.Color)
                        ? Keep(content, NormaliseMethod.Unchanged, before, $"the coloured tile is the mark: {detail}", tileFill)
                        : Keep(content, NormaliseMethod.ManualReview, before, $"{detail}; the white tile stays", tileFill);
                }

                if (!candidate.IsFlat)
                    return Keep(content, NormaliseMethod.ManualReview, before, $"the full-canvas background is not one flat colour (a gradient or a pattern) and the mark reads without it ({detail}): is the tile the mark?", tileFill);
            }

            working = without;
            tile = candidate.Element;
        }

        // 2. The content bounds, found by drawing.
        using var raster = SvgRaster.Render(working, box, BoundsPixels);
        var bounds = raster?.ContentBounds();
        if (bounds is not { } drawn)
            return Keep(content, NormaliseMethod.ManualReview, before, "nothing is drawn when the SVG is rendered");

        var margin = MarginShare * Math.Max(drawn.Width, drawn.Height);
        var left = Math.Max(box.X, drawn.X - margin);
        var top = Math.Max(box.Y, drawn.Y - margin);
        var right = Math.Min(box.X + box.Width, drawn.X + drawn.Width + margin);
        var bottom = Math.Min(box.Y + box.Height, drawn.Y + drawn.Height + margin);
        var tightened = new ViewBox(left, top, right - left, bottom - top);

        var moved = Math.Abs(tightened.X - box.X) > Tolerance * box.Width
            || Math.Abs(tightened.Y - box.Y) > Tolerance * box.Height
            || Math.Abs(tightened.Width - box.Width) > Tolerance * box.Width
            || Math.Abs(tightened.Height - box.Height) > Tolerance * box.Height;
        if (tile is null && !moved)
            return Keep(content, NormaliseMethod.Unchanged, before, "already fills its canvas");

        // 3. Write the one background removal and the viewBox; everything else stays as the source wrote it.
        var result = working.Root!;
        result.SetAttributeValue("viewBox", tightened.ToAttribute());
        Rescale(result, "width", tightened.Width / box.Width);
        Rescale(result, "height", tightened.Height / box.Height);
        var bytes = Serialise(working, content);

        // 4. The result must draw what the source drew: it is checked, not assumed.
        using (var check = SvgRaster.Render(XDocument.Parse(Encoding.UTF8.GetString(bytes), LoadOptions.PreserveWhitespace), tightened, BoundsPixels))
        {
            var after = check?.ContentBounds();
            if (after is not { } filled || Math.Max(filled.Width / tightened.Width, filled.Height / tightened.Height) < 0.9)
                return Keep(content, NormaliseMethod.ManualReview, before, "the tightened SVG does not draw what the original drew");
        }

        return new Normalisation(
            bytes,
            tile is null ? NormaliseMethod.ViewBoxTightened : NormaliseMethod.BackgroundRemovedAndTightened,
            tile is not null,
            before,
            tightened.ToAttribute(),
            tile is null ? null : "the full-canvas tile was dropped",
            tileFill);
    }

    /// <summary>
    /// How much of a mark is visible on the light and on the dark card, as the shares of its drawn pixels with enough
    /// contrast; null for a mark that cannot be drawn (a PNG, or an SVG the renderer rejects). Information for the report:
    /// the shapes and colours are the institution's, so a mark that reads on one card only is listed, not changed.
    /// </summary>
    public static (double OnLight, double OnDark)? Legibility(byte[] content, string extension)
    {
        if (!string.Equals(extension, "svg", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(content), LoadOptions.PreserveWhitespace);
            if (document.Root is not { } root || ReadViewBox(root) is not { } box)
                return null;

            using var raster = SvgRaster.Render(document, box, ProbePixels);
            return raster is null ? null : (raster.ShareVisibleOn(White, MinimumContrast), raster.ShareVisibleOn(Dark, MinimumContrast));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static Normalisation Keep(byte[] content, string method, string? viewBox, string? note, string? fill = null) =>
        new(content, method, false, viewBox, viewBox, note, fill);

    private static ViewBox? ReadViewBox(XElement root)
    {
        if (ViewBox.Parse(root.Attribute("viewBox")?.Value) is { } declared)
            return declared;

        return Length(root.Attribute("width")?.Value) is { } width && Length(root.Attribute("height")?.Value) is { } height
            ? new ViewBox(0, 0, width, height)
            : null;
    }

    /// <summary>A plain number of user units or pixels ("24", "24px"); null for percentages and other units.</summary>
    private static double? Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            text = text[..^2];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    /// <summary>Scales a "24" or "24px" width or height so the tightened viewBox keeps its aspect ratio; other values are left.</summary>
    private static void Rescale(XElement root, string name, double factor)
    {
        var attribute = root.Attribute(name);
        if (attribute is null || Length(attribute.Value) is not { } current)
            return;

        var unit = attribute.Value.Trim().EndsWith("px", StringComparison.OrdinalIgnoreCase) ? "px" : string.Empty;
        attribute.Value = Math.Round(current * factor, 3).ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }

    private static byte[] Serialise(XDocument document, byte[] original)
    {
        var builder = new StringBuilder();
        if (document.Declaration is { } declaration)
            builder.Append(declaration).Append('\n');
        builder.Append(document.ToString(SaveOptions.DisableFormatting));
        var text = builder.ToString();
        // The source's final newline, if it had one.
        if (original.Length > 0 && original[^1] == (byte)'\n')
            text += "\n";
        return new UTF8Encoding(false).GetBytes(text);
    }

    private sealed record Tile(XElement Element, bool IsFlat, SKColor Color, string Fill);

    /// <summary>
    /// The shapes that paint (nearly) the whole canvas on their own: the bottom layer, drawn with its own transform and
    /// the groups around it (a clip to the canvas is common), and any other shape whose bounds reach across it.
    /// </summary>
    private static List<Tile> FindTiles(XDocument document, ViewBox box)
    {
        var tiles = new List<Tile>();
        var bottom = FirstDrawn(document.Root!);
        foreach (var element in DrawnShapes(document.Root!))
        {
            if (!ReferenceEquals(element, bottom) && !CouldCoverCanvas(element, box))
                continue;

            using var raster = SvgRaster.Render(ShapeAlone(document, element), box, ProbePixels);
            if (raster is null || raster.Coverage(minimumAlpha: 20) < TileCoverage)
                continue;

            var (isFlat, color) = FlatColor(raster);
            tiles.Add(new Tile(element, isFlat, color, color.ToString()));
        }

        return tiles;
    }

    /// <summary>A cheap test before drawing: a rect, or a path or polygon, whose own bounds reach across the canvas.</summary>
    private static bool CouldCoverCanvas(XElement element, ViewBox box)
    {
        if (element.Attribute("transform") is not null)
            return false;

        switch (element.Name.LocalName)
        {
            case "rect":
                var x = Length(element.Attribute("x")?.Value) ?? (element.Attribute("x") is null ? 0 : double.NaN);
                var y = Length(element.Attribute("y")?.Value) ?? (element.Attribute("y") is null ? 0 : double.NaN);
                var width = PercentOrLength(element.Attribute("width")?.Value, box.Width);
                var height = PercentOrLength(element.Attribute("height")?.Value, box.Height);
                return width >= TileExtent * box.Width && height >= TileExtent * box.Height
                    && (double.IsNaN(x) || x <= box.X + ((1 - TileExtent) * box.Width))
                    && (double.IsNaN(y) || y <= box.Y + ((1 - TileExtent) * box.Height));
            case "path":
                if (element.Attribute("d")?.Value is not { } data)
                    return false;
                using (var path = SKPath.ParseSvgPathData(data))
                    return path is not null && Covers(path.TightBounds, box);
            case "polygon":
                return PolygonBounds(element.Attribute("points")?.Value) is { } polygon && Covers(polygon, box);
            default:
                return false;
        }
    }

    private static bool Covers(SKRect bounds, ViewBox box) =>
        bounds.Width >= TileExtent * box.Width
        && bounds.Height >= TileExtent * box.Height
        && bounds.Left <= box.X + ((1 - TileExtent) * box.Width)
        && bounds.Top <= box.Y + ((1 - TileExtent) * box.Height);

    private static double PercentOrLength(string? value, double reference)
    {
        if (value is not null && value.Trim().EndsWith('%')
            && double.TryParse(value.Trim()[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return reference * percent / 100;
        }

        return Length(value) ?? 0;
    }

    private static SKRect? PolygonBounds(string? points)
    {
        if (points is null)
            return null;

        var numbers = points.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN)
            .ToList();
        if (numbers.Count < 6 || numbers.Count % 2 != 0 || numbers.Any(double.IsNaN))
            return null;

        var xs = numbers.Where((_, i) => i % 2 == 0).ToList();
        var ys = numbers.Where((_, i) => i % 2 == 1).ToList();
        return new SKRect((float)xs.Min(), (float)ys.Min(), (float)xs.Max(), (float)ys.Max());
    }

    /// <summary>
    /// The document with only <paramref name="shape"/> drawn: the source's root, its defs and styles, the groups around the
    /// shape (their attributes only, so a clip or transform still applies) and a copy of the shape.
    /// </summary>
    private static XDocument ShapeAlone(XDocument document, XElement shape)
    {
        var root = new XElement(document.Root!.Name, document.Root.Attributes());
        foreach (var definition in document.Root.Elements().Where(e => NotDrawn.Contains(e.Name.LocalName)))
            root.Add(new XElement(definition));

        XElement current = new(shape);
        foreach (var ancestor in shape.Ancestors().Where(a => a != document.Root))
            current = new XElement(ancestor.Name, ancestor.Attributes(), current);
        root.Add(current);
        return new XDocument(root);
    }

    /// <summary>Whether everything drawn is one colour, and which: read from the pixels, so any way of writing a fill works.</summary>
    private static (bool IsFlat, SKColor Color) FlatColor(SvgRaster raster)
    {
        var pixels = raster.Bitmap.GetPixelSpan();
        var stride = raster.Bitmap.RowBytes;
        var centre = ((raster.Bitmap.Height / 2) * stride) + ((raster.Bitmap.Width / 2) * 4);
        var colour = new SKColor(pixels[centre], pixels[centre + 1], pixels[centre + 2], pixels[centre + 3]);
        if (colour.Alpha < 20)
            return (false, colour);

        for (var y = 0; y < raster.Bitmap.Height; y++)
        {
            for (var x = 0; x < raster.Bitmap.Width; x++)
            {
                var offset = (y * stride) + (x * 4);
                // Anti-aliased edges have less alpha; every pixel painted with the tile's own alpha has its colour.
                if (Math.Abs(pixels[offset + 3] - colour.Alpha) > 3)
                    continue;
                if (Math.Abs(pixels[offset] - colour.Red) > 3 || Math.Abs(pixels[offset + 1] - colour.Green) > 3 || Math.Abs(pixels[offset + 2] - colour.Blue) > 3)
                    return (false, colour);
            }
        }

        return (true, colour);
    }

    private static bool IsNearWhite(SKColor color) => color.Red >= 240 && color.Green >= 240 && color.Blue >= 240;

    /// <summary>Every shape the renderer paints, in document order (not what only defines clips, masks, gradients or patterns).</summary>
    private static IEnumerable<XElement> DrawnShapes(XElement root) =>
        root.Descendants().Where(e => Shapes.Contains(e.Name.LocalName) && !e.Ancestors().Any(a => NotDrawn.Contains(a.Name.LocalName)));

    private static XElement? FirstDrawn(XElement root) => DrawnShapes(root).FirstOrDefault();

    /// <summary>A position-based identity of an element: the same in a copy of the document.</summary>
    private static string Path(XElement element) =>
        string.Join('/', element.AncestorsAndSelf().Reverse().Select(e => e.Parent is null ? e.Name.LocalName : $"{e.Name.LocalName}[{e.ElementsBeforeSelf().Count()}]"));
}
