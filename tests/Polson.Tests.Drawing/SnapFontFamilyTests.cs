namespace Polson.Tests.Drawing;

using System;
using System.Linq;
using Polson.Drawing.Svg;
using SkiaSharp;
using Xunit;

/// <summary>
/// Double-quoted names in a <c>font-family</c> list, which made centred text land off centre.
/// </summary>
/// <remarks>
/// Found by the poster1 run: <c>"Agency FB", Bahnschrift, sans-serif</c> at <c>text-anchor: middle</c> drew about 85 of
/// 1000 units left of centre while <c>getBBox()</c> reported it centred. Svg.Skia measured the anchor offset with a
/// different face than it drew. A single-quoted list does not, so the setter rewrites one into the other.
/// </remarks>
public class SnapFontFamilyTests : TestsRuntime
{
    #region Methods
    [Theory]
    [InlineData("\"Agency FB\", Bahnschrift, sans-serif", "'Agency FB', Bahnschrift, sans-serif")]
    [InlineData("\"Bodoni MT\", \"Georgia\", serif", "'Bodoni MT', 'Georgia', serif")]
    [InlineData("'Agency FB', sans-serif", "'Agency FB', sans-serif")]
    [InlineData("Arial", "Arial")]
    [InlineData("\"Ma'ruf Sans\", sans-serif", "\"Ma'ruf Sans\", sans-serif")]   // an apostrophe keeps its double quotes
    public void TestDoubleQuotedNamesAreSingleQuoted(string written, string stored)
    {
        var paper = Snap.Create(200, 100);
        var text = paper.Text(10, 50, "x");
        text.Attr("font-family", written);

        Assert.Equal(stored, text.Node.FontFamily);
    }

    /// <summary>The ink of a centred line sits on its anchor however the family list was quoted, by attribute or by stylesheet.</summary>
    [Fact]
    public void TestACentredLineWithAQuotedListIsDrawnCentred()
    {
        // The defect shows only when the named face exists and is narrower than the fallback the anchor was measured
        // with. Without a condensed face installed there is nothing to compare; the rewrite above is still tested.
        var face = new[] { "Agency FB", "DejaVu Sans Condensed", "Liberation Sans Narrow", "Arial Narrow" }
            .FirstOrDefault(f => SKFontManager.Default.MatchFamily(f) is { } t && t.FamilyName == f);
        if (face is null) return;

        float InkCentre(Action<SnapPaper> build)
        {
            var paper = Snap.Create(1000, 120);
            paper.Rect(0, 0, 1000, 120).Attr("fill", "#fff");
            build(paper);
            using var bitmap = SKBitmap.Decode(paper.ToImageBytes(1000, 120, "png"));
            var columns = Enumerable.Range(0, bitmap.Width)
                .Where(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).Red < 128))
                .ToList();
            return (columns.First() + columns.Last()) / 2f;
        }

        var quoted = $"\"{face}\", Bahnschrift, sans-serif";
        var byAttribute = InkCentre(p => p.Text(500, 80, "THE LONG LISTEN")
            .Attr(new System.Collections.Generic.Dictionary<string, object?> { ["font-family"] = quoted, ["font-size"] = 60, ["text-anchor"] = "middle", ["fill"] = "#111" }));
        var byStylesheet = InkCentre(p =>
        {
            p.Text(500, 80, "THE LONG LISTEN").Attr(new System.Collections.Generic.Dictionary<string, object?> { ["class"] = "t", ["font-size"] = 60, ["text-anchor"] = "middle", ["fill"] = "#111" });
            p.Style($".t {{ font-family: {quoted}; }}");
        });

        Assert.InRange(byAttribute, 495f, 505f);
        Assert.InRange(byStylesheet, 495f, 505f);
    }
    #endregion
}
