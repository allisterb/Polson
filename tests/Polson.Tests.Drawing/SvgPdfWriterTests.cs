namespace Polson.Tests.Drawing;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Polson.Drawing.Svg;
using Xunit;

/// <summary>
/// Writing a paper as a print PDF: the physical size, the bleed, the print boxes, and the check that
/// artwork reaching the trim continues into the bleed.
/// </summary>
/// <remarks>
/// The boxes are asserted on the file's bytes, and the incremental update's xref is checked to point at
/// the objects it names: a table that is off by a byte is a file some RIPs repair silently and others reject.
/// </remarks>
public class SvgPdfWriterTests : TestsRuntime
{
    #region Fields
    private const float Mm = PrintSize.PointsPerMm;

    /// <summary>A2 portrait in A-series proportions, with a background drawn exactly to the paper's edges.</summary>
    private const string ToTheEdge = """
        <svg xmlns="http://www.w3.org/2000/svg" width="1000" height="1414.29" viewBox="0 0 1000 1414.29">
          <rect x="0" y="0" width="1000" height="1414.29" fill="#c0392b"/>
          <text x="100" y="300" font-family="DejaVu Sans, Arial, sans-serif" font-size="80" fill="#000000">Get screened</text>
        </svg>
        """;

    /// <summary>The same, with the background carried 20 units past every edge — more than 3 mm at A2.</summary>
    private const string IntoTheBleed = """
        <svg xmlns="http://www.w3.org/2000/svg" width="1000" height="1414.29" viewBox="0 0 1000 1414.29">
          <rect x="-20" y="-20" width="1040" height="1454.29" fill="#c0392b"/>
          <text x="100" y="300" font-family="DejaVu Sans, Arial, sans-serif" font-size="80" fill="#000000">Get screened</text>
        </svg>
        """;
    #endregion

