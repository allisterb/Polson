namespace Polson.Tests.Drawing;

using System.Linq;
using Polson.Drawing.Skia;
using Polson.Drawing.Svg;
using Xunit;

/// <summary>
/// Type on type, type off the page, and type straddling a shape — on a paper and on a canvas.
/// </summary>
/// <remarks>
/// Every case here is one a real mood board did (nemologo2–5, geminilogo1–2): a model looked at the render and called it
/// clean. The positions are generous so the result does not depend on whether this machine has Arial or DejaVu Sans.
/// </remarks>
public class LayoutAuditTests : TestsRuntime
{
    #region Fields
    private const string Face = "font-family=\"Arial, DejaVu Sans, sans-serif\"";
    #endregion

    #region Methods
    private static string[] Paper(string body) => [.. LayoutAudit.Check(
        $"""<svg xmlns="http://www.w3.org/2000/svg" width="900" height="600" viewBox="0 0 900 600">{body}</svg>""")];

    private static string Text(float x, float y, string words, int size = 24, string extra = "") =>
        $"""<text x="{x}" y="{y}" {Face} font-size="{size}" {extra}>{words}</text>""";

    [Fact]
    public void TestALabelSetOnASpecimenIsReported()
    {
        // nemologo2: a small "Verdana" label printed across the "Harbour Light" specimen.
        var warning = Assert.Single(Paper(Text(30, 170, "Harbour Light", 40) + Text(160, 160, "Verdana", 12)));
        Assert.Contains("\"Harbour Light\" and \"Verdana\" are set on top of each other", warning);
    }

    [Fact]
    public void TestCaptionsRunningIntoEachOtherOnOneRowAreReported()
    {
        // geminilogo2: two columns' captions touching end to end — a small share of either line, still a collision.
        var left = Text(20, 60, "Slate granite, steel maritime blue, mist grey, signal amber", 13);
        var right = Text(330, 60, "Midnight sea, arctic ice white", 13);
        Assert.Contains(Paper(left + right), w => w.Contains("set on top of each other"));
    }

    [Fact]
    public void TestLinesAtOrdinaryLeadingAreNotACollision()
    {
        Assert.Empty(Paper(Text(40, 100, "First line of a block") + Text(40, 128, "Second line of the block")
                           + Text(40, 156, "Third line of the block")));
    }

    [Fact]
    public void TestTypeRunningOffThePageIsReported()
    {
        // nemologo3: a hex code under the last swatch, below the bottom of the board.
        var warning = Assert.Single(Paper(Text(40, 620, "#e9c46a")));
        Assert.Contains("runs off the bottom edge of the page", warning);
    }

    [Fact]
    public void TestTypeHalfAcrossACardIsReportedAndTypeInsideItIsNot()
    {
        // nemologo3: the wordmark running out of the type card into the next one.
        const string Card = """<rect x="290" y="100" width="220" height="340" fill="#ffffff" stroke="#cccccc"/>""";
        var across = Assert.Single(Paper(Card + Text(306, 200, "HARBOUR LIGHT WORDMARK", 34)));
        Assert.Contains("runs across the edge of a rectangle", across);

        Assert.Empty(Paper(Card + Text(306, 200, "Inside", 24)));
    }

    /// <summary>
    /// A label a little too long for its pill: almost all of it on the shape, so never a straddle, and it went unreported.
    /// A label that fits, and one with room to spare, are not.
    /// </summary>
    [Fact]
    public void TestALabelRunningPastTheSideOfItsBoxIsReported()
    {
        // The box ends at x = 400; the line, set to end at 412, overruns it by 12 units and is otherwise wholly on it.
        const string Pill = """<rect x="40" y="100" width="360" height="44" fill="#1b2530"/>""";
        var over = Assert.Single(Paper(Pill + Text(412, 130, "AI and compute, 40 episodes", extra: "text-anchor=\"end\"")));
        Assert.Contains("side of the rectangle it sits on", over);
        Assert.Contains("the box is too small for it", over);

        Assert.Empty(Paper(Pill + Text(390, 130, "AI and compute, 40 episodes", extra: "text-anchor=\"end\"")));
    }

    /// <summary>
    /// all_in_2025: the title ended a few letters inside the audience panel beside it — barely on the panel, so never a
    /// straddle. A title that stops short, and a caption above a bar whose box dips into it, are not reported.
    /// </summary>
    [Fact]
    public void TestALineRunningIntoTheShapeBesideItIsReported()
    {
        const string Panel = """<rect x="600" y="40" width="260" height="80" fill="#ffffff" stroke="#e2e2e2"/>""";
        var into = Assert.Single(Paper(Panel + Text(612, 100, "WHAT ALL-IN TALKED ABOUT", 40, "text-anchor=\"end\"")));
        Assert.Contains("runs into the rectangle beside it", into);

        Assert.Empty(Paper(Panel + Text(590, 100, "WHAT ALL-IN TALKED ABOUT", 40, "text-anchor=\"end\"")));

        const string Bar = """<rect x="40" y="200" width="400" height="24" fill="#d86a2b"/>""";
        Assert.Empty(Paper(Bar + Text(40, 196, "AI & Compute", 20)));
    }

