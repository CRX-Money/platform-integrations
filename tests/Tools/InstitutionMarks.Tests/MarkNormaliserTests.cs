using System.Text;
using System.Xml.Linq;

namespace CrxMoney.Integrations.Tools.InstitutionMarks.Tests;

/// <summary>
/// The marks are the institutions' trademarks: the normaliser may drop a full-canvas background and move the viewBox,
/// nothing else. Every case is a synthetic SVG; no real mark is in these tests.
/// </summary>
public sealed class MarkNormaliserTests
{
    private const string Orange = "#EA7100";

    private static byte[] Svg(string body, string attributes = "viewBox=\"0 0 100 100\" width=\"100\" height=\"100\"") =>
        Encoding.UTF8.GetBytes($"""<svg xmlns="http://www.w3.org/2000/svg" {attributes}>{body}</svg>""");

    private static XElement Root(byte[] svg) => XDocument.Parse(Encoding.UTF8.GetString(svg)).Root!;

    private static ViewBox ViewBoxOf(byte[] svg) => ViewBox.Parse(Root(svg).Attribute("viewBox")!.Value)!.Value;

    // A blue mark in the middle of a 100 x 100 canvas: 40 x 20, at (30, 40).
    private const string BlueMark = """<path d="M30 40H70V60H30Z" fill="#0A3C9C"/>""";

