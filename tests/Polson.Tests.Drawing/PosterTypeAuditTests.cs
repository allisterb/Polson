namespace Polson.Tests.Drawing;

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Polson.Drawing.Svg;
using Svg;
using Xunit;

/// <summary>
/// Line length and group spacing on a poster's type, measured from the document's lines.
/// </summary>
/// <remarks>
/// The spacing cases are built from line steps, not gaps: the gap is the step less the font's ascent plus descent,
/// which differs between Arial and DejaVu Sans (whichever this machine has). The ratios are chosen to land on the
/// right side of the 1.3 and 1.9 thresholds for both.
/// </remarks>
public class PosterTypeAuditTests : TestsRuntime
{
    #region Fields
    private const string Face = "font-family=\"Arial, DejaVu Sans, sans-serif\"";
    #endregion

    #region Methods
    private static SvgDocument Doc(string body) => SvgDocument.FromSvg<SvgDocument>(
        $"""<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="1414" viewBox="0 0 1000 1414">{body}</svg>""");

    /// <summary>Lines at 24 pt down from y = 200, each step in <paramref name="steps"/> added to the last baseline.</summary>
    private static string Stack(float x, params float[] steps)
    {
        var body = new StringBuilder();
        var y = 200f;
        var n = 1;
        body.Append(CultureInfo.InvariantCulture, $"""<text x="{x}" y="{y}" {Face} font-size="24">Line {n}</text>""");
        foreach (var step in steps)
        {
            y += step;
            body.Append(CultureInfo.InvariantCulture, $"""<text x="{x}" y="{y}" {Face} font-size="24">Line {++n}</text>""");
        }
        return body.ToString();
    }

    private static string[] Spacing(string body) => [.. PosterTypeAudit.CheckGroupSpacing(PosterTypeAudit.Lines(Doc(body)))];

    private static string[] Length(string body) => [.. PosterTypeAudit.CheckLineLength(PosterTypeAudit.Lines(Doc(body)))];

    [Fact]
    public void TestALongLineIsReportedWithItsCount()
    {
        var words = string.Concat(Enumerable.Repeat("Screening is free. ", 4)).Trim();
        var warning = Assert.Single(Length($"""<text x="50" y="300" {Face} font-size="24">{words}</text>"""));

        Assert.Contains($"runs to {words.Length} characters", warning);
        Assert.StartsWith("\"Screening is free. Screening is…\"", warning);
    }

    /// <summary>
    /// A line marked <c>data-print-exempt="line-length"</c>, on itself or a group around it, is not reported; its unmarked
    /// neighbour still is. poster3's mandated "Unofficial poster study" line fired on every print file.
    /// </summary>
    [Fact]
    public void TestAnExemptLineIsNotReportedAndItsNeighbourIs()
    {
        const string legal = "Unofficial poster study — not affiliated with the film's studio or distributors.";
        var other = string.Concat(Enumerable.Repeat("Screening is free. ", 4)).Trim();

        var warnings = Length($"""
            <text x="50" y="300" {Face} font-size="24" data-print-exempt="line-length">{legal}</text>
            <g data-print-exempt="safe-area, line-length"><text x="50" y="400" {Face} font-size="24">{legal} Again.</text></g>
            <text x="50" y="500" {Face} font-size="24">{other}</text>
            """);

        Assert.StartsWith("\"Screening", Assert.Single(warnings));
    }

    /// <summary>The attribute set from a script survives into the document the checks read, and exempts there too.</summary>
    [Fact]
    public void TestAnExemptionSetThroughSnapReachesTheCheck()
    {
        var paper = Snap.Create(1000, 1414);
        paper.Text(50, 300, "Unofficial poster study — not affiliated with the film's studio or distributors.")
            .Attr(PosterTypeAudit.ExemptAttribute, PosterTypeAudit.LineLengthCheck);

        Assert.Empty(PosterTypeAudit.CheckLineLength(PosterTypeAudit.Lines(SvgDocument.FromSvg<SvgDocument>(paper.ToString()))));
    }

