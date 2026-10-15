namespace Polson.Tests.Drawing;

using System;
using System.Collections.Generic;
using Polson.Drawing.Skia;
using SkiaSharp;
using Xunit;

/// <summary>
/// <c>bitmap.colorfulness()</c>: Hasler and Süsstrunk's metric, checked against values worked by hand from
/// <c>M = √(σ²rg + σ²yb) + 0.3·√(μ²rg + μ²yb)</c> with <c>rg = R − G</c> and <c>yb = ½(R + G) − B</c>.
/// </summary>
public class ColorfulnessTests : TestsRuntime
{
    #region Methods
    /// <summary>A bitmap whose left half is one colour and right half another.</summary>
    private static SkiaBitmapWrapper Halves(SKColor left, SKColor right, int width = 40, int height = 20)
    {
        var bitmap = new SkiaBitmapWrapper(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.Bitmap.SetPixel(x, y, x < width / 2 ? left : right);
        return bitmap;
    }

    [Fact]
    public void TestGreyIsNotColourful()
    {
        using var grey = Halves(new SKColor(128, 128, 128), new SKColor(40, 40, 40));

        Assert.Equal(0d, grey.Colorfulness(), 6);
    }

    /// <summary>One flat colour has no spread, so only the mean term counts: 0.3·√(255² + 127.5²) for pure red.</summary>
    [Fact]
    public void TestAFlatColourCountsOnlyItsMean()
    {
        using var red = Halves(SKColors.Red, SKColors.Red);

        Assert.Equal(0.3 * Math.Sqrt(255 * 255 + 127.5 * 127.5), red.Colorfulness(), 6);
    }

    /// <summary>
    /// Red beside blue: rg is 255 and 0, yb is 127.5 and −255. Means 127.5 and −63.75; each channel's deviation
    /// is half its range, 127.5 and 191.25.
    /// </summary>
    [Fact]
    public void TestTwoColoursCountTheirSpreadAndTheirMean()
    {
        using var redBlue = Halves(SKColors.Red, SKColors.Blue);
        var expected = Math.Sqrt(127.5 * 127.5 + 191.25 * 191.25) + 0.3 * Math.Sqrt(127.5 * 127.5 + 63.75 * 63.75);

        Assert.Equal(expected, redBlue.Colorfulness(), 6);
    }

    [Fact]
    public void TestASaturatedVariantMeasuresAboveAMutedOne()
    {
        using var saturated = Halves(new SKColor(220, 60, 30), new SKColor(30, 120, 210));
        using var muted = Halves(new SKColor(170, 120, 105), new SKColor(110, 130, 150));

        Assert.True(saturated.Colorfulness() > muted.Colorfulness() * 2,
            $"saturated {saturated.Colorfulness():F1} against muted {muted.Colorfulness():F1}");
    }

    /// <summary>Transparency is not a colour: a half-empty red layer measures as red, not as red diluted.</summary>
    [Fact]
    public void TestTransparentPixelsAreLeftOut()
    {
        using var half = Halves(SKColors.Red, SKColors.Transparent);
        using var red = Halves(SKColors.Red, SKColors.Red);

        Assert.Equal(red.Colorfulness(), half.Colorfulness(), 6);
    }

    [Fact]
    public void TestARegionMeasuresOnlyItself()
    {
        using var redGrey = Halves(SKColors.Red, SKColors.Gray);

        var left = redGrey.Colorfulness(new Dictionary<string, object> { ["x"] = 0, ["y"] = 0, ["width"] = 20, ["height"] = 20 });
        var right = redGrey.Colorfulness(new Dictionary<string, object> { ["x"] = 20, ["width"] = 20 });

        Assert.Equal(0.3 * Math.Sqrt(255 * 255 + 127.5 * 127.5), left, 6);
        Assert.Equal(0d, right, 6);
    }

    /// <summary>Nothing visible is not greyscale, so it is refused rather than reported as 0.</summary>
    [Fact]
    public void TestNothingVisibleIsRefusedNotZero()
    {
        using var empty = new SkiaBitmapWrapper(10, 10);

        var ex = Assert.Throws<ArgumentException>(() => empty.Colorfulness());
        Assert.Contains("no visible pixels", ex.Message);
    }
    #endregion
}
