namespace Polson.Tests.Drawing;

using System;
using System.Collections.Generic;
using System.Linq;
using Polson.Drawing.Skia;
using SkiaSharp;
using Xunit;

/// <summary>
/// Alternative grids (Lupton, <i>Graphic Design Thinking</i>, pp. 120–125): lines found in a picture, tiled from a
/// tessellation, or given — and whether a piece was organised by them.
/// </summary>
public class LineGridTests : TestsRuntime
{
    #region Methods
    private static readonly LayoutToolkit Layout = new();

    /// <summary>White, with thick black lines from point to point.</summary>
    private static SkiaCanvas Sketch(int w, int h, params (float X1, float Y1, float X2, float Y2)[] lines)
    {
        var canvas = new SkiaCanvas(w, h);
        var ctx = canvas.GetContext("2d");
        ctx.FillStyle = "#ffffff";
        ctx.FillRect(0, 0, w, h);
        ctx.StrokeStyle = "#000000";
        ctx.LineWidth = 5;
        foreach (var (x1, y1, x2, y2) in lines)
        {
            ctx.BeginPath();
            ctx.MoveTo(x1, y1);
            ctx.LineTo(x2, y2);
            ctx.Stroke();
        }
        return canvas;
    }

    private static float Fold(float angle) => ((angle % 180) + 180) % 180;

    private static float AngleGap(float a, float b)
    {
        var d = Math.Abs(Fold(a) - Fold(b));
        return Math.Min(d, 180 - d);
    }

    private static Dictionary<string, object> Rect(float x, float y, float w, float h) => Layout.Rect(x, y, w, h);

    [Fact]
    public void TestAPicturesLinesAreFoundAtTheirAnglesAndPlaces()
    {
        // A horizontal at y = 100 and a line rising at 30° through (200, 150).
        var dx = 150 * MathF.Cos(MathF.PI / 6);
        var dy = 150 * MathF.Sin(MathF.PI / 6);
        using var sketch = Sketch(400, 300, (20, 100, 380, 100), (200 - dx, 150 + dy, 200 + dx, 150 - dy));

        var grid = Layout.LinesFrom(sketch, new Dictionary<string, object?> { ["count"] = 4 });

        var lines = grid.Lines;
        Assert.True(lines.Length >= 2, grid.Summary);
        var flat = lines.Single(l => AngleGap(Convert.ToSingle(l["angle"]), 0) < 2);
        var rising = lines.Single(l => AngleGap(Convert.ToSingle(l["angle"]), -30) < 2);
        // The horizontal sits at y = 100 across the frame, and was found along x 20–380.
        Assert.InRange(Convert.ToSingle(flat["y1"]), 97, 103);
        var support = (Dictionary<string, object>)flat["support"];
        Assert.InRange(Math.Min(Convert.ToSingle(support["x1"]), Convert.ToSingle(support["x2"])), 10, 60);
        Assert.InRange(Math.Max(Convert.ToSingle(support["x1"]), Convert.ToSingle(support["x2"])), 340, 390);
        Assert.InRange(grid.Nearest(new Dictionary<string, object> { ["x"] = 200f, ["y"] = 150f })["distance"] is float d ? d : 99f, 0, 3);
        Assert.Equal("picture", grid.Source);
        Assert.Equal(1f, lines.Max(l => Convert.ToSingle(l["strength"])));
        _ = rising;
    }

    /// <summary>A picture covers its frame as a photograph is cropped onto a page: evenly scaled, centred.</summary>
    [Fact]
    public void TestAPicturesLinesAreLaidOverItsFrameByCover()
    {
        using var sketch = Sketch(200, 100, (10, 50, 190, 50));

        var grid = Layout.LinesFrom(sketch, new Dictionary<string, object?> { ["into"] = Rect(0, 0, 400, 400), ["count"] = 1 });

        // Cover scales by 4 (400 / 100), so y = 50 lands at 200; the line spans the whole frame.
        var line = Assert.Single(grid.Lines);
        Assert.InRange(Convert.ToSingle(line["y1"]), 196, 204);
        Assert.Equal(400f, Convert.ToSingle(line["length"]), 0.5f);
    }

    [Theory]
    [InlineData("triangle", new[] { 0f, 60f, 120f })]
    [InlineData("diamond", new[] { 45f, 135f })]
    [InlineData("hex", new[] { 30f, 90f, 150f })]
    [InlineData("rhombille", new[] { 30f, 90f, 150f })]
    public void TestATessellationsLinesFallInItsFamilies(string type, float[] families)
    {
        var grid = Layout.Tessellation(Rect(0, 0, 600, 400), new Dictionary<string, object?> { ["type"] = type, ["size"] = 50f });

        Assert.NotEmpty(grid.Lines);
        Assert.All(grid.Lines, l => Assert.Contains(families, f => AngleGap(Convert.ToSingle(l["angle"]), f) < 0.5f));
        Assert.All(grid.Lines, l =>
        {
            foreach (var k in new[] { "x1", "x2" }) Assert.InRange(Convert.ToSingle(l[k]), -0.01f, 600.01f);
            foreach (var k in new[] { "y1", "y2" }) Assert.InRange(Convert.ToSingle(l[k]), -0.01f, 400.01f);
        });
        Assert.Equal($"tessellation:{type}", grid.Source);
    }

