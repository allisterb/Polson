namespace Polson.Tests.Drawing;

using System;
using System.Linq;
using Polson.Drawing.Skia;
using SkiaSharp;
using Xunit;

/// <summary>
/// Mock-ups (Lupton, <i>Graphic Design Thinking</i>, pp. 136–138): the design drawn into its setting through a true
/// perspective, at its real size where size matters, and always labelled as a mock-up.
/// </summary>
public class MockupTests : TestsRuntime
{
    #region Methods
    /// <summary>A design that is one flat colour, so where it landed can be found by colour.</summary>
    private static SKBitmap Design(int w, int h, SKColor colour)
    {
        var b = new SKBitmap(w, h);
        b.Erase(colour);
        return b;
    }

    private static readonly SKColor Marker = new(0xff, 0x00, 0xcc);

    private static bool Near(SKColor a, SKColor b, int tolerance = 12) =>
        Math.Abs(a.Red - b.Red) <= tolerance && Math.Abs(a.Green - b.Green) <= tolerance && Math.Abs(a.Blue - b.Blue) <= tolerance;

    [Fact]
    public void TestTheSquareMapsOntoItsCorners()
    {
        SKPoint[] quad = [new(100, 50), new(400, 90), new(380, 420), new(120, 380)];

        var map = Mockup.SquareToQuad(quad)!.Value;

        Assert.Equal(quad[0].X, map.MapPoint(0, 0).X, 2);
        Assert.Equal(quad[1].Y, map.MapPoint(1, 0).Y, 2);
        Assert.Equal(quad[2].X, map.MapPoint(1, 1).X, 2);
        Assert.Equal(quad[3].Y, map.MapPoint(0, 1).Y, 2);
        Assert.Null(Mockup.SquareToQuad([new(0, 0), new(10, 0), new(0, 10), new(10, 10)]));     // folds through itself
        Assert.Null(Mockup.SquareToQuad([new(0, 0), new(5, 0), new(10, 0), new(0, 10)]));       // three in a line
    }

    /// <summary>Every setting draws the design where it says it did, inside the picture, and labels the picture.</summary>
    [Theory]
    [InlineData("wall", 420, 594)]
    [InlineData("frame", 420, 594)]
    [InlineData("shelter", 700, 990)]
    [InlineData("billboard", 1280, 720)]
    [InlineData("phone", 700, 990)]
    [InlineData("screen", 1280, 720)]
    [InlineData("card", 850, 550)]
    [InlineData("box", 700, 990)]
    public void TestEverySettingDrawsTheDesignWhereItSays(string setting, int w, int h)
    {
        using var design = Design(w, h, Marker);

        var result = Mockup.Render(design, new Mockup.Spec(setting));
        using var image = result.Image;

        Assert.Equal((1600, 1000), (image.Width, image.Height));
        Assert.Equal(4, result.Corners.Length);
        Assert.All(result.Corners, p => Assert.True(p.X is >= 0 and <= 1600 && p.Y is >= 0 and <= 1000, $"{setting}: corner {p} is outside the picture"));
        // The middle of the quad is the design's colour.
        var mid = new SKPoint(result.Corners.Average(p => p.X), result.Corners.Average(p => p.Y));
        Assert.True(Near(image.GetPixel((int)mid.X, (int)mid.Y), Marker, 40), $"{setting}: {image.GetPixel((int)mid.X, (int)mid.Y)} at the design's middle");
        // And the design is a real part of the picture, not a speck.
        var share = Enumerable.Range(0, 1600 * 1000 / 97).Select(i => i * 97).Count(i => Near(image.GetPixel(i % 1600, i / 1600), Marker, 40)) / (1600 * 1000 / 97.0);
        Assert.True(share > 0.02, $"{setting}: the design is {share:P1} of the picture");
        Assert.Empty(result.Warnings);
    }

    /// <summary>A poster on a wall is drawn at its real size against a figure 1.7 m tall: A2 is about a third of the figure's height.</summary>
    [Fact]
    public void TestAWallShowsThePosterAtItsRealSize()
    {
        using var design = Design(420, 594, Marker);

        var result = Mockup.Render(design, new Mockup.Spec("wall", 420, 594));
        using var image = result.Image;

        var tall = result.Corners[3].Y - result.Corners[0].Y;
        Assert.Equal(594 * result.PixelsPerMm!.Value, tall, 1.0);
        Assert.Equal((420.0, 594.0), (Math.Round(result.WidthMm), Math.Round(result.HeightMm)));
    }

    [Fact]
    public void TestASizeOfAnotherShapeIsWarnedAndTheDesignKeepsItsOwn()
    {
        using var design = Design(400, 400, Marker);

        var result = Mockup.Render(design, new Mockup.Spec("wall", 420, 594));
        result.Image.Dispose();

        Assert.Contains(result.Warnings, w => w.Contains("not the design's shape"));
        Assert.Equal(594.0, result.WidthMm, 1);
    }

    /// <summary>
    /// A plate built around a panel draws its blank panel near, not at, the quad it was given: found, the design goes in
    /// the panel. (Measured 2026-10-25: the panel came back inset and about two thirds the quad's width.)
    /// </summary>
    [Fact]
    public void TestABlankPanelIsFoundNearItsHintAndUsed()
    {
        using var plate = new SKBitmap(1600, 1000);
        using (var c = new SKCanvas(plate))
        {
            c.Clear(new SKColor(0x22, 0x26, 0x33));
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var panel = new SKPath();
            panel.AddPoly([new(635, 287), new(873, 323), new(873, 713), new(633, 731)]);
            c.DrawPath(panel, white);
        }
        using var design = Design(560, 1000, Marker);
        SKPoint[] hint = [new(560, 170), new(930, 240), new(930, 790), new(560, 850)];

        var result = Mockup.Render(design, new Mockup.Spec("onto", Onto: plate, Corners: hint, Find: true));
        result.Image.Dispose();

        SKPoint[] expected = [new(635, 287), new(873, 323), new(873, 713), new(633, 731)];
        for (var i = 0; i < 4; i++)
            Assert.True(SKPoint.Distance(result.Corners[i], expected[i]) < 6, $"corner {i}: {result.Corners[i]} for {expected[i]}");
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TestNoPanelFoundSaysSoAndKeepsTheCornersGiven()
    {
        using var plate = new SKBitmap(1600, 1000);
        plate.Erase(new SKColor(0x22, 0x26, 0x33));
        using var design = Design(560, 1000, Marker);
        SKPoint[] hint = [new(560, 170), new(930, 240), new(930, 790), new(560, 850)];

        var result = Mockup.Render(design, new Mockup.Spec("onto", Onto: plate, Corners: hint, Find: true));
        result.Image.Dispose();

        Assert.Equal(hint, result.Corners);
        Assert.Contains(result.Warnings, w => w.Contains("no blank panel was found"));
    }

    [Fact]
    public void TestWhatCannotBeAMockupSaysWhy()
    {
        using var design = Design(100, 100, Marker);

        Assert.Contains("'wall'", Assert.Throws<ArgumentException>(() => Mockup.Render(design, new Mockup.Spec("cathedral"))).Message);
        Assert.Contains("always says it is a mock-up", Assert.Throws<ArgumentException>(() => Mockup.Render(design, new Mockup.Spec("wall", Label: " "))).Message);
        Assert.Contains("needs onto", Assert.Throws<ArgumentException>(() => Mockup.Render(design, new Mockup.Spec("onto"))).Message);
    }
    #endregion
}
