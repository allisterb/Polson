namespace Polson.Tests.MCPServer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polson.Drawing.Skia;
using Polson.MCPServer;
using SkiaSharp;
using Xunit;

/// <summary>
/// The motion deliverables of Manual 25 §13 (Shaw, <i>Design for Motion</i>): the treatment's shape (<c>Stage.shape</c>),
/// the DOs &amp; DON'Ts list checked (<c>Stage.borders</c>), and the pitch book kept apart from the process book.
/// </summary>
public class MotionDeliverablesTests : TestsRuntime, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-shaw-" + Guid.NewGuid().ToString("N"));

    public MotionDeliverablesTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        Frame("night.png", new SKColor(11, 29, 38));                    // dark teal
        Frame("dawn.png", new SKColor(242, 217, 176));                  // warm cream
        Frame("black.png", SKColors.Black);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void TestAStoryThatRisesHasAPeakAndOneThatDoesNotIsFlat()
    {
        var canvas = new SkiaCanvas(1200, 520);
        var shaped = Stage().Shape(canvas.GetContext("2d"), Spec(("Night", 1, 0.2), ("The claim", 7.5, 0.9), ("Dawn", 11.5, 0.4)));
        var flat = Stage().Shape(null, Spec(("a", 0, 0.5), ("b", 6, 0.55)));

        Assert.Equal("The claim", ((Dictionary<string, object?>)shaped["peak"]!)["name"]);
        Assert.Equal(false, shaped["flat"]);
        Assert.Equal(true, flat["flat"]);
        Assert.Contains("barely changes", Assert.Single((string[])flat["warnings"]!));
        Assert.Contains("at least two beats", Assert.Throws<ArgumentException>(() => Stage().Shape(null, Spec(("a", 0, 0.5)))).Message);
    }

    /// <summary>A line with a clause is measured on every image and fails on the worst; a line without one is judged.</summary>
    [Fact]
    public void TestTheBordersMeasureWhatTheyCanAndListWhatTheyCannot()
    {
        File.WriteAllText(Path.Combine(root, "dos.md"), """
            # DOs & DON'Ts
            - DO keep the ground dark `lightness < 0.35`
            - DON'T use pure black anywhere `colour #000000 < 1%`
            - **DON’T** let green in `hue green < 2%`
            - DON'T set more than two typefaces `fonts <= 2`
            - DO let the claim settle before the cut.
            Some prose that is not a line.
            """);

        var night = Stage().Borders("dos.md", new object[] { "artifacts/night.png" }, Fonts("Georgia"));
        var mixed = Stage().Borders("dos.md", new object[] { "artifacts/night.png", "artifacts/dawn.png", "artifacts/black.png" }, Fonts("Georgia", "Inter", "Bodoni"));

        Assert.True((bool)night["passed"]!, (string)night["summary"]!);
        Assert.Equal(["DO: let the claim settle before the cut."], (string[])night["judged"]!);
        var lines = ((object?[])mixed["lines"]!).Cast<Dictionary<string, object?>>().ToDictionary(l => (string)l["clause"]!);
        Assert.Equal((false, "artifacts/dawn.png"), ((bool)lines["lightness < 0.35"]["passed"]!, (string?)lines["lightness < 0.35"]["where"]));
        Assert.Equal((false, "artifacts/black.png"), ((bool)lines["colour #000000 < 1%"]["passed"]!, (string?)lines["colour #000000 < 1%"]["where"]));
        Assert.Equal(true, lines["hue green < 2%"]["passed"]);
        Assert.Equal((false, 3d), ((bool)lines["fonts <= 2"]["passed"]!, (double)lines["fonts <= 2"]["value"]!));
        Assert.Contains("3 broken", (string)mixed["summary"]!);
    }

    /// <summary>
    /// Manual 25 §14a: the resolve is balanced and the opening need not be, so a clause can hold on the last frame only;
    /// a gradient between palette colours is on the palette, and a patch of another colour is its share off it.
    /// </summary>
    [Fact]
    public void TestSymmetryHoldsOnTheResolveAndOffPaletteCountsOnlyStrayColour()
    {
        Draw("open.png", (c, p) => { c.DrawRect(20, 20, 220, 50, p); c.DrawCircle(520, 280, 50, p); });
        Draw("resolve.png", (c, p) => { c.DrawRect(170, 150, 300, 40, p); c.DrawRect(240, 205, 160, 14, p); });
        Draw("graded.png", (c, p) =>
        {
            p.Shader = SKShader.CreateLinearGradient(new(0, 0), new(640, 0), [new SKColor(0x14, 0x20, 0x2b), new SKColor(0xf2, 0xb1, 0x34)], SKShaderTileMode.Clamp);
            c.DrawRect(0, 0, 640, 360, p);
            p.Shader = null; p.Color = new SKColor(0x2a, 0x9d, 0x8f);
            c.DrawRect(0, 0, 64, 360, p);                                                      // a tenth of the frame in teal
        });
        File.WriteAllText(Path.Combine(root, "dos.md"), """
            - DO resolve on a balanced end card `symmetry on last > 0.9`
            - DON'T open on a balanced frame `symmetry on first < 0.8`
            - DON'T stray off the palette `offPalette #14202b #f2b134 < 5%`
            """);

        var film = Lines(Stage().Borders("dos.md", new object[] { "artifacts/open.png", "artifacts/resolve.png" }));
        var graded = Lines(Stage().Borders("dos.md", "artifacts/graded.png"));

        Assert.Equal((true, "artifacts/resolve.png"), ((bool)film["symmetry on last > 0.9"]["passed"]!, (string?)film["symmetry on last > 0.9"]["where"]));
        Assert.Equal((true, "artifacts/open.png"), ((bool)film["symmetry on first < 0.8"]["passed"]!, (string?)film["symmetry on first < 0.8"]["where"]));
        Assert.Equal(true, film["offPalette #14202b #f2b134 < 5%"]["passed"]);
        Assert.InRange((double)graded["offPalette #14202b #f2b134 < 5%"]["value"]!, 0.09, 0.11);
        File.WriteAllText(Path.Combine(root, "dos.md"), "- DON'T stray `offPalette < 3%`\n");
        Assert.Contains("palette in the clause", Assert.Throws<ArgumentException>(() => Stage().Borders("dos.md", "artifacts/graded.png")).Message);
    }

    [Fact]
    public void TestAnUnknownMeasureAndAMissingFontListAreRefusedInWords()
    {
        File.WriteAllText(Path.Combine(root, "dos.md"), "- DON'T glow `brightness < 0.5`\n");
        Assert.Contains("not a measure", Assert.Throws<ArgumentException>(() => Stage().Borders("dos.md", "artifacts/night.png")).Message);

        File.WriteAllText(Path.Combine(root, "dos.md"), "- DON'T set more than two `fonts <= 2`\n");
        Assert.Contains("pass { fonts", Assert.Throws<ArgumentException>(() => Stage().Borders("dos.md", "artifacts/night.png")).Message);
    }

    /// <summary>A pitch book sells the chosen concept; the workshop's sheets, a missing board and a missing treatment are said.</summary>
    [Fact]
    public void TestAPitchBookIsWarnedAboutWorkshopSheetsAndWhatItLacks()
    {
        File.Copy(Path.Combine(root, "artifacts", "night.png"), Path.Combine(root, "artifacts", "02_dump_a.png"));
        File.Copy(Path.Combine(root, "artifacts", "night.png"), Path.Combine(root, "artifacts", "03_board.png"));
        File.WriteAllText(Path.Combine(root, "treatment.md"), "# The story\nNight, then the name, then dawn.");

        var good = Book("pitch", ("The story", null, "treatment.md"), ("Design board", "artifacts/03_board.png", null));
        var bad = Book("pitch", ("Dump", "artifacts/02_dump_a.png", null));
        var process = Book("process", ("Dump", "artifacts/02_dump_a.png", null));

        Assert.Empty((List<object?>)good["warnings"]!);
        Assert.Equal("pitch", good["kind"]);
        var warned = string.Join(" | ", (List<object?>)bad["warnings"]!);
        Assert.Contains("02_dump_a", warned);
        Assert.Contains("design board or its style frames", warned);
        Assert.Contains("treatment.md", warned);
        Assert.Empty((List<object?>)process["warnings"]!);
        Assert.Contains("'process' or 'pitch'", Assert.Throws<ArgumentException>(() => Book("deck", ("Dump", "artifacts/02_dump_a.png", null))).Message);
    }

    /// <summary>
    /// A book's pictures go in losslessly, and <c>proof</c> writes every page as a PNG to read back. nightshift2's books
    /// held their stills as quality-0 JPEGs (a struct initialiser left EncodingQuality at 0), and the run could not open
    /// either PDF to see it.
    /// </summary>
    [Fact]
    public void TestABookKeepsItsPicturesLosslessAndWritesProofs()
    {
        using (var fine = new SKBitmap(400, 225))
        {
            for (var y = 0; y < 225; y++)
                for (var x = 0; x < 400; x++) fine.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);
            File.WriteAllBytes(Path.Combine(root, "artifacts", "03_board.png"), fine.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        }
        File.WriteAllText(Path.Combine(root, "treatment.md"), "# The story\nNight, then the name, then dawn.");

        var book = Stage().Book("pitch.pdf", new Dictionary<string, object?>
        {
            ["title"] = "Nightshift", ["kind"] = "pitch", ["proof"] = "artifacts/pitch_proof",
            ["pages"] = new object?[]
            {
                new Dictionary<string, object?> { ["title"] = "The story", ["file"] = "treatment.md" },
                new Dictionary<string, object?> { ["title"] = "Design board", ["image"] = "artifacts/03_board.png" },
            },
        });

        var pdf = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(root, "pitch.pdf")));
        Assert.DoesNotContain("/DCTDecode", pdf, StringComparison.Ordinal);
        var proofs = ((object?[])book["proofs"]!).Cast<string>().ToArray();
        Assert.Equal((int)book["pages"]!, proofs.Length);
        Assert.Equal("artifacts/pitch_proof_01.png", proofs[0]);
        using var page = SKBitmap.Decode(File.ReadAllBytes(Path.Combine(root, proofs[^1])));
        Assert.Equal((1440, 810), (page.Width, page.Height));
    }

    private StageApi Stage() => new(null, null, null, projectRoot: root);

    private Dictionary<string, object?> Book(string kind, params (string Title, string? Image, string? File)[] pages) =>
        Stage().Book($"{kind}.pdf", new Dictionary<string, object?>
        {
            ["title"] = "Nightshift", ["kind"] = kind,
            ["pages"] = pages.Select(p => (object?)new Dictionary<string, object?> { ["title"] = p.Title, ["image"] = p.Image, ["file"] = p.File }).ToArray(),
        });

    private static Dictionary<string, object?> Spec(params (string Name, double Time, double Intensity)[] beats) => new()
    {
        ["beats"] = beats.Select(b => (object?)new Dictionary<string, object?> { ["name"] = b.Name, ["time"] = b.Time, ["intensity"] = b.Intensity }).ToArray(),
    };

    private static Dictionary<string, object?> Fonts(params string[] fonts) => new() { ["fonts"] = fonts };

    private static Dictionary<string, Dictionary<string, object?>> Lines(Dictionary<string, object?> borders) =>
        ((object?[])borders["lines"]!).Cast<Dictionary<string, object?>>().ToDictionary(l => (string)l["clause"]!);

    /// <summary>A 640 × 360 frame on the dark ground, drawn in amber.</summary>
    private void Draw(string name, Action<SKCanvas, SKPaint> draw)
    {
        using var bitmap = new SKBitmap(640, 360);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = new SKColor(0xf2, 0xb1, 0x34), IsAntialias = true })
        {
            canvas.Clear(new SKColor(0x14, 0x20, 0x2b));
            draw(canvas, paint);
        }
        File.WriteAllBytes(Path.Combine(root, "artifacts", name), bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }

    private void Frame(string name, SKColor colour)
    {
        using var bitmap = new SKBitmap(96, 54);
        bitmap.Erase(colour);
        File.WriteAllBytes(Path.Combine(root, "artifacts", name), bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }
}