    [Fact]
    public void A_white_tile_is_dropped_and_the_viewBox_tightens_to_the_mark_plus_a_two_percent_margin()
    {
        var source = Svg($"""<rect width="100" height="100" fill="#FFFFFF"/>{BlueMark}""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.Equal(NormaliseMethod.BackgroundRemovedAndTightened, result.Method);
        Assert.True(result.BackgroundRemoved);
        Assert.DoesNotContain("<rect", Encoding.UTF8.GetString(result.Content));
        var box = ViewBoxOf(result.Content);
        // 40 x 20 plus 2% of the longer side (0.8) on each side, to the pixel the bounds are found at.
        Assert.Equal(29.2, box.X, 0.15);
        Assert.Equal(39.2, box.Y, 0.15);
        Assert.Equal(41.6, box.Width, 0.15);
        Assert.Equal(21.6, box.Height, 0.15);
        Assert.Equal("0 0 100 100", result.ViewBoxBefore);
        Assert.Equal(box.ToAttribute(), result.ViewBoxAfter);
    }

    [Fact]
    public void Paths_and_fills_are_never_touched_and_the_size_follows_the_new_viewBox()
    {
        var source = Svg($"""<rect width="100" height="100" fill="#FFFFFF"/>{BlueMark}""");

        var result = MarkNormaliser.Normalise(source, "svg");

        var path = Root(result.Content).Elements().Single();
        Assert.Equal("path", path.Name.LocalName);
        Assert.Equal("M30 40H70V60H30Z", path.Attribute("d")!.Value);
        Assert.Equal("#0A3C9C", path.Attribute("fill")!.Value);
        // 100 x 100 shrinks by the factors the viewBox shrank by, so the aspect ratio stays the mark's.
        var root = Root(result.Content);
        var box = ViewBoxOf(result.Content);
        Assert.Equal(box.Aspect, double.Parse(root.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture) / double.Parse(root.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture), 3);
    }

    [Fact]
    public void A_coloured_tile_that_is_the_mark_is_kept_byte_for_byte()
    {
        // An orange square with a white glyph: without the tile the glyph reads on neither white nor dark.
        var source = Svg($"""<rect width="100" height="100" fill="{Orange}"/><path d="M30 40H70V60H30Z" fill="#FFFFFF"/>""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.Equal(NormaliseMethod.Unchanged, result.Method);
        Assert.False(result.BackgroundRemoved);
        Assert.Equal(source, result.Content);
        Assert.Contains("coloured tile is the mark", result.Note);
    }

    [Fact]
    public void A_tile_inside_a_group_with_a_clip_and_an_identity_transform_is_still_found()
    {
        var source = Svg($"""<defs><clipPath id="c"><rect width="100" height="100"/></clipPath></defs><g clip-path="url(#c)"><path d="M0 0H100V100H0Z" fill="white" transform="translate(0,0)"/>{BlueMark}</g>""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.Equal(NormaliseMethod.BackgroundRemovedAndTightened, result.Method);
        Assert.Equal(1, Root(result.Content).Descendants().Count(e => e.Name.LocalName == "path"));
    }

    [Fact]
    public void A_semi_transparent_white_tile_counts_as_a_white_tile()
    {
        var source = Svg($"""<rect width="100" height="100" fill="white" fill-opacity="0.58"/>{BlueMark}""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.True(result.BackgroundRemoved);
    }

    [Fact]
    public void A_mark_without_a_tile_only_gets_its_viewBox_tightened()
    {
        var source = Svg(BlueMark);

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.Equal(NormaliseMethod.ViewBoxTightened, result.Method);
        Assert.False(result.BackgroundRemoved);
        Assert.Equal(Root(source).Elements().Single().ToString(), Root(result.Content).Elements().Single().ToString());
    }

    [Fact]
    public void A_mark_that_already_fills_its_canvas_is_left_alone()
    {
        var source = Svg("""<path d="M1 1H99V99H1Z" fill="#0A3C9C"/>""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.Equal(NormaliseMethod.Unchanged, result.Method);
        Assert.Equal(source, result.Content);
    }

    [Fact]
    public void The_new_viewBox_never_reaches_outside_the_old_one()
    {
        // The mark touches the left edge: no margin is added there, so nothing that was clipped becomes visible.
        var source = Svg("""<path d="M0 40H40V60H0Z" fill="#0A3C9C"/>""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.Equal(0, ViewBoxOf(result.Content).X, 3);
    }

    [Fact]
    public void A_viewBox_is_taken_from_width_and_height_when_the_file_has_none()
    {
        var source = Svg($"""<rect width="100" height="100" fill="#FFFFFF"/>{BlueMark}""", "width=\"100\" height=\"100\"");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.True(result.BackgroundRemoved);
        Assert.Equal("0 0 100 100", result.ViewBoxBefore);
    }

    [Fact]
    public void A_white_tile_that_the_mark_cannot_do_without_is_left_for_review()
    {
        // A near-black glyph on white reads on white but not on the dark card.
        var source = Svg("""<rect width="100" height="100" fill="#FFFFFF"/><path d="M30 40H70V60H30Z" fill="#101216"/>""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.True(result.NeedsReview);
        Assert.Equal(source, result.Content);
        Assert.Contains("the white tile stays", result.Note);
    }

    [Fact]
    public void Several_candidate_backgrounds_are_left_for_review()
    {
        var source = Svg($"""<rect width="100" height="100" fill="#FFFFFF"/><rect width="100" height="100" fill="#EEEEEE"/>{BlueMark}""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.True(result.NeedsReview);
        Assert.Equal(source, result.Content);
        Assert.Contains("several candidate backgrounds", result.Note);
    }

    [Fact]
    public void Text_is_left_for_review_because_its_bounds_depend_on_the_fonts()
    {
        var source = Svg("""<text x="10" y="50" font-size="20">Bank</text>""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.True(result.NeedsReview);
        Assert.Equal(source, result.Content);
    }

    [Fact]
    public void A_gradient_tile_is_never_dropped_silently()
    {
        var source = Svg($"""<defs><linearGradient id="g"><stop offset="0" stop-color="#C00"/><stop offset="1" stop-color="#600"/></linearGradient></defs><rect width="100" height="100" fill="url(#g)"/>{BlueMark}""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.False(result.BackgroundRemoved);
        Assert.Equal(source, result.Content);
    }

    [Fact]
    public void Broken_xml_and_pngs_are_kept_as_they_are()
    {
        var broken = Encoding.UTF8.GetBytes("<svg><path></svg>");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };

        Assert.True(MarkNormaliser.Normalise(broken, "svg").NeedsReview);
        var kept = MarkNormaliser.Normalise(png, "png");
        Assert.Equal(NormaliseMethod.NotApplicable, kept.Method);
        Assert.Equal(png, kept.Content);
    }

    [Fact]
    public void The_result_is_clean_svg_the_package_would_accept()
    {
        var source = Svg($"""<rect width="100" height="100" fill="#FFFFFF"/>{BlueMark}""");

        var result = MarkNormaliser.Normalise(source, "svg");

        Assert.Null(MarkImages.Reject(result.Content));
    }
}
