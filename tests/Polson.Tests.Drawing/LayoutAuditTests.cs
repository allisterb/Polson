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
