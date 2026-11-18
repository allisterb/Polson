namespace Polson.Tests.MCPServer;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Polson.MCPServer;
using SkiaSharp;
using Xunit;

/// <summary>
/// <c>Stage.place</c>: a supplied logo or photograph fitted into a box without being stretched, with whether it will be
/// sharp on the deliverable recorded as a check the script cannot write.
/// </summary>
public class StagePlaceTests : TestsRuntime, IDisposable
{
    #region Constants
    /// <summary>An Illustrator-style logo: its colour comes from a class in a stylesheet, as exported logos usually do.</summary>
    private const string StyledLogo = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 50">
          <defs><style>.cls-1{fill:#ff0000;}</style></defs>
          <rect class="cls-1" x="0" y="0" width="200" height="50"/>
        </svg>
        """;

    /// <summary>Red on the left half, blue on the right.</summary>
    private const string HalvesLogo = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100">
          <rect x="0" y="0" width="100" height="100" fill="#ff0000"/>
          <rect x="100" y="0" width="100" height="100" fill="#0000ff"/>
        </svg>
        """;
    #endregion

    #region Fields
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-place-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Constructors
    public StagePlaceTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        Png("logo_small.png", 400, 100, SKColors.Red);
        Png("logo_large.png", 2000, 500, SKColors.Red);
        Png("photo.png", 1000, 500, SKColors.Red, SKColors.Blue);
        File.WriteAllText(Path.Combine(root, "documents", "styled.svg"), StyledLogo);
        File.WriteAllText(Path.Combine(root, "documents", "halves.svg"), HalvesLogo);
    }
    #endregion

