namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;

/// <summary>The switch layer: a group showing one of its layers at a time, by name, as Synfig's switch does.</summary>
public class MotionSwitchTests : TestsRuntime
{
    /// <summary>Three discs side by side, named a, b and c, red, green and blue, in a switch keyed by <paramref name="active"/>.</summary>
    internal static (MotionComposition Comp, MotionGroup Switch) Discs(object active, object? extra = null)
    {
        var comp = new MotionToolkit().Composition(Opts(("width", 300d), ("height", 100d), ("fps", 12d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#ffffff")));
        var options = Opts(("active", active));
        if (extra is IDictionary<string, object?> more) foreach (var (k, v) in more) options[k] = v;
        var s = comp.Switch(options);
        s.Circle(Opts(("origin", new[] { 50d, 50d }), ("radius", 20d), ("color", "#ff0000"), ("desc", "a")));
        s.Circle(Opts(("origin", new[] { 150d, 50d }), ("radius", 20d), ("color", "#00ff00"), ("desc", "b")));
        s.Circle(Opts(("origin", new[] { 250d, 50d }), ("radius", 20d), ("color", "#0000ff"), ("desc", "c")));
        return (comp, s);
    }

    /// <summary>Which of the three discs a rendered frame shows, by colour: "a", "b", "c", or "" for none, joined if several.</summary>
    internal static string Showing(SKBitmap bitmap) => string.Concat(new[] { (50, "a", 0), (150, "b", 1), (250, "c", 2) }
        .Where(d => bitmap.GetPixel(d.Item1, 50) is var c && (d.Item3 switch { 0 => c.Red, 1 => c.Green, _ => c.Blue }) > 200
            && (d.Item3 switch { 0 => c.Green, 1 => c.Red, _ => c.Red }) < 60)
        .Select(d => d.Item2));

    static string At(MotionComposition comp, double t)
    {
        using var frame = comp.Render(t);
        return Showing(frame.SkBitmap);
    }

    [Fact]
    public void ItShowsTheLayerItNames()
    {
        Assert.Equal("b", At(Discs("b").Comp, 0.5));
        Assert.Equal("", At(Discs("").Comp, 0.5));
    }

    /// <summary>Keys hold: the name changes at its key, and before the first key the first name shows.</summary>
    [Fact]
    public void KeysHoldUntilTheNext()
    {
        var (comp, _) = Discs(new object[] { new object[] { 0.25d, "a" }, Opts(("time", 1d), ("value", "c")), new object[] { 1.5d, "b" } });
        Assert.Equal("a", At(comp, 0));
        Assert.Equal("a", At(comp, 0.99));
        Assert.Equal("c", At(comp, 1));
        Assert.Equal("c", At(comp, 1.4));
        Assert.Equal("b", At(comp, 1.9));
    }

    [Fact]
    public void TheTopmostOfTwoNamesShows()
    {
        var (comp, s) = Discs("a");
        s.Circle(Opts(("origin", new[] { 250d, 50d }), ("radius", 20d), ("color", "#0000ff"), ("desc", "a")));
        Assert.Equal("c", At(comp, 0));
    }

    /// <summary>The keys are read at the switch's own time, not its children's: a dilated clock inside does not move them.</summary>
    [Fact]
    public void KeysAreInTheSwitchsOwnTime()
    {
        var (comp, _) = Discs(new object[] { new object[] { 0d, "a" }, new object[] { 1d, "b" } }, Opts(("timeDilation", 3d), ("timeOffset", 5d)));
        Assert.Equal("a", At(comp, 0.9));
        Assert.Equal("b", At(comp, 1.1));
    }

    [Fact]
    public void ANameNoLayerHasIsRefusedListingThoseItHas()
    {
        var (comp, _) = Discs("mouthA");
        var e = Assert.Throws<ArgumentException>(() => At(comp, 0));
        Assert.Contains("'mouthA'", e.Message);
        Assert.Contains("'a', 'b', 'c'", e.Message);
        Assert.Throws<ArgumentException>(() => comp.ToSif());
    }

    [Fact]
    public void BadKeysAreRefused()
    {
        var comp = new MotionToolkit().Composition();
        Assert.Throws<ArgumentException>(() => comp.Switch(Opts()));
        Assert.Throws<ArgumentException>(() => comp.Switch(Opts(("active", 2d))));
        Assert.Throws<ArgumentException>(() => comp.Switch(Opts(("active", new MotionToolkit().Nodes.Linear("real", 1, 0)))));
        Assert.Throws<ArgumentException>(() => comp.Switch(Opts(("active", new object[] { new object[] { 0d, 3d } }))));
        Assert.Throws<ArgumentException>(() => comp.Switch(Opts(("active", new object[] { new object[] { 0d, "a" }, new object[] { 0d, "b" } }))));
        Assert.Contains("'name'", Assert.Throws<ArgumentException>(() => comp.Switch(Opts(("active", new object[] { Opts(("time", 0d), ("name", "a")) })))).Message);
    }

    [Fact]
    public void ItIsWrittenAsSynfigsSwitchAndAsSvgThatShowsOneAtATime()
    {
        var (comp, _) = Discs(new object[] { new object[] { 0d, "a" }, new object[] { 1d, "b" } });
        var sif = comp.ToSif();
        Assert.Contains("<layer type=\"switch\"", sif);
        Assert.Contains("<animated type=\"string\">", sif);
        Assert.Contains("<string>b</string>", sif);

        // a and b come and go; c never shows, so it is hidden outright rather than animated.
        var svg = comp.ToSvg();
        Assert.Equal(2, svg.Split("attributeName=\"display\"").Length - 1);
        Assert.Contains("display=\"none\"", svg);
    }

    private static Dictionary<string, object?> Opts(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
