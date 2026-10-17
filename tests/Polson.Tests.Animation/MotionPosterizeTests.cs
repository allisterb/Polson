namespace Polson.Tests.Animation;

using System;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// Posterize time (<c>comp.posterizeTime</c>, Synfig's stroboscope layer): stepped time below it and not above, a hold at
/// fps 0, two composing, the group boundary, the text handle, and Synfig's renderer on every frame of a second.
/// </summary>
public class MotionPosterizeTests(ITestOutputHelper output) : TestsRuntime
{
    #region Methods
    [Fact]
    public void LayersBelowStepAndLayersAboveDoNot()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        Mover(comp, motion, 40, "#000000");                     // below: steps at 4 a second
        comp.PosterizeTime(Opts(("fps", 4d)));
        Mover(comp, motion, 120, "#e63946");                    // above: smooth

        using var a = comp.Render(0.30);
        Assert.InRange(X(a.SkBitmap, SKColors.Black, 40), 44, 46);              // as at 0.25: 20 + 100 × 0.25
        Assert.InRange(X(a.SkBitmap, SKColor.Parse("#e63946"), 120), 49, 51);  // as at 0.30
    }

    [Fact]
    public void NoFpsHoldsTheStartAndTwoCompose()
    {
        var motion = new MotionToolkit();
        var held = Scene(motion);
        Mover(held, motion, 40, "#000000");
        held.PosterizeTime(Opts(("fps", 0d)));
        using (var frame = held.Render(0.7))
            Assert.InRange(X(frame.SkBitmap, SKColors.Black, 40), 19, 21);

        var nested = Scene(motion);
        Mover(nested, motion, 40, "#000000");
        nested.PosterizeTime(Opts(("fps", 6d)));
        nested.PosterizeTime(Opts(("fps", 2d)));                // the outer steps first: 0.7 → 0.5, then 0.5 → 0.5
        using var n = nested.Render(0.7);
        Assert.InRange(X(n.SkBitmap, SKColors.Black, 40), 69, 71);
    }

    [Fact]
    public void ItStopsAtItsGroup()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        Mover(comp, motion, 40, "#000000");
        var g = comp.Group();
        g.PosterizeTime(Opts(("fps", 4d)));
        using var frame = comp.Render(0.3);
        Assert.InRange(X(frame.SkBitmap, SKColors.Black, 40), 49, 51);
    }

    [Fact]
    public void TextReportsWhereItIsDrawn()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        var title = comp.Text(Opts(("text", "T"), ("family", "Arial"), ("size", 30d), ("color", "#000000"),
            ("origin", motion.Nodes.Linear("vector", new[] { 100d, 0d }, new[] { 20d, 80d }))));
        comp.PosterizeTime(Opts(("fps", 4d)));

        Assert.Equal((double)title.BoundsAt(0.25)!["x"]!, (double)title.BoundsAt(0.3)!["x"]!, 6);
        Assert.Equal((double)title.BoundsAt(0.25)!["x"]!, (double)title.BoundsAt(0.3, Opts(("frame", true)))!["x"]!, 6);
    }

    [Fact]
    public void ToSifWritesAStroboscopeAndSvgRefuses()
    {
        var motion = new MotionToolkit();
        var comp = Scene(motion);
        Mover(comp, motion, 40, "#000000");
        comp.PosterizeTime(Opts(("fps", 12d)));
        Assert.Contains("type=\"stroboscope\"", comp.ToSif());
        Assert.Contains("step time", Assert.Throws<InvalidOperationException>(() => comp.ToSvg()).Message);
        Assert.Contains("takes fps", Assert.Throws<ArgumentException>(() => comp.PosterizeTime(Opts())).Message);
    }

    /// <summary>Synfig steps the same on every frame of a second at 24 fps, frames lying exactly on a step included.</summary>
    [Fact]
    public void SynfigStepsTheSame()
    {
        if (SynfigOracle.Find() is not { } synfig) return;

        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 160d), ("height", 60d), ("fps", 24d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        comp.Circle(Opts(("origin", motion.Nodes.Linear("vector", new[] { 120d, 0d }, new[] { 20d, 30d })), ("radius", 10d), ("color", "#000000")));
        comp.PosterizeTime(Opts(("fps", 6d)));

        var theirs = SynfigOracle.Render(synfig, comp, "posterize", output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        var worst = 0d;
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i / comp.Fps);
            using var bitmap = theirs[i];
            worst = Math.Max(worst, Differing(ours.SkBitmap, bitmap, 24));
        }
        output.WriteLine($"25 frames: worst {worst:P2} differ by more than 24");
        Assert.True(worst < 0.01);
    }

    private static MotionComposition Scene(MotionToolkit motion)
    {
        var comp = motion.Composition(Opts(("width", 200d), ("height", 160d), ("fps", 24d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        return comp;
    }

    /// <summary>A disc of radius 8 at y, moving 100 units a second from x = 20.</summary>
    private static void Mover(MotionLayerList stack, MotionToolkit motion, double y, string color) =>
        stack.Circle(Opts(("origin", motion.Nodes.Linear("vector", new[] { 100d, 0d }, new[] { 20d, y })), ("radius", 8d), ("color", color)));

    /// <summary>The centre of the run of <paramref name="color"/> along row <paramref name="y"/>.</summary>
    private static double X(SKBitmap b, SKColor color, int y)
    {
        var xs = Enumerable.Range(0, b.Width).Where(x => Near(b.GetPixel(x, y), color)).ToArray();
        return xs.Length == 0 ? -1 : (xs[0] + xs[^1] + 1) / 2.0;
    }

    private static bool Installed(string family)
    {
        using var face = SKFontManager.Default.MatchFamily(family);
        return face is not null && face.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase);
    }
    #endregion
}