    #region Methods
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Contain keeps the whole picture at its own shape, centred in the box; one too small for its size fails.</summary>
    [Fact]
    public void TestALogoIsFittedWholeAndASmallOneFailsAsSoft()
    {
        var result = Run("""
            const paper = Snap(1080, 1920);
            const p = Stage.place(paper, 'documents/logo_small.png', { x: 140, y: 0, width: 800, height: 800 });
            log(`drawn ${p.x} ${p.y} ${p.width} ${p.height} ratio ${p.ratio} sharp ${p.sharp} vector ${p.vector}`);
            paper;
            """, 1080, 1920);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("drawn 140 300 800 200 ratio 0.5 sharp false vector false"));
        var check = Assert.Single(result.Logs, l => l.StartsWith("[CHECK] FAIL placed sharp: documents/logo_small.png", StringComparison.Ordinal));
        Assert.Contains("enlarged 2.0×", check);
        Assert.Contains("at most 400 paper units wide", check);
        var record = File.ReadAllLines(Path.Combine(root, "events", "server.jsonl"));
        Assert.Contains(record, l => l.Contains("\"type\":\"place\"") && l.Contains("documents/logo_small.png") && l.Contains("\"fit\":\"contain\""));
    }

    /// <summary>The brief's size decides the scale: a 1080-wide paper for a 2160-wide sign needs twice the pixels.</summary>
    [Theory]
    [InlineData("logo_large.png", true)]
    [InlineData("logo_small.png", false)]
    public void TestSharpnessIsMeasuredAtTheBriefsSize(string file, bool sharp)
    {
        File.WriteAllText(Path.Combine(root, "brief.md"), "A sign.\n\n- Sizes: Portrait 4K 2160 × 3840 px\n");
        var result = Run($$"""
            const paper = Snap(1080, 1920);
            const p = Stage.place(paper, 'documents/{{file}}', { x: 140, y: 0, width: 800, height: 800 });
            log(`scale ${p.scale} sharp ${p.sharp}`);
            paper;
            """, 1080, 1920);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains($"scale 2 sharp {sharp.ToString().ToLowerInvariant()}"));
    }

    /// <summary>Cover fills the box and crops around the focus, never stretching: focus on the left keeps the red half.</summary>
    [Fact]
    public void TestCoverFillsTheBoxAndCropsAroundTheFocus()
    {
        var result = Run("""
            const paper = Snap(400, 400);
            paper.rect(0, 0, 400, 400).attr({ fill: '#ffffff' });
            const p = Stage.place(paper, 'documents/photo.png', { x: 0, y: 0, width: 400, height: 400 }, { fit: 'cover', focus: [0, 0.5] });
            log(`kept ${p.kept} drawn ${p.width}x${p.height}`);
            paper;
            """, 400, 400);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("kept 0.5 drawn 400x400"));
        using var bitmap = SKBitmap.Decode(result.ImageBytes);
        Assert.True(IsRed(bitmap.GetPixel(390, 200)), $"the right edge shows {bitmap.GetPixel(390, 200)}");
    }

    /// <summary>An SVG logo stays vector, its stylesheet's colours kept, and is always sharp.</summary>
    [Fact]
    public void TestAnSvgLogoStaysVectorWithItsStylesheet()
    {
        var result = Run("""
            const paper = Snap(400, 400);
            paper.rect(0, 0, 400, 400).attr({ fill: '#ffffff' });
            const p = Stage.place(paper, 'documents/styled.svg', { x: 0, y: 0, width: 400, height: 400 });
            log(`drawn ${p.y} ${p.height} vector ${p.vector} sharp ${p.sharp}`);
            paper;
            """, 400, 400);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("drawn 150 100 vector true sharp true"));
        Assert.Contains(result.Logs, l => l.StartsWith("[CHECK] PASS placed sharp: documents/styled.svg", StringComparison.Ordinal));
        using var bitmap = SKBitmap.Decode(result.ImageBytes);
        Assert.True(IsRed(bitmap.GetPixel(200, 200)), $"the logo shows {bitmap.GetPixel(200, 200)}");
        Assert.False(IsRed(bitmap.GetPixel(200, 60)), "the logo spilled outside its fitted height");
    }

    /// <summary>A cropped SVG is clipped to its box: nothing of the crop shows beside it.</summary>
    [Fact]
    public void TestACroppedSvgIsClippedToItsBox()
    {
        var result = Run("""
            const paper = Snap(300, 100);
            paper.rect(0, 0, 300, 100).attr({ fill: '#ffffff' });
            Stage.place(paper, 'documents/halves.svg', { x: 0, y: 0, width: 100, height: 100 }, { fit: 'cover', focus: [1, 0.5] });
            paper;
            """, 300, 100);

        Assert.True(result.Success, result.Error);
        using var bitmap = SKBitmap.Decode(result.ImageBytes);
        var inside = bitmap.GetPixel(50, 50);
        Assert.True(inside.Blue > 200 && inside.Red < 50, $"the box shows {inside}");
        var beside = bitmap.GetPixel(150, 50);
        Assert.True(beside.Red > 240 && beside.Green > 240 && beside.Blue > 240, $"beside the box shows {beside}");
    }

    /// <summary>francis_fashions_2: a black wordmark on the black it was sampled from fails; on white it stands.</summary>
    [Theory]
    [InlineData("#050505", false)]
    [InlineData("#ffffff", true)]
    public void TestALogosInkIsHeldAgainstWhatIsBehindIt(string ground, bool stands)
    {
        Wordmark("wordmark.png", opaque: false);
        var result = Run($$"""
            const paper = Snap(400, 400);
            paper.rect(0, 0, 400, 400).attr({ fill: '{{ground}}' });
            const p = Stage.place(paper, 'documents/wordmark.png', { x: 0, y: 0, width: 400, height: 400 });
            log(`contrast ${p.contrast.toFixed(1)}`);
            paper;
            """, 400, 400);

        Assert.True(result.Success, result.Error);
        Assert.Single(result.Logs, l => l.StartsWith($"[CHECK] {(stands ? "PASS" : "FAIL")} placed against its ground: documents/wordmark.png", StringComparison.Ordinal));
        if (!stands) Assert.Contains(result.Logs, l => l.Contains("never recolour"));
    }

    /// <summary>Each line of a wordmark is measured, so the smaller line is the one the legibility check gets; a picture with
    /// its own ground has no contrast to check, but its lines are measured against that ground.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TestAWordmarksLinesAreMeasuredOneByOne(bool opaque)
    {
        Wordmark("wordmark.png", opaque);
        var result = Run("""
            const paper = Snap(400, 400);
            paper.rect(0, 0, 400, 400).attr({ fill: '#ffffff' });
            const p = Stage.place(paper, 'documents/wordmark.png', { x: 0, y: 0, width: 400, height: 400 });
            log(`lines ${p.lines.map(l => Math.round(l.px)).join(',')} smallest ${Math.round(p.smallestLine)} contrast ${p.contrast}`);
            paper;
            """, 400, 400);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains($"lines 40,60 smallest 40 contrast {(opaque ? "null" : "")}"));
    }

    /// <summary>A store sign's final is held to the brief's words, as a logo's mark is: an invented "collection" fails.</summary>
    [Fact]
    public async Task TestAStoreSignsFinalCarriesOnlyTheBriefsWords()
    {
        File.WriteAllText(Path.Combine(root, "project.json"), "{ \"id\": \"s\", \"workflow\": \"poster\", \"type\": \"store\" }");
        File.WriteAllText(Path.Combine(root, "brief.md"), "BRIEF-BEGIN\nA sign.\n- Store: Shoe Locker\n- What it sells: urban shoes\nBRIEF-END\n");
        var tools = new DrawingMcpTools(null, null, null, root);

        var invented = await tools.ExecuteScript("const p = Snap(400, 200); p.text(20, 60, 'SHOE LOCKER COLLECTION'); p;",
            400, 200, outSvg: "artifacts/final_a.svg");
        var check = Assert.Single(invented.Logs, l => l.StartsWith("[CHECK] FAIL Client's words on artifacts/final_a.svg", StringComparison.Ordinal));
        Assert.Contains("COLLECTION", check);
        Assert.Contains("A store sign carries", check);

        var kept = await tools.ExecuteScript("const p = Snap(400, 200); p.text(20, 60, 'SHOE LOCKER'); p.text(20, 120, 'URBAN SHOES'); p;",
            400, 200, outSvg: "artifacts/final_b.svg");
        Assert.Single(kept.Logs, l => l.StartsWith("[CHECK] PASS Client's words on artifacts/final_b.svg", StringComparison.Ordinal));

        var comp = await tools.ExecuteScript("const p = Snap(400, 200); p.text(20, 60, 'SHOE LOCKER COLLECTION'); p;",
            400, 200, outSvg: "artifacts/04_comp_a.svg");
        Assert.DoesNotContain(comp.Logs, l => l.Contains("Client's words"));
    }

    /// <summary>Its verdict is its own: a script cannot write it.</summary>
    [Fact]
    public void TestTheSharpnessClaimCannotBeWrittenByHand()
    {
        var result = Run("Stage.check('placed sharp: documents/logo_small.png', true); createCanvas(8, 8);", 8, 8);

        Assert.False(result.Success);
        Assert.Contains("recorded by Stage.place itself", result.Error);
    }

    [Theory]
    [InlineData("Stage.place(null, 'documents/logo_small.png', { x: 0, y: 0, width: 10, height: 10 })", "the paper")]
    [InlineData("Stage.place(Snap(10, 10), 'documents/none.png', { x: 0, y: 0, width: 10, height: 10 })", "is not in the project")]
    [InlineData("Stage.place(Snap(10, 10), 'documents/logo_small.png', { x: 0, y: 0, width: 0, height: 10 })", "above 0")]
    [InlineData("Stage.place(Snap(10, 10), 'documents/logo_small.png', { x: 0, y: 0, width: 10, height: 10 }, { fit: 'stretch' })", "'contain'")]
    [InlineData("Stage.place(Snap(10, 10), 'documents/logo_small.png', { x: 0, y: 0, width: 10, height: 10 }, { focus: [2, 0] })", "each 0 to 1")]
    [InlineData("Stage.place(Snap(10, 10), 'documents/logo_small.png', { x: 0, y: 0, width: 10, height: 10 }, { size: 2 })", "no option 'size'")]
    public void TestWhatCannotBePlacedIsRefused(string call, string words)
    {
        var result = Run(call + "; createCanvas(8, 8);", 8, 8);

        Assert.False(result.Success);
        Assert.Contains(words, result.Error);
    }

    /// <summary>A deliverable names the brief's supplied pictures it never placed, while there is still time to place them.</summary>
    [Fact]
    public async Task TestAFinalRenderNamesTheSuppliedPicturesNotPlaced()
    {
        File.WriteAllText(Path.Combine(root, "brief.md"), "A sign.\n\n- Logo, in documents/: styled.svg\n"
            + "- Images to include, in documents/: logo_large.png, photo.png\n");
        var tools = new DrawingMcpTools(null, null, null, root);

        var first = await tools.ExecuteScript("""
            const paper = Snap(400, 400);
            Stage.place(paper, 'documents/styled.svg', { x: 0, y: 0, width: 400, height: 100 });
            paper;
            """, 400, 400, outFile: "artifacts/final_a.png");
        Assert.True(first.Success, first.Error);
        var warning = Assert.Single(first.Logs, l => l.Contains("which Stage.place has not drawn"));
        Assert.Contains("documents/logo_large.png, documents/photo.png", warning);
        Assert.DoesNotContain("styled.svg", warning);

        var second = await tools.ExecuteScript("""
            const paper = Snap(400, 400);
            Stage.place(paper, 'documents/logo_large.png', { x: 0, y: 100, width: 400, height: 100 });
            Stage.place(paper, 'documents/photo.png', { x: 0, y: 200, width: 400, height: 200 }, { fit: 'cover' });
            paper;
            """, 400, 400, outFile: "artifacts/final_a.png");
        Assert.True(second.Success, second.Error);
        Assert.DoesNotContain(second.Logs, l => l.Contains("which Stage.place has not drawn"));
    }

    private DrawingExecutionResult Run(string body, int width, int height) =>
        new JsDrawingEngine { ProjectRoot = root, Events = new RunEventLog(root) }.Execute(body, width, height, null, "png", 90);

    private void Png(string name, int width, int height, SKColor left, SKColor? right = null)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(left);
            if (right is { } r) using (var paint = new SKPaint { Color = r }) canvas.DrawRect(width / 2f, 0, width / 2f, height, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(root, "documents", name), data.ToArray());
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;

    /// <summary>A 400 × 400 two-line wordmark in black: a 40-row line and a 60-row line, on clear pixels or on a white box.</summary>
    private void Wordmark(string name, bool opaque)
    {
        using var bitmap = new SKBitmap(400, 400);
        using (var canvas = new SKCanvas(bitmap))
        using (var ink = new SKPaint { Color = new SKColor(0x05, 0x05, 0x05) })
        {
            canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);
            canvas.DrawRect(40, 100, 320, 40, ink);
            canvas.DrawRect(40, 200, 320, 60, ink);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(root, "documents", name), data.ToArray());
    }
    #endregion
}
