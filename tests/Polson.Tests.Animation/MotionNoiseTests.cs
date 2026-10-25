namespace Polson.Tests.Animation;

using System;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// Noise (<c>comp.noise</c>): reproducible by seed, still or moving by speed, inside its gradient, refused where it cannot
/// go, and drawn the same as Synfig's own noise layer for every interpolation.
/// </summary>
public class MotionNoiseTests(ITestOutputHelper output) : TestsRuntime
{
    #region Methods
    /// <summary>A seed reproduces a pattern and another seed does not; speed 0 holds still, and grain at speed = fps moves every frame.</summary>
    [Fact]
    public void ASeedReproducesAndSpeedMoves()
    {
        static SKBitmap Frame(double seed, double speed, double time)
        {
            var comp = new MotionToolkit().Composition(Opts(("width", 64d), ("height", 32d), ("fps", 24d), ("duration", 1d)));
            comp.Noise(Opts(("seed", seed), ("size", 2d), ("detail", 1d), ("smooth", "nearest"), ("speed", speed)));
            using var frame = comp.Render(time);
            return frame.SkBitmap.Copy();
        }
        static bool Same(SKBitmap a, SKBitmap b) => Enumerable.Range(0, a.Height).All(y => Enumerable.Range(0, a.Width).All(x => a.GetPixel(x, y) == b.GetPixel(x, y)));

        using var a = Frame(7, 0, 0);
        using var again = Frame(7, 0, 0.5);
        using var other = Frame(8, 0, 0);
        using var moving0 = Frame(7, 24, 0);
        using var moving1 = Frame(7, 24, 1 / 24.0);
        Assert.True(Same(a, again), "speed 0 should hold still");
        Assert.False(Same(a, other), "another seed should give another pattern");
        Assert.False(Same(moving0, moving1), "grain at speed = fps should change every frame");
    }

    /// <summary>Every pixel is a colour of the gradient: between its two stops, here a dark and a light grey.</summary>
    [Fact]
    public void EveryPixelIsAColourOfTheGradient()
    {
        var comp = new MotionToolkit().Composition(Opts(("width", 96d), ("height", 48d)));
        comp.Noise(Opts(("stops", new object[] { "#202020", "#c0c0c0" }), ("size", 24d), ("seed", 3d)));
        using var frame = comp.Render(0);
        var greys = Enumerable.Range(0, 48).SelectMany(y => Enumerable.Range(0, 96).Select(x => frame.SkBitmap.GetPixel(x, y))).ToArray();
        Assert.All(greys, c => Assert.InRange((int)c.Red, 0x20, 0xc0));
        Assert.True(greys.Select(c => c.Red).Distinct().Count() > 20, "cosine noise should spread over many greys");
    }

    [Fact]
    public void BadOptionsAreRefusedAndSvgCannotHoldIt()
    {
        var comp = new MotionToolkit().Composition();
        Assert.Contains("'nearest', 'linear'".Replace("'", ""), Assert.Throws<ArgumentException>(() => comp.Noise(Opts(("smooth", "perlin")))).Message.Replace("'", ""));
        Assert.Contains("1 to 10", Assert.Throws<ArgumentException>(() => comp.Noise(Opts(("detail", 0d)))).Message);
        comp.Noise();
        Assert.Contains("cannot be written as animated SVG", Assert.Throws<InvalidOperationException>(() => comp.ToSvg()).Message);
    }

    /// <summary>Synfig draws each interpolation the same, still and moving, turbulent and with alpha, from the .sif's native noise layer.</summary>
    [Theory]
    [InlineData("nearest", 0d, false, false)]
    [InlineData("linear", 0d, false, false)]
    [InlineData("cosine", 0d, false, false)]
    [InlineData("spline", 0d, false, false)]
    [InlineData("cubic", 0d, false, false)]
    [InlineData("nearest", 2d, false, false)]
    [InlineData("linear", 0.6, false, false)]
    [InlineData("cosine", 0.6, true, false)]
    [InlineData("spline", 0.6, false, false)]
    [InlineData("cosine", 0d, false, true)]
    public void SynfigDrawsTheSameNoise(string smooth, double speed, bool turbulent, bool alpha)
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var comp = new MotionToolkit().Composition(Opts(("width", 120d), ("height", 80d), ("fps", 1d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#ffffff")));
        comp.Noise(Opts(("stops", new object[] { "#102a43", "#f0b429" }), ("seed", 11d), ("size", 40d), ("detail", 3d),
            ("smooth", smooth), ("speed", speed), ("turbulent", turbulent), ("alpha", alpha)));
        var name = $"noise-{smooth}-{speed}-{turbulent}-{alpha}";
        var theirs = SynfigOracle.Render(synfig, comp, name, output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"{name}-{i}");
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"{name} frame {i}: {differing:P2} differ by more than 24, {Differing(ours.SkBitmap, bitmap, 8):P2} by more than 8");
            Assert.True(differing < 0.02, $"{differing:P2} differ from synfig at frame {i}");
        }
    }
    #endregion
}
