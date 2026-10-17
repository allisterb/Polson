namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Animation;

using Xunit;

using static MotionCompositionTests;

/// <summary>
/// The WCAG 2.3.1 flash check, on strobes either side of three a second, a strobe too small or too faint or too bright
/// to count, a red flash with no change in luminance, and a single fade; and the WCAG contrast ratio.
/// </summary>
public class MotionFlashesTests : TestsRuntime
{
    #region Methods
    [Theory]
    [InlineData(2, false, 6d)]     // a change every 2 frames at 24 fps: 12 transitions a second, 6 flashes
    [InlineData(3, false, 4d)]     // every 3: 8 transitions, 4 flashes — more than three
    [InlineData(4, true, 3d)]      // every 4: 6 transitions, 3 flashes — at the limit, which passes
    public void AFullFrameStrobeFailsPastThreeFlashesASecond(int every, bool passed, double flashes)
    {
        var result = Strobe("#000000", "#ffffff", every).Flashes();

        Assert.Equal(passed, (bool)result["passed"]!);
        Assert.Equal(flashes, (double)result["generalFlashes"]!);
        Assert.Equal(0d, (double)result["redFlashes"]!);
    }

    /// <summary>A strobing square a tenth of a 10° field wide is below the area threshold, however fast.</summary>
    [Fact]
    public void ASmallStrobePasses()
    {
        var result = Strobe("#000000", "#ffffff", 2, square: 0.03).Flashes();

        Assert.True((bool)result["passed"]!);
        Assert.Empty((List<Dictionary<string, object?>>)result["transitions"]!);
    }

    [Theory]
    [InlineData("#767676", "#7f7f7f")]   // a change of 0.03 in luminance: under the 10% step
    [InlineData("#ffffff", "#eeeeee")]   // a change of 0.14, but the darker state is 0.85: above 0.8, not a flash
    public void ChangesBelowTheThresholdsAreNotFlashes(string a, string b)
    {
        var result = Strobe(a, b, 2).Flashes();

        Assert.True((bool)result["passed"]!);
        Assert.Equal(0d, (double)result["generalFlashes"]!);
    }

    /// <summary>Saturated red against a grey of the same luminance: no general flash at all, and a red flash six times a second.</summary>
    [Fact]
    public void ARedStrobeIsARedFlash()
    {
        var result = Strobe("#ff0000", "#7f7f7f", 2).Flashes();

        Assert.False((bool)result["passed"]!);
        Assert.Equal(0d, (double)result["generalFlashes"]!);
        Assert.Equal(6d, (double)result["redFlashes"]!);
        Assert.Contains((List<Dictionary<string, object?>>)result["transitions"]!, t => (string)t["direction"]! == "toRed");
    }

    /// <summary>A fade from black to white over a second is one transition, as a slow swing counts as surely as a cut.</summary>
    [Fact]
    public void AFadeIsOneTransition()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 320d), ("height", 180d), ("fps", 24d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#000000")));
        comp.Fill(Opts(("color", "#ffffff"), ("amount", motion.Nodes.Linear("real", 1d, 0d))));

        var result = comp.Flashes();
        var transitions = (List<Dictionary<string, object?>>)result["transitions"]!;

        Assert.True((bool)result["passed"]!);
        Assert.Equal("brighter", (string)Assert.Single(transitions)["direction"]!);
        Assert.Null(result["worstAt"]);
        Assert.Equal(49, result["frames"]);
    }

    [Fact]
    public void WorstAtSaysWhereTheStrobeIs()
    {
        var result = Strobe("#000000", "#ffffff", 2, start: 1.0).Flashes();

        Assert.False((bool)result["passed"]!);
        Assert.InRange((double)result["worstAt"]!, 1.0, 1.1);
    }

    [Fact]
    public void AWidthOutOfRangeIsRefused()
    {
        var e = Assert.Throws<ArgumentException>(() => Strobe("#000000", "#ffffff", 2).Flashes(Opts(("width", 8d))));
        Assert.Contains("from 32 to 640", e.Message);
    }

    [Theory]
    [InlineData("#ffffff", "#000000", 21d)]
    [InlineData("#767676", "#ffffff", 4.54)]   // the lightest grey that passes 4.5 on white
    [InlineData("#777777", "#ffffff", 4.48)]   // and the next, which does not
    [InlineData("#ffffff80", "#000000", 5.32)] // half white over black is a mid grey
    public void ContrastIsWcagsRatio(string a, string b, double ratio) =>
        Assert.Equal(ratio, new MotionToolkit().Contrast(a, b));

    [Fact]
    public void ContrastNeedsAnOpaqueGround()
    {
        var e = Assert.Throws<ArgumentException>(() => new MotionToolkit().Contrast("#ffffff80", "#00000080"));
        Assert.Contains("opaque", e.Message);
    }

    /// <summary>
    /// A 2 s film switching between two fills every <paramref name="every"/> frames from <paramref name="start"/>, or
    /// a square of them <paramref name="square"/> of the frame's width across on white.
    /// </summary>
    private static MotionComposition Strobe(string a, string b, int every, double square = 0, double start = 0)
    {
        var comp = new MotionToolkit().Composition(Opts(("width", 320d), ("height", 180d), ("fps", 24d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#ffffff")));
        var keys = Enumerable.Range(0, 48 / every + 1)
            .Select(i => (object)new object[] { start + i * every / 24d, i % 2 == 0 ? "a" : "b" })
            .Where(k => (double)((object[])k)[0] <= 2).ToList();
        if (start > 0) keys.Insert(0, new object[] { 0d, "a" });
        var flash = comp.Switch(Opts(("active", keys.ToArray())));
        foreach (var (name, color) in new[] { ("a", a), ("b", b) })
        {
            if (square <= 0) flash.Fill(Opts(("color", color), ("desc", name)));
            else flash.Rectangle(Opts(("point1", new[] { 100d, 60d }), ("point2", new[] { 100 + 320 * square, 60 + 320 * square }), ("color", color), ("desc", name)));
        }
        return comp;
    }
    #endregion
}