    [Fact]
    public void TestADividerThroughALineIsReportedAndAnUnderlineIsNot()
    {
        // nemologo4: column rules running through the palette labels.
        const string Rule = """<line x1="300" y1="0" x2="300" y2="600" stroke="#999999" stroke-width="1"/>""";
        var crossed = Assert.Single(Paper(Rule + Text(170, 140, "#163240 depth swatch")));
        Assert.Contains("vertical line running through it. Move the line of type or the rule", crossed);
        Assert.DoesNotContain("wholly on the shape", crossed);   // nemologo7 got the straddle remedy for a rule

        // A rule in the descent under its own line is an underline.
        const string Underline = """<line x1="40" y1="104" x2="260" y2="104" stroke="#000000" stroke-width="1"/>""";
        Assert.Empty(Paper(Underline + Text(40, 100, "Underlined heading")));
    }

    [Fact]
    public void TestALineMarkedExemptIsLeftOut()
    {
        Assert.Empty(Paper(Text(40, 620, "Bleeds off on purpose", extra: "data-print-exempt=\"layout\"")));
    }

    /// <summary>poster5's mirror ball, its top edge under the tagline.</summary>
    private const string Ball = """<circle cx="450" cy="400" r="138" fill="#B8381F"/>""";

    /// <summary>poster5's title band: opaque, painted over the ball's top, down past the tagline.</summary>
    private const string Band = """<rect x="-10" y="-10" width="920" height="310" fill="#F2E3C4"/>""";

    private static string Tagline() => Text(90, 286, "Nothing personal. It's the music business.", 34);

    [Fact]
    public void TestTypeAcrossAShapeIsReportedWhenNothingCoversIt()
    {
        Assert.Contains(Paper(Ball + Tagline()), w => w.Contains("runs across the edge of a circle"));
    }

    /// <summary>
    /// poster5: the tagline was reported across the mirror ball, which sat behind an opaque band painted after it. The
    /// reader never sees that edge; the band covers the ball wherever the line meets it.
    /// </summary>
    [Fact]
    public void TestAShapePaintedOverByAnOpaqueBandBeforeTheTypeIsNotReported()
    {
        Assert.Empty(Paper(Ball + Band + Tagline()));
        Assert.Empty(Paper(Ball + $"""<g>{Band}</g>""" + $"<g>{Tagline()}</g>"));                // groups do not change the order
        Assert.Empty(Paper(Ball + """<ellipse cx="450" cy="150" rx="700" ry="200" fill="#F2E3C4"/>""" + Tagline()));
    }

    [Theory]
    [InlineData("band under the ball", Band + Ball, "")]                                               // the ball is on top
    [InlineData("band over the type", Ball, Band)]                                                      // hides the line too: not this audit's call
    [InlineData("translucent band", Ball + """<rect x="-10" y="-10" width="920" height="310" fill="#F2E3C4" fill-opacity="0.9"/>""", "")]
    [InlineData("band in a faded group", Ball + """<g opacity="0.95"><rect x="-10" y="-10" width="920" height="310" fill="#F2E3C4"/></g>""", "")]
    [InlineData("gradient band", Ball + """<defs><linearGradient id="g"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#000"/></linearGradient></defs><rect x="-10" y="-10" width="920" height="310" fill="url(#g)"/>""", "")]
    [InlineData("rotated band", Ball + """<rect x="-10" y="-10" width="920" height="310" fill="#F2E3C4" transform="rotate(1 450 150)"/>""", "")]
    [InlineData("rounded band", Ball + """<rect x="-10" y="-10" width="920" height="310" rx="40" fill="#F2E3C4"/>""", "")]
    [InlineData("band too short", Ball + """<rect x="-10" y="-10" width="920" height="280" fill="#F2E3C4"/>""", "")]
    [InlineData("band too narrow", Ball + """<rect x="-10" y="-10" width="400" height="310" fill="#F2E3C4"/>""", "")]
    public void TestACrossingIsStillReportedUnlessAnOpaqueShapeCertainlyCoversIt(string why, string before, string after)
    {
        Assert.True(Paper(before + Tagline() + after).Any(w => w.Contains("runs across the edge of a circle")), why);
    }