    [Fact]
    public void TestEachTspanWithItsOwnYIsALine()
    {
        var lines = PosterTypeAudit.Lines(Doc($"""
            <text {Face} font-size="24">
              <tspan x="50" y="300">Free screening for adults over fifty</tspan>
              <tspan x="50" y="330">at the clinic on Market Street, every Saturday morning in May and June</tspan>
            </text>
            """));

        Assert.Equal(2, lines.Count);
        var warning = Assert.Single(PosterTypeAudit.CheckLineLength(lines));
        Assert.StartsWith("\"at the clinic", warning);
    }

    /// <summary>
    /// A run from <c>paper.trackedText(...)</c> — one <c>&lt;text&gt;</c> per glyph — is read as one line.
    /// </summary>
    /// <remarks>The poster1 run's tracked title was sixteen one-letter lines to these checks, and so invisible to both.</remarks>
    [Fact]
    public void TestATrackedRunIsOneLine()
    {
        var paper = Snap.Create(1000, 1414);
        paper.TrackedText(500, 600, "LA LARGA ESCUCHA", "0.05em",
            new System.Collections.Generic.Dictionary<string, object?> { ["font-family"] = "Arial", ["font-size"] = 75, ["text-anchor"] = "middle" });
        paper.Text(100, 700, "Algunas señales son para una sola persona.").Attr("font-size", 24);

        var lines = PosterTypeAudit.Lines(PosterTypeAudit.Parse(paper.ToString()));

        Assert.Equal(2, lines.Count);
        var title = Assert.Single(lines, l => l.Words.StartsWith("LA", StringComparison.Ordinal));
        Assert.Equal("LA LARGA ESCUCHA", title.Words);
        Assert.InRange(title.Bounds.MidX, 495f, 505f);
    }

    [Fact]
    public void TestASpanThatOnlyShiftsContinuesItsLine()
    {
        var lines = PosterTypeAudit.Lines(Doc($"""<text x="50" y="300" {Face} font-size="24">Call <tspan dx="4" font-weight="bold">0800 123 456</tspan> today</text>"""));

        Assert.Equal("Call 0800 123 456 today", Assert.Single(lines).Words);
    }

    [Fact]
    public void TestEvenSpacingAndAClearBreakPass()
    {
        // Four lines at one step, then a step that adds a clear 16 units, then two more lines.
        Assert.Empty(Spacing(Stack(50, 36, 36, 36, 52, 36)));
    }

    [Fact]
    public void TestANearMissBetweenGroupsIsReported()
    {
        // The fourth gap is 4 units wider than the rest: about 1.4–1.5x, which reads as neither.
        var warning = Assert.Single(Spacing(Stack(50, 36, 36, 40, 36)));

        Assert.StartsWith("The space between \"Line 3\" and \"Line 4\"", warning);
        Assert.Contains("too much to read as one group", warning);
    }

    [Fact]
    public void TestAScaledGroupIsJudgedTheSame()
    {
        var warning = Assert.Single(Spacing($"""<g transform="scale(1.5)">{Stack(50, 36, 36, 40, 36)}</g>"""));
        Assert.Contains("\"Line 3\" and \"Line 4\"", warning);
    }

    [Fact]
    public void TestSideBySideColumnsAreNotCompared()
    {
        // Each column is evenly spaced on its own; together their steps would look mismatched.
        Assert.Empty(Spacing(Stack(50, 36, 36, 36) + Stack(600, 44, 44, 44)));
    }

    [Fact]
    public void TestRotatedLinesAreLeftOut()
    {
        Assert.Empty(Spacing($"""<g transform="rotate(90 500 500)">{Stack(50, 36, 36, 40, 36)}</g>"""));
    }

