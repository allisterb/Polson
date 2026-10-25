namespace Polson.Tests.MCPServer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polson.Drawing.Skia;
using Polson.ExtendedMind.Taste;
using Polson.MCPServer;
using SkiaSharp;
using Xunit;

/// <summary>
/// <c>Stage.matrix</c>: Lupton's brand matrix (Manual 29 §2d), the audience's field and the concepts on two crossed
/// polarities. The field's pictures are built here with their measurements; the concepts are canvases, measured as a
/// Qloo picture is.
/// </summary>
public class BrandMatrixTests : TestsRuntime
{
    /// <summary>A dark, muted field — the Berlin films' shape — with one light poster, and three concepts.</summary>
    private static TasteImage[] Field() =>
    [
        Picture("Suspiria", 0.30, 0.09, "#8a1f1f", 0.62),
        Picture("Aladdin", 0.25, 0.05, "#4a2a10", 0.81),
        Picture("Uncut Gems", 0.20, 0.01, null, 0.55),
        Picture("All About Eve", 0.28, 0.02, null, 0.40),
        Picture("Portrait of a Lady on Fire", 0.22, 0.04, "#5a3a20", 0.48),
        Picture("Mamma Mia! Here We Go Again", 0.80, 0.06, "#f2d9b0", 0.90),
    ];

    [Fact]
    public void TestTheFieldAndTheConceptsAreCountedByQuadrantAndTheEmptyOnesNamed()
    {
        var canvas = new SkiaCanvas(1000, 760);
        var result = Matrix(canvas, new()
        {
            ["x"] = Axis("lightness"),
            ["y"] = Axis("chroma"),
            ["field"] = Field(),
            ["concepts"] = new object?[] { Concept("A", SKColors.OrangeRed), Concept("B", new SKColor(40, 30, 30)), Concept("C", SKColors.LightGray) },
            ["chosen"] = "A",
        });

        var concepts = ((object?[])result["concepts"]!).Cast<Dictionary<string, object?>>().ToDictionary(c => (string)c["label"]!);
        Assert.Equal("vivid & light", concepts["A"]["quadrant"]);            // nothing in the field is light and vivid
        Assert.Equal(0, concepts["A"]["fieldInQuadrant"]);
        Assert.Equal(true, concepts["A"]["chosen"]);
        Assert.Equal("muted & dark", concepts["B"]["quadrant"]);              // B belongs: it sits with the crowd
        Assert.True((int)concepts["B"]["fieldInQuadrant"]! >= 4);
        Assert.Contains("vivid & light", (string[])result["empty"]!);
        Assert.Contains("Concept A (chosen) sits vivid & light, with 0 of the field", (string)result["summary"]!);
        Assert.Empty((string[])result["unplaced"]!);

        if (Environment.GetEnvironmentVariable("POLSON_MATRIX_PNG") is { Length: > 0 } keep)
            File.WriteAllBytes(keep, canvas.ToImageBytes("png", 100));
    }

    [Fact]
    public void TestAJudgedAxisPlacesWhatItNamesAndSaysWhatItCouldNot()
    {
        var result = Matrix(new SkiaCanvas(800, 600), new()
        {
            ["x"] = Axis("lightness"),
            ["y"] = new Dictionary<string, object?>
            {
                ["judged"] = new Dictionary<string, object?> { ["Suspiria"] = 0.9, ["Aladdin"] = 0.1, ["A"] = 0.7 },
                ["low"] = "formal", ["high"] = "playful",
            },
            ["field"] = Field(),
            ["concepts"] = new object?[] { Concept("A", SKColors.OrangeRed) },
        });

        Assert.Equal("judged by the studio", ((Dictionary<string, object?>)result["y"]!)["kind"]);
        Assert.Equal(2, ((object?[])result["field"]!).Length);
        Assert.Contains("6 Mamma Mia! Here We Go Again", (string[])result["unplaced"]!);
    }

    [Fact]
    public void TestAConceptCannotBePlacedOnWhatOnlyTheFieldHas()
    {
        var refused = Assert.Throws<ArgumentException>(() => Matrix(null, new()
        {
            ["x"] = Axis("lightness"), ["y"] = Axis("popularity"), ["field"] = Field(),
            ["concepts"] = new object?[] { Concept("A", SKColors.OrangeRed) },
        }));
        var fieldOnly = Matrix(null, new() { ["x"] = Axis("lightness"), ["y"] = Axis("popularity"), ["field"] = Field() });

        Assert.Contains("has no popularity", refused.Message);
        Assert.Equal("Qloo · popularity", ((Dictionary<string, object?>)fieldOnly["y"]!)["kind"]);
    }

    [Theory]
    [InlineData("brightness", "not a measure")]
    public void TestAnUnknownMeasureIsRefusedByName(string measure, string words) =>
        Assert.Contains(words, Assert.Throws<ArgumentException>(() =>
            Matrix(null, new() { ["x"] = Axis(measure), ["y"] = Axis("chroma"), ["field"] = Field() })).Message);

    private static Dictionary<string, object?> Matrix(SkiaCanvas? canvas, Dictionary<string, object?> spec) =>
        new StageApi(null, null, null).Matrix(canvas?.GetContext("2d"), spec);

    private static Dictionary<string, object?> Axis(string measure) => new() { ["measure"] = measure };

    private static Dictionary<string, object?> Concept(string label, SKColor colour)
    {
        var canvas = new SkiaCanvas(120, 180);
        canvas.GetContext("2d").FillStyle = $"#{colour.Red:X2}{colour.Green:X2}{colour.Blue:X2}";
        canvas.GetContext("2d").FillRect(0, 0, 120, 180);
        return new() { ["label"] = label, ["image"] = canvas };
    }

    private static TasteImage Picture(string name, double lightness, double chroma, string? lead, double popularity) => new()
    {
        Success = true, Name = name, Lightness = lightness, Chroma = chroma, Neutral = lead is null ? 0.95 : 0.4,
        Popularity = popularity,
        Lead = lead is null ? null : new() { ["hue"] = 40.0, ["chroma"] = chroma, ["concentration"] = 0.8, ["color"] = lead },
    };
}
