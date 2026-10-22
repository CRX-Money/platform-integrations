using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>A rectangle in the user units of an SVG's viewBox.</summary>
internal readonly record struct ViewBox(double X, double Y, double Width, double Height)
{
    public double Aspect => Width / Height;

    /// <summary>"x y width height" the way a viewBox attribute is written: shortest round-trip form, invariant culture.</summary>
    public string ToAttribute() => string.Join(' ', Format(X), Format(Y), Format(Width), Format(Height));

    public static ViewBox? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            return null;
        }

        return new ViewBox(x, y, width, height);
    }

    private static string Format(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>An SVG drawn to pixels: the bitmap, and the mapping from its pixels back to the viewBox units.</summary>
internal sealed class SvgRaster(SKBitmap bitmap, ViewBox viewBox) : IDisposable
{
    public SKBitmap Bitmap { get; } = bitmap;

    public ViewBox ViewBox { get; } = viewBox;

    public double PixelsPerUnit => Bitmap.Width / ViewBox.Width;

    public void Dispose() => Bitmap.Dispose();

    /// <summary>
    /// Draws <paramref name="svg"/> with its viewBox filling a bitmap whose longer side is <paramref name="longSide"/> pixels,
    /// on a transparent canvas. The width and height attributes of the copy that is drawn are replaced, so the viewBox
    /// maps onto the bitmap whatever the file declares; the file itself is not touched. Null when it cannot be drawn.
    /// </summary>
    public static SvgRaster? Render(XDocument svg, ViewBox viewBox, int longSide)
    {
        var width = viewBox.Width >= viewBox.Height ? longSide : Math.Max(1, (int)Math.Round(longSide * viewBox.Aspect));
        var height = viewBox.Width >= viewBox.Height ? Math.Max(1, (int)Math.Round(longSide / viewBox.Aspect)) : longSide;

        var copy = new XDocument(svg);
        var root = copy.Root!;
        root.SetAttributeValue("viewBox", viewBox.ToAttribute());
        root.SetAttributeValue("width", width.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", height.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("preserveAspectRatio", "none");

        using var skSvg = new SKSvg();
        if (skSvg.FromSvg(copy.ToString(SaveOptions.DisableFormatting)) is not { } picture)
            return null;

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var cull = picture.CullRect;
            if (cull.Width > 0 && cull.Height > 0)
                canvas.Scale(width / cull.Width, height / cull.Height);
            canvas.DrawPicture(picture);
        }

        return new SvgRaster(bitmap, viewBox);
    }

    /// <summary>
    /// The bounds of what is drawn (pixels at least <paramref name="minimumAlpha"/> opaque), in viewBox units; null when
    /// nothing is drawn.
    /// </summary>
    public ViewBox? ContentBounds(byte minimumAlpha = 12)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var pixels = Bitmap.GetPixelSpan();
        var stride = Bitmap.RowBytes;
        for (var y = 0; y < Bitmap.Height; y++)
        {
            for (var x = 0; x < Bitmap.Width; x++)
            {
                if (pixels[(y * stride) + (x * 4) + 3] < minimumAlpha)
                    continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < 0)
            return null;

        var scale = PixelsPerUnit;
        return new ViewBox(
            ViewBox.X + (minX / scale),
            ViewBox.Y + (minY / scale),
            (maxX + 1 - minX) / scale,
            (maxY + 1 - minY) / scale);
    }

    /// <summary>The share of the canvas that is drawn (alpha at least <paramref name="minimumAlpha"/>), 0 to 1.</summary>
    public double Coverage(byte minimumAlpha = 200)
    {
        var pixels = Bitmap.GetPixelSpan();
        var stride = Bitmap.RowBytes;
        long covered = 0;
        for (var y = 0; y < Bitmap.Height; y++)
        {
            for (var x = 0; x < Bitmap.Width; x++)
            {
                if (pixels[(y * stride) + (x * 4) + 3] >= minimumAlpha)
                    covered++;
            }
        }

        return covered / (double)(Bitmap.Width * Bitmap.Height);
    }

    /// <summary>
    /// How much of the drawn content is visible on a flat background: the share of its (mostly opaque) pixels whose
    /// WCAG contrast against <paramref name="background"/> reaches <paramref name="minimumContrast"/>. 1 when nothing is drawn.
    /// </summary>
    public double ShareVisibleOn(SKColor background, double minimumContrast)
    {
        var pixels = Bitmap.GetPixelSpan();
        var stride = Bitmap.RowBytes;
        var backgroundLuminance = Luminance(background.Red, background.Green, background.Blue);
        long drawn = 0, visible = 0;
        for (var y = 0; y < Bitmap.Height; y++)
        {
            for (var x = 0; x < Bitmap.Width; x++)
            {
                var offset = (y * stride) + (x * 4);
                if (pixels[offset + 3] < 128)
                    continue;

                drawn++;
                var luminance = Luminance(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
                var lighter = Math.Max(luminance, backgroundLuminance);
                var darker = Math.Min(luminance, backgroundLuminance);
                if ((lighter + 0.05) / (darker + 0.05) >= minimumContrast)
                    visible++;
            }
        }

        return drawn == 0 ? 1 : visible / (double)drawn;
    }

    private static double Luminance(byte red, byte green, byte blue)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(red)) + (0.7152 * Channel(green)) + (0.0722 * Channel(blue));
    }
}