    /// <summary>Greyscale pixel noise, optionally blurred, as a data URI — a stand-in for photographic texture.</summary>
    private static string Noise(float blur)
    {
        var rng = new Random(7);
        using var raw = new SkiaSharp.SKBitmap(500, 707);
        for (var y = 0; y < raw.Height; y++)
            for (var x = 0; x < raw.Width; x++) { var v = (byte)rng.Next(256); raw.SetPixel(x, y, new SkiaSharp.SKColor(v, v, v)); }

        using var image = new SkiaSharp.SKBitmap(500, 707);
        using (var canvas = new SkiaSharp.SKCanvas(image))
        using (var paint = new SkiaSharp.SKPaint { ImageFilter = blur > 0 ? SkiaSharp.SKImageFilter.CreateBlur(blur, blur) : null })
            canvas.DrawImage(SkiaSharp.SKImage.FromBitmap(raw), 0, 0, paint);

        using var data = SkiaSharp.SKImage.FromBitmap(image).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    private static string[] Backgrounds(string background, string line = "Get screened this Saturday")
    {
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="1000" height="1414" viewBox="0 0 1000 1414">
              {background}
              <text x="100" y="600" {Face} font-size="40" fill="#000">{line}</text>
            </svg>
            """;
        var lines = PosterTypeAudit.Lines(SvgDocument.FromSvg<SvgDocument>(svg));
        return [.. PosterTypeAudit.CheckBackgrounds(PosterTypeAudit.MeasureBackgrounds(svg, lines))];
    }

    [Fact]
    public void TestFlatColourAndABroadGradientPass()
    {
        Assert.Empty(Backgrounds("""<rect width="1000" height="1414" fill="#f4d03f"/>"""));
        Assert.Empty(Backgrounds("""
            <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#111"/><stop offset="1" stop-color="#eee"/></linearGradient></defs>
            <rect width="1000" height="1414" fill="url(#g)"/>
            """));
    }

    [Fact]
    public void TestTextOverTextureIsReported()
    {
        var warning = Assert.Single(Backgrounds($"""<image width="1000" height="1414" xlink:href="{Noise(0)}"/>"""));

        Assert.StartsWith("\"Get screened this Saturday\" is set over a busy background", warning);
        Assert.Contains("panel", warning);
    }

    /// <summary>A soft mottle is not texture at the letters' scale: the measure is tied to the type size, not the pixels.</summary>
    [Fact]
    public void TestAHeavilySoftenedImagePasses()
    {
        Assert.Empty(Backgrounds($"""<image width="1000" height="1414" xlink:href="{Noise(8)}"/>"""));
    }

    /// <summary>The fix the warning suggests: a panel behind the line counts as its background, the line itself does not.</summary>
    [Fact]
    public void TestAPanelBehindTheLineClearsIt()
    {
        Assert.Empty(Backgrounds($"""
            <image width="1000" height="1414" xlink:href="{Noise(0)}"/>
            <rect x="80" y="550" width="700" height="70" fill="#fff" opacity="0.9"/>
            """));
    }

    [Fact]
    public void TestALineAcrossALightDarkEdgeIsReported()
    {
        var warning = Assert.Single(Backgrounds("""<rect width="1000" height="1414" fill="#fff"/><rect x="300" width="700" height="1414" fill="#111"/>"""));

        Assert.Contains("runs", warning);
        Assert.Contains("no one text colour contrasts with all along", warning);
        Assert.DoesNotContain("busy", warning);
    }

    [Fact]
    public void TestThePdfReportCarriesBothChecks()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"polson-type-{Guid.NewGuid():N}.pdf");
        try
        {
            var svg = $"""
                <svg xmlns="http://www.w3.org/2000/svg" width="1000" height="1414.29" viewBox="0 0 1000 1414.29">
                  <rect x="-20" y="-20" width="1040" height="1454.29" fill="#fff"/>
                  {Stack(100, 36, 36, 40, 36)}
                  <text x="100" y="900" {Face} font-size="20">{new string('x', 64)}</text>
                </svg>
                """;
            var report = SvgPdfWriter.Write(svg, path, PrintSize.Parse("A2"), 3 * PrintSize.PointsPerMm);

            Assert.Contains(report.Warnings, w => w.Contains("runs to 64 characters", StringComparison.Ordinal));
            Assert.Contains(report.Warnings, w => w.Contains("too much to read as one group", StringComparison.Ordinal));
        }
        finally { System.IO.File.Delete(path); }
    }
    #endregion
}