    [Theory]
    [InlineData("triangle", 3)]
    [InlineData("hex", 6)]
    [InlineData("rhombille", 4)]
    public void TestATessellationsTilesHaveTheirCorners(string type, int corners)
    {
        var grid = Layout.Tessellation(Rect(0, 0, 600, 400), new Dictionary<string, object?> { ["type"] = type, ["size"] = 60f });

        Assert.NotEmpty(grid.Cells);
        Assert.All(grid.Cells, c => Assert.Equal(corners, ((Dictionary<string, object>[])c).Length));
    }

    /// <summary>The check behind "the grid organised it": baselines along lines, a box on an edge or a crossing, a point on a line.</summary>
    [Fact]
    public void TestFitSaysWhichElementsSitOnTheGrid()
    {
        var grid = Layout.Lines(Rect(0, 0, 1000, 1000), new object[]
        {
            new Dictionary<string, object> { ["x1"] = 0f, ["y1"] = 300f, ["x2"] = 1000f, ["y2"] = 300f },
            new Dictionary<string, object> { ["x1"] = 0f, ["y1"] = 0f, ["x2"] = 1000f, ["y2"] = 1000f },
        });

        var fit = grid.Fit(new object[]
        {
            new Dictionary<string, object> { ["x1"] = 100f, ["y1"] = 102f, ["x2"] = 400f, ["y2"] = 398f },   // along the diagonal, 0.3% off
            new Dictionary<string, object> { ["x"] = 600f, ["y"] = 300f, ["width"] = 200f, ["height"] = 80f }, // top edge on y = 300
            new Dictionary<string, object> { ["x"] = 250f, ["y"] = 253f },                                      // on the diagonal
            new Dictionary<string, object> { ["x1"] = 100f, ["y1"] = 500f, ["x2"] = 400f, ["y2"] = 500f },   // parallel, 200 off
            new Dictionary<string, object> { ["x"] = 50f, ["y"] = 600f, ["width"] = 100f, ["height"] = 100f }, // nowhere near
        });

        Assert.Equal(3, fit["on"]);
        Assert.Equal(5, fit["total"]);
        Assert.Equal(0.6f, (float)fit["share"], 3);
        Assert.Equal(new[] { true, true, true, false, false }, ((object[])fit["items"]).Cast<Dictionary<string, object?>>().Select(i => (bool)i["on"]!));
        Assert.Contains("Off it: 3, 4", (string)fit["summary"]);
    }

    [Fact]
    public void TestSnapAlongAndPathDataUseTheLines()
    {
        var grid = Layout.Lines(Rect(0, 0, 100, 100), new object[]
        {
            new object[] { new[] { 0f, 50f }, new[] { 100f, 50f } },
            new object[] { new[] { 50f, 0f }, new[] { 50f, 100f } },
        });

        var onLine = grid.Snap(new Dictionary<string, object> { ["x"] = 20f, ["y"] = 47f });
        var onCrossing = grid.Snap(new Dictionary<string, object> { ["x"] = 44f, ["y"] = 57f }, new Dictionary<string, object?> { ["to"] = "intersection" });
        var along = grid.Along(1, 0.25f);

        Assert.Equal((20f, 50f), ((float)onLine["x"], (float)onLine["y"]));
        Assert.Equal((50f, 50f), ((float)onCrossing["x"], (float)onCrossing["y"]));
        Assert.Equal((50f, 25f, 90f), ((float)along["x"], (float)along["y"], (float)along["angle"]));
        Assert.Equal("M0 50L100 50M50 0L50 100", grid.PathData());
        Assert.Single(grid.Intersections);
    }

    [Fact]
    public void TestWhatCannotBeAGridSaysWhy()
    {
        using var blank = Sketch(200, 200);
        var empty = Layout.LinesFrom(blank);

        Assert.Empty(empty.Lines);
        Assert.Contains("no lines", Assert.Throws<InvalidOperationException>(() => empty.Fit(new object[] { Rect(0, 0, 10, 10) })).Message);
        Assert.Contains("not a filename", Assert.Throws<ArgumentException>(() => Layout.LinesFrom("sketch.png")).Message);
        Assert.Contains("'triangle', 'diamond', 'hex' or 'rhombille'",
            Assert.Throws<ArgumentException>(() => Layout.Tessellation(Rect(0, 0, 100, 100), new Dictionary<string, object?> { ["type"] = "penrose" })).Message);
        Assert.Contains("two different ends", Assert.Throws<ArgumentException>(() => Layout.Lines(Rect(0, 0, 10, 10),
            new object[] { new Dictionary<string, object> { ["x1"] = 1f, ["y1"] = 1f, ["x2"] = 1f, ["y2"] = 1f } })).Message);
    }
    #endregion
}