    #region Methods
    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"polson-pdf-{Guid.NewGuid():N}.pdf");

    private static float[] Box(string pdf, string name) =>
        Regex.Matches(pdf, $@"/{name}\s*\[([^\]]+)\]").Last().Groups[1].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    public void TestA2WithBleedHasTheBoxesAPrintShopReads()
    {
        var path = TempPdf();
        try
        {
            var report = SvgPdfWriter.Write(IntoTheBleed, path, PrintSize.Parse("A2"), PrintSize.ParseLength("3mm", "pdfBleed"));
            var pdf = Encoding.Latin1.GetString(File.ReadAllBytes(path));

            var media = Box(pdf, "MediaBox");
            var trim = Box(pdf, "TrimBox");
            var bleed = Box(pdf, "BleedBox");

            Assert.Equal(426 * Mm, media[2], 0.5f);
            Assert.Equal(600 * Mm, media[3], 0.5f);
            Assert.Equal(new[] { 0f, 0f, media[2], media[3] }, bleed, new ToleranceComparer(0.01f));
            Assert.Equal(3 * Mm, trim[0], 0.01f);
            Assert.Equal(3 * Mm, trim[1], 0.01f);
            Assert.Equal(420 * Mm, trim[2] - trim[0], 0.5f);
            Assert.Equal(594 * Mm, trim[3] - trim[1], 0.5f);

            Assert.Equal(0.42f, report.MillimetresPerUnit, 0.001f);
            Assert.Empty(report.Warnings);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TestTheIncrementalXrefPointsAtTheRevisedPages()
    {
        var path = TempPdf();
        try
        {
            SvgPdfWriter.Write(IntoTheBleed, path, PrintSize.Parse("A2"), 3 * Mm);
            var pdf = Encoding.Latin1.GetString(File.ReadAllBytes(path));

            var startXref = int.Parse(Regex.Matches(pdf, @"startxref\s+(\d+)").Last().Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.StartsWith("xref", pdf[startXref..]);

            var entries = Regex.Matches(pdf[startXref..], @"(\d+) 1\n(\d{10}) (\d{5}) n\r\n");
            Assert.NotEmpty(entries);
            foreach (Match e in entries)
            {
                var offset = int.Parse(e.Groups[2].Value, CultureInfo.InvariantCulture);
                Assert.StartsWith($"{e.Groups[1].Value} {int.Parse(e.Groups[3].Value, CultureInfo.InvariantCulture)} obj", pdf[offset..]);
                Assert.Contains("/TrimBox", pdf[offset..pdf.IndexOf("endobj", offset, StringComparison.Ordinal)]);
            }

            Assert.Matches(@"/Prev \d+", pdf[startXref..]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TestArtworkThatStopsAtTheTrimIsReportedOnEverySide()
    {
        var path = TempPdf();
        try
        {
            var report = SvgPdfWriter.Write(ToTheEdge, path, PrintSize.Parse("A2"), 3 * Mm);

            Assert.Equal(4, report.Warnings.Count);
            foreach (var side in new[] { "top", "bottom", "left", "right" })
                Assert.Contains(report.Warnings, w => w.Contains($"{side} edge", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TestNoBleedSkipsTheCheckAndTrimEqualsMedia()
    {
        var path = TempPdf();
        try
        {
            var report = SvgPdfWriter.Write(ToTheEdge, path, PrintSize.Parse("A2"), PrintSize.ParseLength("0", "pdfBleed"));
            var pdf = Encoding.Latin1.GetString(File.ReadAllBytes(path));

            Assert.Empty(report.Warnings);
            Assert.Equal(Box(pdf, "MediaBox"), Box(pdf, "TrimBox"), new ToleranceComparer(0.01f));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TestTextIsEmbeddedAsAFontNotRasterised()
    {
        var path = TempPdf();
        try
        {
            SvgPdfWriter.Write(IntoTheBleed, path, PrintSize.Parse("A2"), 3 * Mm);
            var pdf = Encoding.Latin1.GetString(File.ReadAllBytes(path));

            Assert.Contains("/Type /Font", pdf);
            Assert.Matches(@"/FontFile\d?", pdf);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TestMismatchedProportionsAreRefusedWithTheFix()
    {
        var path = TempPdf();
        try
        {
            var square = """<svg xmlns="http://www.w3.org/2000/svg" width="800" height="800" viewBox="0 0 800 800"/>""";
            var ex = Assert.Throws<ArgumentException>(() => SvgPdfWriter.Write(square, path, PrintSize.Parse("A2"), 3 * Mm));
            Assert.Contains("Snap(1000, 1414)", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TestANamedSizeTakesTheArtworksOrientation()
    {
        var a2 = PrintSize.Parse("a2");
        var landscape = a2.OrientedLike(1414, 1000);

        Assert.True(landscape.WidthPt > landscape.HeightPt);
        Assert.Equal(a2.HeightPt, landscape.WidthPt);

        var explicitSize = PrintSize.Parse("24x18in");
        Assert.Equal(explicitSize, explicitSize.OrientedLike(1000, 1333));
        Assert.Equal(24 * 72f, explicitSize.WidthPt, 0.01f);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("0.125")]
    [InlineData("3 furlongs")]
    public void TestALengthWithoutAUnitIsRefused(string text)
    {
        var ex = Assert.Throws<ArgumentException>(() => PrintSize.ParseLength(text, "pdfBleed"));
        Assert.Contains("needs a unit", ex.Message);
    }

    /// <summary>A solid PNG of the given size as a data URI, the way <c>paper.image(bitmap, …)</c> inlines one.</summary>
    private static string Png(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(new SkiaSharp.SKColor(30, 120, 200));
        using var data = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    /// <summary>An A2 poster, background into the bleed, carrying <paramref name="body"/>. One unit prints at 0.42 mm.</summary>
    private static string PosterWith(string body) => $"""
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="1000" height="1414.29" viewBox="0 0 1000 1414.29">
          <rect x="-20" y="-20" width="1040" height="1454.29" fill="#ffffff"/>
          {body}
        </svg>
        """;

    private static PdfPrintReport WritePoster(string body)
    {
        var path = TempPdf();
        try { return SvgPdfWriter.Write(PosterWith(body), path, PrintSize.Parse("A2"), 3 * Mm); }
        finally { File.Delete(path); }
    }

    /// <summary>100 px across 500 units — 210 mm — is about 12 ppi.</summary>
    [Fact]
    public void TestALowResolutionBitmapIsReportedWithThePixelsItNeeds()
    {
        var report = WritePoster($"""<image x="100" y="200" width="500" height="500" xlink:href="{Png(100, 100)}"/>""");

        var bitmap = Assert.Single(report.Bitmaps);
        Assert.Equal(100, bitmap.PixelWidth);
        Assert.Equal(210, bitmap.WidthPt / Mm, 0.5f);
        Assert.Equal(12.1f, bitmap.EffectivePpi, 0.2f);

        var warning = Assert.Single(report.Warnings);
        Assert.Contains("100 x 100 px bitmap prints 210 x 210 mm", warning);
        Assert.Contains("at 12 ppi", warning);
        // 210 mm is 8.27 in: about 1240 px at 150 ppi and 2480 at 300, give or take a pixel of rounding.
        var needed = Regex.Match(warning, @"needs (\d+) x \d+ px for 150 ppi \((\d+) x \d+ for 300\)");
        Assert.True(needed.Success, warning);
        Assert.InRange(int.Parse(needed.Groups[1].Value, CultureInfo.InvariantCulture), 1240, 1241);
        Assert.InRange(int.Parse(needed.Groups[2].Value, CultureInfo.InvariantCulture), 2480, 2481);
        Assert.Contains("42 mm from the left and 84 mm from the top", warning);
    }

    /// <summary>1000 px across 100 units — 42 mm — is about 605 ppi.</summary>
    [Fact]
    public void TestAHighResolutionBitmapPassesQuietly()
    {
        var report = WritePoster($"""<image x="100" y="200" width="100" height="100" xlink:href="{Png(1000, 1000)}"/>""");

        Assert.Equal(605, Assert.Single(report.Bitmaps).EffectivePpi, 2f);
        Assert.Empty(report.Warnings);
    }

    /// <summary>
    /// The case reading the SVG would get wrong: a group scale enlarges the bitmap on the page without
    /// changing its width attribute. 1000 px at 100 units passes alone, and fails four times larger.
    /// </summary>
    [Fact]
    public void TestATransformedGroupIsMeasuredAtItsPrintedSize()
    {
        var report = WritePoster($"""<g transform="scale(4)"><image x="10" y="10" width="100" height="100" xlink:href="{Png(1000, 1000)}"/></g>""");

        var bitmap = Assert.Single(report.Bitmaps);
        Assert.Equal(168, bitmap.WidthPt / Mm, 0.5f);
        Assert.Equal(151, bitmap.EffectivePpi, 2f);
        Assert.Empty(report.Warnings);

        var five = WritePoster($"""<g transform="scale(5)"><image x="10" y="10" width="100" height="100" xlink:href="{Png(1000, 1000)}"/></g>""");
        Assert.Contains("at 121 ppi", Assert.Single(five.Warnings));
    }

    /// <summary>Opacity makes Skia draw through a transparency group, which is a form XObject with a matrix of its own.</summary>
    [Fact]
    public void TestABitmapInsideAnOpacityGroupIsStillMeasured()
    {
        var report = WritePoster($"""<g opacity="0.5"><image x="100" y="200" width="500" height="500" xlink:href="{Png(100, 100)}"/></g>""");

        Assert.Equal(12.1f, Assert.Single(report.Bitmaps).EffectivePpi, 0.2f);
        Assert.Contains(report.Warnings, w => w.Contains("at 12 ppi", StringComparison.Ordinal));
    }

    /// <summary>A bitmap used as a pattern fill is not a placement the audit can size, and it says so.</summary>
    [Fact]
    public void TestABitmapInAPatternFillIsReportedAsUnmeasured()
    {
        var report = WritePoster($"""
            <defs><pattern id="p" width="50" height="50" patternUnits="userSpaceOnUse">
              <image width="50" height="50" xlink:href="{Png(20, 20)}"/></pattern></defs>
            <rect x="100" y="100" width="600" height="600" fill="url(#p)"/>
            """);

        // Skia draws a pattern through a shader, not a Do, so nothing is placed — and the image objects it
        // wrote are counted instead. Measured: two objects for this one bitmap. Silence would read as a pass.
        Assert.Empty(report.Bitmaps);
        Assert.True(report.UnplacedBitmaps > 0);
        Assert.Contains(report.Warnings, w => w.Contains("not measured", StringComparison.Ordinal));
    }

    /// <summary>poster5's grain: fractal noise kept to the source's shape, multiplied over it.</summary>
    private const string Grain = """
        <defs><filter id="grain" x="-10%" y="-10%" width="120%" height="120%">
          <feTurbulence baseFrequency="0.9" numOctaves="3" seed="5" type="fractalNoise" result="noise"/>
          <feComposite operator="in" in2="SourceGraphic" in="noise" result="grain"/>
          <feBlend mode="multiply" in2="grain" in="SourceGraphic"/>
        </filter></defs>
        """;

    /// <summary>
    /// poster5: one grain filter on a group spanning the sheet made <c>final_a.pdf</c> 20.5 MB against its sibling's
    /// 228 KB, and nothing said so. Here on A5, which is heavy enough to show it and quick enough to test.
    /// </summary>
    [Fact]
    public void TestAFilterRasterisedIntoMostOfTheFileIsNamedWithItsCost()
    {
        var path = TempPdf();
        try
        {
            var svg = $"""
                <svg xmlns="http://www.w3.org/2000/svg" width="1000" height="1414.29" viewBox="0 0 1000 1414.29">{Grain}
                  <rect x="-20" y="-20" width="1040" height="1454.29" fill="#f2e3c4"/>
                  <g id="image" filter="url(#grain)"><rect x="-20" y="-20" width="1040" height="1454.29" fill="#1f6f78"/></g>
                  <text x="100" y="300" font-family="DejaVu Sans, Arial, sans-serif" font-size="80" fill="#000000">Grain</text>
                </svg>
                """;
            var report = SvgPdfWriter.Write(svg, path, PrintSize.Parse("A5"), 3 * Mm);

            Assert.Equal(new FileInfo(path).Length, report.FileBytes);
            Assert.True(report.FileBytes > SvgPdfWriter.HeavyBitmapBytes, $"the fixture should be heavy: {report.FileBytes:N0} bytes");
            var warning = Assert.Single(report.Warnings, w => w.StartsWith("The filter on", StringComparison.Ordinal));
            Assert.StartsWith("The filter on <g id=\"image\"> (#grain: feTurbulence, feComposite, feBlend) has no PDF equivalent", warning);
            Assert.Matches(@"rasterised at 300 ppi into one [\d,]+ x [\d,]+ px bitmap over \d+ x \d+ mm — [\d.]+ MB of this [\d.]+ MB file; the rest of the page is \d+ KB", warning);
            Assert.Contains("put the filter on only the element that needs it", warning);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A filter on a small mark rasterises a small bitmap: worth nothing to report.</summary>
    [Fact]
    public void TestAFilterOnASmallMarkIsNotReported()
    {
        var report = WritePoster($"""{Grain}<circle id="dot" cx="500" cy="700" r="40" fill="#1f6f78" filter="url(#grain)"/>""");

        Assert.NotEmpty(report.Bitmaps);                                  // it was rasterised, at 300 ppi …
        Assert.Equal(300, report.Bitmaps.Min(b => b.EffectivePpi), 3f);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("filter on", StringComparison.Ordinal));   // … and is light
        Assert.True(report.FileBytes < SvgPdfWriter.HeavyBitmapBytes);
    }

    private static PdfPrintReport WriteText(string body, float? safeMarginPt = null)
    {
        var path = TempPdf();
        try { return SvgPdfWriter.Write(PosterWith(body), path, PrintSize.Parse("A2"), 3 * Mm, safeMarginPt: safeMarginPt); }
        finally { File.Delete(path); }
    }

    /// <summary>5 units is 2.1 mm at A2: inside the trim, inside the 5 mm margin.</summary>
    [Fact]
    public void TestTextNearTheTrimIsNamedWithItsDistance()
    {
        var report = WriteText("""<text x="5" y="700" font-family="DejaVu Sans, Arial" font-size="40" fill="#000">Near the edge</text>""");

        var warning = Assert.Single(report.Warnings);
        Assert.StartsWith("\"Near the edge\" comes within about", warning);
        Assert.Contains("of the left trim", warning);
        // 2.1 mm to the anchor, plus the N's side bearing, measured to about 0.6 mm: the ink is 3.5–4.5 mm in.
        var mm = float.Parse(Regex.Match(warning, @"about ([\d.]+) mm").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(mm, 2f, 4.9f);
    }

    [Fact]
    public void TestTextPastTheTrimIsReportedAsCutOff()
    {
        var report = WriteText("""<text x="-30" y="700" font-family="DejaVu Sans, Arial" font-size="40">Clipped</text>""");

        Assert.Contains(report.Warnings, w => w.StartsWith("\"Clipped\" crosses the left trim", StringComparison.Ordinal) && w.Contains("cut off"));
    }

    /// <summary>The anchor is mid-page; the line is not. Reading x would pass this.</summary>
    [Fact]
    public void TestACentredLineIsMeasuredByItsInkNotItsAnchor()
    {
        var report = WriteText("""<text x="500" y="700" text-anchor="middle" font-family="DejaVu Sans, Arial" font-size="60">A headline set far too wide for this poster</text>""");

        Assert.Contains(report.Warnings, w => w.Contains("left trim", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("right trim", StringComparison.Ordinal));
    }

    /// <summary>White type on a dark band is still type: the background is removed before measuring, not the colour.</summary>
    [Fact]
    public void TestWhiteTextOnABackgroundIsFound()
    {
        var report = WriteText("""
            <rect x="-20" y="-20" width="1040" height="200" fill="#1a1a1a"/>
            <text x="400" y="20" font-family="DejaVu Sans, Arial" font-size="40" fill="#ffffff">Top line</text>
            """);

        Assert.Contains(report.Warnings, w => w.StartsWith("\"Top line\"", StringComparison.Ordinal) && w.Contains("top trim"));
    }

    [Fact]
    public void TestTextInATransformedGroupIsMeasuredWhereItPrints()
    {
        var report = WriteText("""<g transform="translate(-80, 0)"><text x="50" y="700" font-family="DejaVu Sans, Arial" font-size="40">Moved</text></g>""");

        Assert.Contains(report.Warnings, w => w.StartsWith("\"Moved\" crosses the left trim", StringComparison.Ordinal));
    }

    [Fact]
    public void TestTextWellInsideAndShapesAtTheEdgeAreNotReported()
    {
        var report = WriteText("""
            <rect x="0" y="0" width="30" height="30" fill="#000"/>
            <text x="100" y="700" font-family="DejaVu Sans, Arial" font-size="40">Comfortably inside</text>
            """);

        // The square at the corner does draw the bleed warning — it stops at the trim — but it is not text.
        Assert.DoesNotContain(report.Warnings, w => w.Contains("comes within", StringComparison.Ordinal) || w.Contains("crosses the", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.StartsWith("Artwork stops at the trim", StringComparison.Ordinal));
    }

    [Fact]
    public void TestAZeroSafeMarginSkipsTheCheck()
    {
        var report = WriteText("""<text x="-30" y="700" font-size="40">Clipped</text>""", safeMarginPt: 0);

        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void TestAnUnknownSizeListsTheNames()
    {
        var ex = Assert.Throws<ArgumentException>(() => PrintSize.Parse("A9"));
        Assert.Contains("A2", ex.Message);
        Assert.Contains("420x594mm", ex.Message);
    }
    #endregion

    #region Types
    private sealed class ToleranceComparer(float tolerance) : System.Collections.Generic.IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => MathF.Abs(x - y) <= tolerance;

        public int GetHashCode(float obj) => 0;
    }
    #endregion
}
