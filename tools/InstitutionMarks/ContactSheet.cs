using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace CrxMoney.Integrations.Tools.InstitutionMarks;

/// <summary>One line of the contact sheet: a mark as downloaded and as shipped.</summary>
internal sealed record SheetEntry(string Compe, string Institution, string Extension, byte[] Before, byte[] After, string Method);

/// <summary>
/// One PNG with every mark before and after normalisation at the size the picker shows a chip (44 px boxes), on a light
/// and a dark card, so the optical size can be judged in one look (#1378).
/// </summary>
internal static class ContactSheet
{
    public const int Cell = 44;
    private const int Pad = 4;
    private const int Slot = Cell + (2 * Pad);
    private const int LabelWidth = 214;
    private const int Rows = 22;
    private const int Gutter = 24;
    private const int Header = 54;

    private static readonly SKColor Light = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor Dark = new(0x14, 0x16, 0x1A);
    private static readonly SKColor Page = new(0xF1, 0xF3, 0xF5);

    public static byte[] Render(IReadOnlyList<SheetEntry> entries)
    {
        var blocks = (int)Math.Ceiling(entries.Count / (double)Rows);
        var blockWidth = LabelWidth + (4 * Slot);
        var width = (blocks * blockWidth) + ((blocks + 1) * Gutter);
        var height = Header + (Rows * Slot) + Gutter;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(Page);
        using var text = new SKPaint { Color = new SKColor(0x20, 0x24, 0x2A), IsAntialias = true };
        using var small = new SKPaint { Color = new SKColor(0x5B, 0x63, 0x6E), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 12);
        using var tiny = new SKFont(SKTypeface.Default, 10);

        canvas.DrawText("Institution marks of the Brazil package, as downloaded and as shipped, in 44 px boxes: light card, then dark card.", Gutter, 22, SKTextAlign.Left, font, text);
        canvas.DrawText("Outlines show the 44 px box. The shapes are the institutions'; only the canvas and the background change.", Gutter, 40, SKTextAlign.Left, tiny, small);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var left = Gutter + ((index / Rows) * (blockWidth + Gutter));
            var top = Header + ((index % Rows) * Slot);
            canvas.DrawText($"{entry.Compe}  {entry.Institution}", left, top + 20, SKTextAlign.Left, font, text);
            canvas.DrawText(entry.Method, left, top + 36, SKTextAlign.Left, tiny, small);

            var slots = new[] { (entry.Before, Light), (entry.After, Light), (entry.Before, Dark), (entry.After, Dark) };
            for (var s = 0; s < slots.Length; s++)
            {
                var box = new SKRect(left + LabelWidth + (s * Slot) + Pad, top + Pad, left + LabelWidth + (s * Slot) + Pad + Cell, top + Pad + Cell);
                using var fill = new SKPaint { Color = slots[s].Item2 };
                canvas.DrawRect(box, fill);
                DrawSvg(canvas, slots[s].Item1, entry.Extension, box);
                using var outline = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x9A, 0xA3, 0xAF, 0x90), StrokeWidth = 1 };
                canvas.DrawRect(box, outline);
            }
        }

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>Draws a mark inside <paramref name="box"/>, its viewBox fitted and centred (what <c>object-fit: contain</c> does).</summary>
    private static void DrawSvg(SKCanvas canvas, byte[] content, string extension, SKRect box)
    {
        if (string.Equals(extension, "png", StringComparison.OrdinalIgnoreCase))
        {
            using var bitmap = SKBitmap.Decode(content);
            if (bitmap is null)
                return;
            var scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
            var target = SKRect.Create(box.MidX - (bitmap.Width * scale / 2), box.MidY - (bitmap.Height * scale / 2), bitmap.Width * scale, bitmap.Height * scale);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return;
        }

        var document = XDocument.Parse(Encoding.UTF8.GetString(content), LoadOptions.PreserveWhitespace);
        var root = document.Root!;
        var viewBox = ViewBox.Parse(root.Attribute("viewBox")?.Value)
            ?? new ViewBox(0, 0, ParseLength(root.Attribute("width")?.Value) ?? 100, ParseLength(root.Attribute("height")?.Value) ?? 100);
        root.SetAttributeValue("viewBox", viewBox.ToAttribute());
        root.SetAttributeValue("width", viewBox.Width.ToString("0.###", CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", viewBox.Height.ToString("0.###", CultureInfo.InvariantCulture));
        root.SetAttributeValue("preserveAspectRatio", "none");

        using var svg = new SKSvg();
        if (svg.FromSvg(document.ToString(SaveOptions.DisableFormatting)) is not { } picture)
            return;

        var fit = Math.Min(box.Width / viewBox.Width, box.Height / viewBox.Height);
        canvas.Save();
        canvas.ClipRect(box);
        canvas.Translate((float)(box.MidX - (viewBox.Width * fit / 2)), (float)(box.MidY - (viewBox.Height * fit / 2)));
        canvas.Scale((float)(fit * viewBox.Width / picture.CullRect.Width), (float)(fit * viewBox.Height / picture.CullRect.Height));
        canvas.DrawPicture(picture);
        canvas.Restore();
    }

    private static double? ParseLength(string? value) =>
        double.TryParse(value?.Trim().Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
}
