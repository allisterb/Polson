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

    private void Frame(string name, SKColor colour)
    {
        using var bitmap = new SKBitmap(96, 54);
        bitmap.Erase(colour);
        File.WriteAllBytes(Path.Combine(root, "artifacts", name), bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }
}
