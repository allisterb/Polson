namespace Polson.Tests.MCPServer;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// <c>outPdf</c>: a print file from the returned paper, at a stated size with bleed.
/// </summary>
/// <remarks>
/// The refusals are what matter most. A print PDF that silently dropped the raster half of a poster, or
/// was sized by a guess, would reach a print shop looking finished.
/// </remarks>
public class PrintPdfTests : TestsRuntime, IDisposable
{
    #region Constants
    /// <summary>A2 proportions, background carried 20 units past every edge — past a 3 mm bleed at A2.</summary>
    private const string BledPoster =
        "const p = Snap(1000, 1414.29); p.rect(-20, -20, 1040, 1454.29).attr({ fill: '#c0392b' }); "
      + "p.text(100, 300, 'Get screened').attr({ 'font-size': 80, fill: '#000' }); p;";

    private const string EdgePoster =
        "const p = Snap(1000, 1414.29); p.rect(0, 0, 1000, 1414.29).attr({ fill: '#c0392b' }); p;";

    private const string MixedScript =
        "const p = Snap(1000, 1414.29); p.circle(500, 700, 200); "
      + "const c = createCanvas(1000, 1414); c.getContext('2d').fillRect(0, 0, 10, 10); c;";
    #endregion

    #region Fields
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-pdf-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public PrintPdfTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TestAPaperWritesAPrintPdfAndSaysHowItScales()
    {
        var result = await Tools().ExecuteScript(BledPoster, outFile: "artifacts/poster.webp",
            outPdf: "artifacts/poster.pdf", pdfSize: "A2");

        Assert.True(result.Success, result.Error);
        var pdf = Path.Combine(root, "artifacts", "poster.pdf");
        Assert.Equal(pdf, result.PdfFilePath);
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(File.ReadAllBytes(pdf).AsSpan(0, 4)));
        Assert.Contains("/TrimBox", Encoding.Latin1.GetString(File.ReadAllBytes(pdf)));

