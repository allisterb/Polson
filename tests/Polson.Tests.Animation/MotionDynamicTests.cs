namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Animation;

using Xunit;

/// <summary>
/// The dynamic node: Synfig's spring-and-torsion tip, integrated from 0 on a fixed grid so any frame gives the same value
/// in any order.
/// </summary>
public class MotionDynamicTests : TestsRuntime
{
    static MotionNodeFactory N => new MotionToolkit().Nodes;

    static (double X, double Y) P(MotionNode node, double t)
    {
        var p = (IDictionary<string, object?>)node.At(t);
        return ((double)p["x"]!, (double)p["y"]!);
    }

    [Fact]
    public void AtRestItStaysAtRest()
    {
        var d = N.Dynamic(new[] { 0d, -60d }, Opts(("origin", new[] { 100d, 200d })));
        foreach (var t in new[] { 0, 0.37, 1, 4.2 })
        {
            var (x, y) = P(d, t);
            Assert.Equal(100d, x, 6);
            Assert.Equal(140d, y, 6);
        }
    }

    /// <summary>
    /// A steady pull along the stalk on a stalk that cannot turn is a damped spring from rest, whose answer is known:
    /// <c>r₀ + (F/k)(1 − e^(−ζωt)(cos ω_d t + ζ/√(1−ζ²) sin ω_d t))</c>.
    /// </summary>
    [Fact]
    public void ASteadyPullIsTheDampedSpringItShouldBe()
    {
        const double k = 30, m = 0.3, c = 0.4, f = 300, r0 = 50;
        var d = N.Dynamic(new[] { 0d, r0 }, Opts(("force", new[] { 0d, f }), ("torsionRigid", true)));
        var w = Math.Sqrt(k / m);
        var zeta = c / (2 * Math.Sqrt(k * m));
        var wd = w * Math.Sqrt(1 - zeta * zeta);
        foreach (var t in new[] { 0.05, 0.2, 0.31, 0.77, 1.5, 3.03 })
        {
            var expected = r0 + f / k * (1 - Math.Exp(-zeta * w * t) * (Math.Cos(wd * t) + zeta / Math.Sqrt(1 - zeta * zeta) * Math.Sin(wd * t)));
            Assert.Equal(expected, P(d, t).Y, 3);
        }
        Assert.Equal(r0 + f / k, P(d, 25).Y, 3);
    }

    /// <summary>Asked in any order, it answers the same: the simulation runs from 0 on its own grid, not from the last request.</summary>
    [Fact]
    public void AnyFrameInAnyOrderGivesTheSameValue()
    {
        MotionDynamic Make() => N.Dynamic(new[] { 0d, -60d }, Opts(("origin", Swing())));
        var times = Enumerable.Range(0, 49).Select(i => i / 24d).ToArray();
        var forward = Make();
        var inOrder = times.Select(t => P(forward, t)).ToArray();

        var shuffled = Make();
        var order = times.Select((t, i) => (t, i)).OrderBy(p => (p.i * 7919) % 49).ToArray();
        var seen = new (double, double)[times.Length];
        foreach (var (t, i) in order) seen[i] = P(shuffled, t);
        for (var i = 0; i < times.Length; i++)
        {
            Assert.Equal(inOrder[i].X, seen[i].Item1, 9);
            Assert.Equal(inOrder[i].Y, seen[i].Item2, 9);
        }
    }

    /// <summary>The origin jumps right; the tip trails it, swings past, and settles where the rest offset puts it.</summary>
    [Fact]
    public void ATipLagsAMovingOriginAndSettles()
    {
        var d = N.Dynamic(new[] { 0d, -60d }, Opts(("origin", Swing())));
        var (midX, _) = P(d, 0.4);
        Assert.True(midX < 200 - 5, $"at 0.4s the tip is at x {midX:0.0}, not behind the origin");
        Assert.True(Enumerable.Range(0, 60).Select(i => P(d, 0.8 + i / 30d).X).Max() > 300.5, "the tip never swings past");
        var (endX, endY) = P(d, 30);
        Assert.Equal(300d, endX, 2);
        Assert.Equal(140d, endY, 2);
    }

    [Fact]
    public void WithoutTheOriginItIsTheOffsetAlone()
    {
        var d = N.Dynamic(new[] { 0d, -60d }, Opts(("origin", new[] { 100d, 200d }), ("originDragsTip", false)));
        var (x, y) = P(d, 1);
        Assert.Equal(0d, x, 6);
        Assert.Equal(-60d, y, 6);
    }

    [Fact]
    public void BadSpringsAreRefusedByName()
    {
        Assert.Contains("'stiffness'", Assert.Throws<ArgumentException>(() => N.Dynamic(new[] { 0d, 10d }, Opts(("stiffness", 3d)))).Message);
        Assert.Throws<ArgumentException>(() => N.Dynamic(new[] { 0d, 10d }, Opts(("springRigid", 1d))));
        Assert.Throws<ArgumentException>(() => N.Dynamic(3d));
        Assert.Throws<ArgumentException>(() => N.Dynamic(new[] { 0d, 10d }).At(MotionDynamic.Longest + 1));
    }

    /// <summary>An origin at x 100 for 0.2 s, then across to 300 by 0.6 s, at y 200.</summary>
    internal static MotionNode Swing() => N.Animated("vector", new object[]
    {
        Opts(("time", 0d), ("value", new[] { 100d, 200d }), ("ease", "halt")),
        Opts(("time", 0.2d), ("value", new[] { 100d, 200d }), ("ease", "halt")),
        Opts(("time", 0.6d), ("value", new[] { 300d, 200d }), ("ease", "halt"))
    });

    private static Dictionary<string, object?> Opts(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