    [Fact]
    public void TestACanvasBandFilledOverAShapeBeforeTheTypeHidesTheCrossing()
    {
        string[] Draw(string band)
        {
            using var canvas = new SkiaCanvas(900, 600);
            var ctx = canvas.GetContext("2d");
            ctx.FillStyle = "#B8381F";
            ctx.BeginPath();
            ctx.Arc(450, 400, 138, 0, (float)(2 * System.Math.PI));
            ctx.Fill();
            ctx.FillStyle = band;
            ctx.FillRect(-10, -10, 920, 310);
            ctx.FillStyle = "#333333";
            ctx.Font = "34px Arial";
            ctx.FillText("Nothing personal. It's the music business.", 90, 286);
            return [.. canvas.AuditLayout()];
        }

        Assert.DoesNotContain(Draw("#F2E3C4"), w => w.Contains("runs across the edge"));
        Assert.Contains(Draw("rgba(242, 227, 196, 0.8)"), w => w.Contains("runs across the edge"));     // translucent: the ball shows
    }

    [Fact]
    public void TestACanvasRecordsWhatItDrewAndIsAuditedTheSameWay()
    {
        // nemologo2 drew its board with ctx.fillText: "Aa" straddling a swatch.
        using var canvas = new SkiaCanvas(900, 600);
        var ctx = canvas.GetContext("2d");
        ctx.FillStyle = "#0f3d5e";
        ctx.FillRect(30, 220, 44, 44);
        ctx.FillStyle = "#333333";
        ctx.Font = "bold 36px Arial";
        ctx.FillText("Aa", 30, 290);

        Assert.Contains(canvas.AuditLayout(), w => w.Contains("\"Aa\" runs across the edge of a rectangle"));
    }

    [Fact]
    public void TestACanvasDividerDrawnAsOnePathOfSeveralSegmentsIsFound()
    {
        // nemologo4: both column rules in one path, one stroke().
        using var canvas = new SkiaCanvas(900, 600);
        var ctx = canvas.GetContext("2d");
        ctx.BeginPath();
        ctx.MoveTo(300, 0); ctx.LineTo(300, 600);
        ctx.MoveTo(600, 0); ctx.LineTo(600, 600);
        ctx.Stroke();
        ctx.Font = "24px Arial";
        ctx.FillText("A palette label that runs on", 170, 140);

        Assert.Contains(canvas.AuditLayout(), w => w.Contains("vertical line running through it"));
    }

    /// <summary>
    /// poster6's kit sheet (2026-10-25): each reconfiguration drawn in a clipped tile, a disc magnified past its tile, and
    /// 26 collisions reported with captions outside the tiles, where nothing was drawn. Only what lands in the clip counts —
    /// and a caption inside the tile still collides.
    /// </summary>
    [Fact]
    public void TestOnlyWhatLandsInsideAClipIsAudited()
    {
        using var canvas = new SkiaCanvas(900, 600);
        var ctx = canvas.GetContext("2d");
        ctx.Save();
        ctx.BeginPath();
        ctx.Rect(30, 30, 400, 170);
        ctx.Clip();
        ctx.FillStyle = "#111111";
        ctx.BeginPath();
        ctx.Arc(230, 200, 260, 0, MathF.PI * 2);
        ctx.Fill();
        ctx.FillRect(-100, 150, 800, 200);                // a band, cut to the tile
        ctx.Restore();
        ctx.FillStyle = "#333333";
        ctx.Font = "16px Arial";
        ctx.FillText("scale — the part magnified past the frame", 30, 222);

        Assert.DoesNotContain(canvas.AuditLayout(), w => w.Contains("scale — the part"));

        // Inside a clip, a collision is still one: "Aa" straddling a swatch, both within the tile.
        ctx.Save();
        ctx.BeginPath();
        ctx.Rect(450, 30, 400, 170);
        ctx.Clip();
        ctx.FillStyle = "#0f3d5e";
        ctx.FillRect(480, 80, 44, 44);
        ctx.FillStyle = "#333333";
        ctx.Font = "bold 36px Arial";
        ctx.FillText("Aa", 480, 150);
        ctx.Restore();
        Assert.Contains(canvas.AuditLayout(), w => w.Contains("\"Aa\" runs across the edge of a rectangle"));
    }

    [Fact]
    public void TestACanvasDrawnACellAtATimeIsNotAudited()
    {
        // Noise or a pixel sheet has no layout; past the cap the record stops rather than report thousands of nothing.
        using var canvas = new SkiaCanvas(200, 200);
        var ctx = canvas.GetContext("2d");
        for (var x = 0; x < 100; x++)
            for (var y = 0; y < 50; y++)
                ctx.FillRect(x * 2, y * 4, 2, 4);
        ctx.FillText("over the noise", 0, 300);

        Assert.Empty(canvas.AuditLayout());
    }
    #endregion
}
