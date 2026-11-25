namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Animation;

using Xunit;

using static MotionCompositionTests;

/// <summary>
/// <c>comp.blank()</c>: where a film shows nothing. A Manimator film of the Fourier transform was an empty screen for 9.8
/// of its 44 seconds, and an empty frame sets no text, so nothing else in the record would say so.
/// </summary>
public class MotionBlankTests : TestsRuntime
{
    #region Methods
    /// <summary>An equation, 2.5 s of nothing but the ground, another equation: the gap is found, and fails a 1 s limit.</summary>
    [Fact]
    public void AGapBetweenScenesIsFoundAndAFilmWithoutOnePasses()
    {
        var motion = new MotionToolkit();
        var comp = Film(motion, gap: true);
        var result = comp.Blank();
        Assert.False((bool)result["passed"]!);
        var stretch = (Dictionary<string, object?>)Assert.Single((object?[])result["stretches"]!)!;
        Assert.InRange((double)stretch["from"]!, 2.0, 2.4);
        Assert.InRange((double)stretch["to"]!, 4.4, 4.8);
        Assert.InRange((double)result["blankSeconds"]!, 2.0, 2.8);

        Assert.True((bool)comp.Blank(Opts(("longest", 3d)))["passed"]!);
        Assert.True((bool)Film(motion, gap: false).Blank()["passed"]!);
        Assert.Empty((object?[])Film(motion, gap: false).Blank()["stretches"]!);
    }

    /// <summary>A dark film reads as a light one does: its ground is its own most common colour, not the background.</summary>
    [Fact]
    public void AFillIsGroundNotContent()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 1280d), ("height", 720d), ("fps", 12d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#0f172a")));
        var result = comp.Blank();
        Assert.False((bool)result["passed"]!);
        Assert.InRange((double)result["blankSeconds"]!, 1.9, 2.0);

        Assert.Throws<ArgumentException>(() => comp.Blank(Opts(("width", 20d))));
    }

    /// <summary>
    /// A cut that opens on a few empty frames fails when the cut is named, though the sparse samples miss it. polsonpromo1
    /// opened every cut on 2–8 empty frames and passed.
    /// </summary>
    [Fact]
    public void ACutOpeningOnEmptyFramesIsCaught()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 1280d), ("height", 720d), ("fps", 24d), ("duration", 4d)));
        comp.Fill(Opts(("color", "#0f172a")));
        // Shown to 2 s, then nothing for 5 frames after the cut at 2 s, then shown again.
        var amount = motion.Nodes.Animated("real", new object[]
        {
            Opts(("time", 0d), ("value", 1d)), Opts(("time", 1.99d), ("value", 1d)), Opts(("time", 2d), ("value", 0d)),
            Opts(("time", 2.2d), ("value", 0d)), Opts(("time", 2.21d), ("value", 1d)), Opts(("time", 4d), ("value", 1d)),
        });
        comp.Group(Opts(("amount", amount))).Equation(Opts(("latex", @"x^2"), ("origin", new[] { 640d, 360d }),
            ("size", 96d), ("color", "#e2e8f0")));

        Assert.True((bool)comp.Blank()["passed"]!);                                     // between the samples
        var named = comp.Blank(Opts(("cuts", new object[] { 0d, 2d })));
        Assert.False((bool)named["passed"]!);
        var cuts = ((object?[])named["cuts"]!).Cast<Dictionary<string, object?>>().ToArray();
        Assert.Equal(0, cuts[0]["emptyFrames"]);
        Assert.Equal(5, cuts[1]["emptyFrames"]);
        Assert.True((bool)comp.Blank(Opts(("cuts", new object[] { 2d }), ("atCut", 5d)))["passed"]!);
    }

    private static MotionComposition Film(MotionToolkit motion, bool gap)
    {
        var comp = motion.Composition(Opts(("width", 1280d), ("height", 720d), ("fps", 12d), ("duration", 6d)));
        comp.Fill(Opts(("color", "#0f172a")));
        // Shown 0 to 2 s and 4.5 to 6 s with a gap, throughout without one.
        var amount = gap
            ? motion.Nodes.Animated("real", new object[]
              {
                  Opts(("time", 0d), ("value", 1d)), Opts(("time", 2d), ("value", 1d)), Opts(("time", 2.05d), ("value", 0d)),
                  Opts(("time", 4.45d), ("value", 0d)), Opts(("time", 4.5d), ("value", 1d)), Opts(("time", 6d), ("value", 1d)),
              })
            : (object)1d;
        comp.Group(Opts(("amount", amount))).Equation(Opts(("latex", @"\int_0^2 x^2\,dx = \frac{8}{3}"), ("origin", new[] { 640d, 360d }),
            ("size", 48d), ("color", "#e2e8f0")));
        return comp;
    }
    #endregion
}
