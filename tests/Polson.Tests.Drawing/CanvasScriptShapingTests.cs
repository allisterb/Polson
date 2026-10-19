namespace Polson.Tests.Drawing;

using System;
using System.Linq;
using Polson.Drawing.Skia;
using Polson.MCPServer;
using SkiaSharp;
using Xunit;

/// <summary>
/// Canvas text in scripts that need shaping: joining, right to left, and a face for what the chosen one lacks.
/// </summary>
/// <remarks>
/// Found on the cloud image (2026-10-19): <c>ctx.fillText</c> set Arabic and Urdu unjoined and backwards, Hebrew
/// backwards, and a missing character as a box, while Snap and Motion text set the same strings correctly. Faces are
/// found through Skia's own fallback for each script, so these hold on any machine with a face for it (Arial or Segoe
/// on Windows, DejaVu or Noto on Linux).
/// </remarks>
public class CanvasScriptShapingTests : TestsRuntime
{
    #region Methods
    private static SKFont FontFor(string sample, float size = 48f)
    {
        var typeface = SKFontManager.Default.MatchCharacter(char.ConvertToUtf32(sample, 0));
        Assert.True(typeface is not null, $"no face on this machine has '{sample}'");
        return new SKFont(typeface, size);
    }

    [Fact]
    public void TestHebrewIsLaidOutRightToLeft()
    {
        using var font = FontFor("ש");
        var line = TextShaper.Shape("שלום", font, 0f);

        var run = Assert.Single(line.Runs);
        Assert.True(run.RightToLeft);
        // Glyphs come in visual order, left to right, so the leftmost glyph is the word's last letter.
        Assert.True(run.Clusters[0] > run.Clusters[^1], string.Join(",", run.Clusters));
    }

    [Fact]
    public void TestArabicLettersJoin()
    {
        // Three of one letter: unshaped, three identical glyphs (the isolated form); shaped, the initial, medial and
        // final forms, which are different glyphs.
        using var font = FontFor("س");
        var shaped = TextShaper.Shape("سسس", font, 0f).Runs.SelectMany(r => r.Glyphs).ToArray();
        var unshaped = font.GetGlyphs("سسس");

        Assert.Single(unshaped.Distinct());
        Assert.True(shaped.Distinct().Count() > 1, $"shaped glyphs {string.Join(",", shaped)} did not join");
    }

    [Fact]
    public void TestAYearInsideArabicStillReadsLeftToRight()
    {
        using var font = FontFor("س");
        var line = TextShaper.Shape("سنة 2026", font, 0f);

        // The line is right to left, so the number, which comes last, is seen first (leftmost), and in its own order.
        var number = line.Runs[0];
        Assert.False(number.RightToLeft);
        Assert.True(number.Clusters.SequenceEqual(number.Clusters.Order()), string.Join(",", number.Clusters));
        Assert.True(line.Runs[^1].RightToLeft);
    }

    [Fact]
    public void TestACharacterTheFaceLacksIsDrawnFromOneThatHasIt()
    {
        using var latin = FontFor("A");
        Assert.False(latin.ContainsGlyph(char.ConvertToUtf32("등", 0)), "the Latin face unexpectedly has Hangul");

        var line = TextShaper.Shape("A 등대", latin, 0f);

        var hangul = line.Runs.Single(r => !ReferenceEquals(r.Typeface, latin.Typeface));
        using var probe = new SKFont(hangul.Typeface);
        Assert.True(probe.ContainsGlyph(char.ConvertToUtf32("등", 0)));
        Assert.DoesNotContain((ushort)0, hangul.Glyphs);
    }

    [Fact]
    public void TestTrackingFallsBetweenClustersNotInsideThem()
    {
        // A base letter with a combining mark is one cluster: tracking must not prise the mark off its letter.
        using var font = FontFor("e");
        var plain = TextShaper.Shape("éa", font, 0f);
        var tracked = TextShaper.Shape("éa", font, 10f);
        var clusters = tracked.Runs.SelectMany(r => r.Clusters).Distinct().Count();

        Assert.Equal(plain.Width + 10f * (clusters - 1), tracked.Width, 2);
    }

    [Fact]
    public void TestRightAlignedArabicEndsOnItsPoint()
    {
        // Measured as it is drawn, so right-aligned right-to-left text ends where it was asked to. Measured one way
        // and drawn another, it would sit off by the difference between the two widths.
        var result = new JsDrawingEngine().Execute("""
            const c = createCanvas(400, 100);
            const x = c.getContext('2d');
            x.fillStyle = '#fff'; x.fillRect(0, 0, 400, 100);
            x.fillStyle = '#000'; x.font = '48px sans-serif'; x.textAlign = 'right';
            x.fillText('حارس المنارة', 380, 70);
            c;
            """, 400, 100, null, "png", 100);
        Assert.True(result.Success, result.Error);

        using var bitmap = SKBitmap.Decode(result.ImageBytes!);
        var right = -1;
        for (var px = bitmap.Width - 1; px >= 0 && right < 0; px--)
            for (var y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(px, y).Red < 128) { right = px; break; }

        Assert.InRange(right, 372, 382);
    }

    [Fact]
    public void TestFillTextDrawsRightToLeftScriptsWithInk()
    {
        foreach (var sample in new[] { "שלום", "سلام", "등대지기" })
        {
            var result = new JsDrawingEngine().Execute($$"""
                const c = createCanvas(400, 100);
                const x = c.getContext('2d');
                x.fillStyle = '#fff'; x.fillRect(0, 0, 400, 100);
                x.fillStyle = '#000'; x.font = '48px sans-serif';
                x.fillText('{{sample}}', 20, 70);
                c;
                """, 400, 100, null, "png", 100);
            Assert.True(result.Success, result.Error);

            using var bitmap = SKBitmap.Decode(result.ImageBytes!);
            var ink = 0;
            for (var y = 0; y < bitmap.Height; y++)
                for (var px = 0; px < bitmap.Width; px++)
                    if (bitmap.GetPixel(px, y).Red < 128) ink++;
            Assert.True(ink > 200, $"'{sample}' drew {ink} ink pixels");
        }
    }
    #endregion
}