        var line = Assert.Single(result.Logs, l => l.StartsWith("[PDF]", StringComparison.Ordinal));
        Assert.Contains("420 x 594 mm", line);
        Assert.Contains("bleed 3 mm", line);
        Assert.Contains("0.42 mm", line);
        Assert.DoesNotContain(result.Logs, l => l.Contains("[WARN]", StringComparison.Ordinal));
        Assert.Contains("\"format\":\"pdf\"", File.ReadAllText(Path.Combine(root, "events", "server.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestArtworkStoppingAtTheTrimIsWarnedAbout()
    {
        var result = await Tools().ExecuteScript(EdgePoster, outPdf: "artifacts/poster.pdf", pdfSize: "A2");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.PdfFilePath);
        Assert.Equal(4, result.Logs.Count(l => l.Contains("stops at the trim", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TestAMissingSizeIsRefusedBeforeTheScriptRuns()
    {
        var result = await Tools().ExecuteScript(BledPoster, outPdf: "artifacts/poster.pdf");

        Assert.False(result.Success);
        Assert.Contains("needs pdfSize", result.Error);
        Assert.False(File.Exists(Path.Combine(root, "artifacts", "poster.pdf")));
    }

    [Theory]
    [InlineData("artifacts/poster.png", "A2", null, "must end in .pdf")]
    [InlineData("artifacts/poster.pdf", "A11", null, "is not a size")]
    [InlineData("artifacts/poster.pdf", "A2", "3", "needs a unit")]
    public async Task TestBadPrintArgumentsAreRefused(string outPdf, string size, string? bleed, string expected)
    {
        var result = await Tools().ExecuteScript(BledPoster, outPdf: outPdf, pdfSize: size, pdfBleed: bleed);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public async Task TestAPaperInTheWrongProportionsWritesNothingAndSaysSo()
    {
        var result = await Tools().ExecuteScript("const p = Snap(800, 800); p.circle(400, 400, 100); p;",
            outPdf: "artifacts/poster.pdf", pdfSize: "A2");

        Assert.True(result.Success, result.Error);
        Assert.Null(result.PdfFilePath);
        var warning = Assert.Single(result.Logs, l => l.Contains("outPdf", StringComparison.Ordinal));
        Assert.Contains("stretched", warning);
    }

    /// <summary>
    /// A script that returns a canvas keeps its last paper for <c>outSvg</c>, but that paper is not the
    /// picture — printing it would ship the poster with its raster half missing.
    /// </summary>
    [Fact]
    public async Task TestAReturnedCanvasIsNotPrintedFromItsLeftoverPaper()
    {
        var result = await Tools().ExecuteScript(MixedScript, outPdf: "artifacts/poster.pdf", pdfSize: "A2");

        Assert.True(result.Success, result.Error);
        Assert.Null(result.PdfFilePath);
        Assert.False(File.Exists(Path.Combine(root, "artifacts", "poster.pdf")));
        var warning = Assert.Single(result.Logs, l => l.Contains("outPdf", StringComparison.Ordinal));
        Assert.Contains("did not return a Snap paper", warning);
        Assert.Contains("render.nopdf", File.ReadAllText(Path.Combine(root, "events", "server.jsonl")), StringComparison.Ordinal);
    }

    /// <summary>A 100 px canvas stretched to 210 mm reaches the agent as a warning with the pixels it needs.</summary>
    [Fact]
    public async Task TestALowResolutionBitmapIsWarnedAboutOnTheResponse()
    {
        const string script =
            "const c = createCanvas(100, 100); const x = c.getContext('2d'); x.fillStyle = '#1e78c8'; x.fillRect(0, 0, 100, 100); "
          + "const p = Snap(1000, 1414.29); p.rect(-20, -20, 1040, 1454.29).attr({ fill: '#fff' }); "
          + "p.image(c, 100, 200, 500, 500); p;";

        var result = await Tools().ExecuteScript(script, outPdf: "artifacts/poster.pdf", pdfSize: "A2");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.PdfFilePath);
        Assert.Contains("1 bitmap(s) placed; the lowest prints at 12 ppi", Assert.Single(result.Logs, l => l.StartsWith("[PDF]", StringComparison.Ordinal)));
        var warning = Assert.Single(result.Logs, l => l.Contains("ppi —", StringComparison.Ordinal));
        Assert.Contains("100 x 100 px bitmap prints 210 x 210 mm", warning);
        Assert.Contains("\"lowestPpi\":12", File.ReadAllText(Path.Combine(root, "events", "server.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestTextNearTheTrimIsWarnedAboutAndTheMarginCanBeSet()
    {
        const string script =
            "const p = Snap(1000, 1414.29); p.rect(-20, -20, 1040, 1454.29).attr({ fill: '#fff' }); "
          + "p.text(8, 700, 'Call 0800 123 456').attr({ 'font-size': 40 }); p;";

        // 8 units is 3.4 mm to the anchor; with the C's side bearing the ink is about 4 mm in — under 5, over 3.
        var defaulted = await Tools().ExecuteScript(script, outPdf: "artifacts/a.pdf", pdfSize: "A2");
        Assert.Contains("text kept 5 mm inside the trim", Assert.Single(defaulted.Logs, l => l.StartsWith("[PDF]", StringComparison.Ordinal)));
        Assert.Contains(defaulted.Logs, l => l.Contains("\"Call 0800 123 456\" comes within about", StringComparison.Ordinal)
            && l.Contains("of the left trim", StringComparison.Ordinal));

        var narrow = await Tools().ExecuteScript(script, outPdf: "artifacts/c.pdf", pdfSize: "A2", pdfSafeMargin: "3mm");
        Assert.DoesNotContain(narrow.Logs, l => l.Contains("comes within", StringComparison.Ordinal));

        var off = await Tools().ExecuteScript(script, outPdf: "artifacts/d.pdf", pdfSize: "A2", pdfSafeMargin: "0");
        Assert.Contains("safe margin not checked", Assert.Single(off.Logs, l => l.StartsWith("[PDF]", StringComparison.Ordinal)));
    }

    /// <summary>The documented way to set a paragraph — one tspan per line, each with x and y — is what both checks read.</summary>
    [Fact]
    public async Task TestLineLengthAndGroupSpacingReachTheResponse()
    {
        const string script =
            "const p = Snap(1000, 1414.29); p.rect(-20, -20, 1040, 1454.29).attr({ fill: '#fff' }); "
          + "const t = p.text(0, 0, '').attr({ 'font-size': 24, 'font-family': 'Arial, DejaVu Sans, sans-serif' }); "
          + "[['Free blood pressure checks', 300], ['Saturday mornings in May', 336], ['at the Market Street clinic', 372], "
          + " ['No appointment is needed, just walk in and ask for a check at the front desk', 412], ['Bring a friend', 448]]"
          + ".forEach(([line, y]) => t.el('tspan', { x: 100, y }).attr({ text: line })); p;";

        var result = await Tools().ExecuteScript(script, outPdf: "artifacts/poster.pdf", pdfSize: "A2");

        Assert.True(result.Success, result.Error);
        var count = "No appointment is needed, just walk in and ask for a check at the front desk".Length;
        Assert.Contains(result.Logs, l => l.Contains($"\"No appointment is needed, just …\" runs to {count} characters", StringComparison.Ordinal));
        Assert.Contains(result.Logs, l => l.Contains("The space between \"at the Market Street clinic\" and \"No appointment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestTextOverATextureIsWarnedAbout()
    {
        // A canvas of random grey pixels, enlarged to fill the page, with a headline straight over it.
        const string script =
            "const c = createCanvas(300, 424); const x = c.getContext('2d'); "
          + "for (let i = 0; i < 300; i += 2) for (let j = 0; j < 424; j += 2) { const v = Math.floor(Math.random() * 256); "
          + "x.fillStyle = `rgb(${v},${v},${v})`; x.fillRect(i, j, 2, 2); } "
          + "const p = Snap(1000, 1414.29); p.image(c, -20, -20, 1040, 1454.29); "
          + "p.text(100, 600, 'Get screened').attr({ 'font-size': 60, fill: '#fff' }); p;";

        var result = await Tools().ExecuteScript(script, outPdf: "artifacts/poster.pdf", pdfSize: "A2");

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("\"Get screened\" is set over a busy background", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestAPdfPathOutsideTheProjectIsRefused()
    {
        var result = await Tools().ExecuteScript(BledPoster, outPdf: "../escaped.pdf", pdfSize: "A2");

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escaped.pdf")));
    }
    #endregion

    #region Methods (private)
    /// <summary>
    /// A family Skia does not have is refused where <c>attr()</c> sets it, and warned about on every paper render when it
    /// arrives another way — here in parsed markup — asked for a print file or not, and recorded.
    /// </summary>
    [Fact]
    public async Task TestAMissingFontFamilyIsWarnedOnAnyPaperRender()
    {
        var refused = await Tools().ExecuteScript(
            "const p = Snap(400, 200); p.text(10, 50, 'x').attr({ id: 'title', 'font-family': \"'Polson Nonexistent Face 7Q', serif\" }); p;",
            outFile: "artifacts/a.png");
        Assert.False(refused.Success);
        Assert.Contains("names no installed family", refused.Error);

        var result = await Tools().ExecuteScript(
            "const p = Snap.parse(\"<svg xmlns='http://www.w3.org/2000/svg' width='400' height='200'><text id='title' x='10' y='50' "
            + "font-family=\\\"'Polson Nonexistent Face 7Q', serif\\\">x</text></svg>\"); p;",
            outFile: "artifacts/a.png");

        Assert.True(result.Success, result.Error);
        var warning = Assert.Single(result.Logs, l => l.StartsWith("[WARN] font-family", StringComparison.Ordinal));
        Assert.Contains("(#title) names no installed family", warning);
        Assert.Contains("render.fontsubstituted", File.ReadAllText(Path.Combine(root, "events", "server.jsonl")), StringComparison.Ordinal);
    }

    /// <summary>
    /// lanternkeepercloud1 delivered 1000 × 1500 posters to a brief asking for 1200 × 1800. A deliverable at a size the
    /// brief does not name is warned about and recorded; a draft is not, and neither is a deliverable at the size asked.
    /// </summary>
    [Fact]
    public async Task TestADeliverableAtASizeTheBriefDoesNotNameIsWarned()
    {
        File.WriteAllText(Path.Combine(root, "brief.md"), "# Brief\n\n- Sizes: Portrait 1200 × 1800 px\n- Formats: .png\n");
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        async Task<IList<string>> Render(int w, int h, string file)
        {
            var r = await Tools().ExecuteScript($"const c = createCanvas({w}, {h}); c.getContext('2d').fillRect(0, 0, 10, 10); c;", outFile: file);
            Assert.True(r.Success, r.Error);
            return r.Logs;
        }

        var off = Assert.Single(await Render(1000, 1500, "artifacts/final_b.png"), l => l.Contains("the brief asks for", StringComparison.Ordinal));
        Assert.Contains("artifacts/final_b.png is 1000 × 1500 px, and the brief asks for 1200 × 1800", off);
        Assert.Contains("render.offsize", File.ReadAllText(Path.Combine(root, "events", "server.jsonl")), StringComparison.Ordinal);
        Assert.DoesNotContain(await Render(1000, 1500, "artifacts/draft_b.png"), l => l.Contains("the brief asks for", StringComparison.Ordinal));
        Assert.DoesNotContain(await Render(1200, 1800, "artifacts/final_b.png"), l => l.Contains("the brief asks for", StringComparison.Ordinal));
    }

    private DrawingMcpTools Tools() => new(null, null, null, root);
    #endregion
}
