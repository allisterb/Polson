namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using Polson.Animation;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// <c>comp.number</c>: a text whose string follows a real node, formatted as Synfig's <c>realstring</c> formats it,
/// listed by <c>comp.texts()</c> by the values it holds, and exported as that converter.
/// </summary>
public class MotionNumberTests(ITestOutputHelper output) : TestsRuntime
{
    #region Methods
    /// <summary>Holds 0 for a second, counts to 974 by 2.5 s, holds to the end.</summary>
    private static MotionAnimated Count(MotionToolkit motion) => motion.Nodes.Animated("real", new object[]
    {
        Opts(("time", 0d), ("value", 0d)), Opts(("time", 1d), ("value", 0d)), Opts(("time", 2.5), ("value", 974d)),
    });

    private static MotionComposition Make(MotionToolkit motion, double duration = 4) =>
        motion.Composition(Opts(("width", 400d), ("height", 240d), ("fps", 12d), ("duration", duration)));

    [Fact]
    public void TestTheStringFollowsTheValueAsFormatted()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = Make(motion);
        var money = comp.Number(Opts(("value", Count(motion)), ("family", "Arial"), ("format", Opts(("prefix", "$"), ("suffix", "M"))), ("origin", new[] { 200d, 120d })));
        Assert.Equal("$0M", money.TextAt(0));
        Assert.Equal("$974M", money.TextAt(3));
        Assert.Equal(974, money.ValueAt(3));
        Assert.InRange(money.ValueAt(1.75), 1, 973);
        Assert.Equal($"${Math.Round(money.ValueAt(1.75))}M", money.TextAt(1.75));

        string F(double v, double decimals, bool grouping = false) =>
            comp.Number(Opts(("value", v), ("family", "Arial"), ("format", Opts(("decimals", decimals), ("grouping", grouping))))).TextAt(0);
        Assert.Equal("0.000", F(-0.0004, 3));                            // −0 shows as 0
        Assert.Equal("-12", F(-12, 0));
        Assert.Equal("231.6", F(231.64999999999998, 1));                 // the binary value rounded, not scaled first
        Assert.Equal(("2", "0.12", "1,234,567.2"), (F(2.5, 0), F(0.125, 2), F(1234567.25, 1, true)));
        Assert.Equal("-0.38", F(-0.375, 2));                             // an exact half goes to even, as printf takes it
    }

    /// <summary>The digits drawn are the value's: 974 is wider than 0, and the frames differ.</summary>
    [Fact]
    public void TestTheDigitsDrawnAreTheValues()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = Make(motion);
        comp.Fill(Opts(("color", "#ffffff")));
        var n = comp.Number(Opts(("value", Count(motion)), ("family", "Arial"), ("size", 60d), ("origin", new[] { 200d, 120d })));
        var start = n.BoundsAt(0)!;
        var end = n.BoundsAt(3)!;
        Assert.True((double)end["width"]! > 2.5 * (double)start["width"]!, $"974 is {end["width"]} wide, 0 is {start["width"]}");

        using var a = comp.Render(0);
        using var b = comp.Render(3);
        Assert.True(Ink(b.SkBitmap) > 2 * Ink(a.SkBitmap));
        var settled = n.SettledAt(1.1)!;
        Assert.Equal(2.5, (double)settled["at"]!, 2);                    // still once it stops counting
    }

    /// <summary>The values held half a second or more are the items; the count between them is not listed, but passes.</summary>
    [Fact]
    public void TestTextsListTheValuesHeldAndNotTheCount()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = Make(motion);
        var n = comp.Number(Opts(("value", Count(motion)), ("family", "Arial"), ("format", Opts(("prefix", "$"), ("suffix", "M"))), ("desc", "returned")));
        comp.Number(Opts(("value", 22d), ("family", "Arial"), ("amount", 0d)));

        var items = comp.Texts().Cast<Dictionary<string, object?>>().ToArray();
        Assert.Equal(["$0M", "$974M", "22"], items.Select(i => (string)i["text"]!));
        Assert.All(items, i => Assert.Equal("number", i["kind"]));
        Assert.Equal("returned", items[0]["desc"]);
        Assert.Equal((0d, 1d), ((double)items[0]["from"]!, (double)items[0]["to"]!));
        Assert.Equal((2.5, 4d), ((double)items[1]["from"]!, (double)items[1]["to"]!));
        Assert.Null(items[2]["from"]);                                   // never shown, reported as a text is

        // Counting from the last frame at 0 to the first at 974; the hidden 22 never counts.
        var counting = Assert.Single(comp.Counting());
        Assert.Equal((0d, 974d), (counting.Min, counting.Max));
        Assert.Equal((1d, 2.5), (counting.From, counting.To));
        Assert.True(counting.Covers(380, 1.5, 0) && counting.Covers(974.4, null, 0));
        Assert.False(counting.Covers(999, 1.5, 0) || counting.Covers(380, 3.5, 0.5));
        Assert.Equal("$380M", counting.Format.Format(380));
    }

    /// <summary>A number in a group whose clock is offset counts on the group's time.</summary>
    [Fact]
    public void TestANumberInAGroupCountsOnTheGroupsClock()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = Make(motion, 5);
        var g = comp.Group(Opts(("timeOffset", -1d)));
        g.Number(Opts(("value", Count(motion)), ("family", "Arial")));
        var items = comp.Texts().Cast<Dictionary<string, object?>>().ToArray();
        Assert.Equal(["0", "974"], items.Select(i => (string)i["text"]!));
        Assert.Equal(3.5, (double)items[1]["from"]!, 3);
    }

    [Fact]
    public void TestANumberExportsAsRealstringAndRefusesWhatItCannotSay()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = Make(motion);
        comp.Number(Opts(("value", Count(motion)), ("family", "Arial"), ("format", Opts(("decimals", 1d), ("prefix", "$")))));
        var sif = comp.ToSif();
        Assert.Contains("<realstring type=\"string\">", sif);
        Assert.Contains("<join type=\"string\">", sif);
        Assert.Contains("<integer value=\"1\" />", sif);
        Assert.Throws<InvalidOperationException>(() => comp.ToSvg());

        var grouped = Make(motion);
        grouped.Number(Opts(("value", 1000d), ("family", "Arial"), ("format", Opts(("grouping", true)))));
        Assert.Contains("grouping", Assert.Throws<InvalidOperationException>(() => grouped.ToSif()).Message);

        Assert.Contains("needs a value", Assert.Throws<ArgumentException>(() => comp.Number(Opts(("family", "Arial")))).Message);
        Assert.Throws<ArgumentException>(() => comp.Number(Opts(("value", 1d), ("family", "Arial"), ("format", Opts(("decimals", 7d))))));
        Assert.Throws<ArgumentException>(() => comp.Number(Opts(("value", 1d), ("family", "Arial"), ("text", "1"))));
    }

    /// <summary>
    /// A number counts in its audience's conventions: 130.5 billion lands as crore in Mumbai and 億 in Tokyo, the figure the
    /// same. Synfig's realstring cannot say crore, so the .sif is refused; a place the data does not carry is refused at once.
    /// </summary>
    [Fact]
    public void TestANumberCountsInItsPlacesConventions()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = Make(motion);
        var revenue = motion.Nodes.Animated("real", new object[] { Opts(("time", 0d), ("value", 0d)), Opts(("time", 2d), ("value", 130497000000d)) });
        var mumbai = comp.Number(Opts(("value", revenue), ("family", "Arial"),
            ("format", Opts(("locale", "en-IN"), ("compact", "long"), ("decimals", 1d), ("prefix", "$")))));
        Assert.Equal("$13,049.7 crore", mumbai.TextAt(3));
        Assert.Equal("$0", mumbai.TextAt(0));
        var tokyo = comp.Number(Opts(("value", 130497000000d), ("family", "Arial"),
            ("format", Opts(("locale", "ja-JP"), ("compact", "short"), ("currency", "USD"), ("decimals", 1d)))));
        Assert.Equal("$1305億", tokyo.TextAt(0));
        Assert.Contains("en-IN's conventions", Assert.Throws<InvalidOperationException>(() => comp.ToSif()).Message);

        var unknown = Assert.Throws<ArgumentException>(() => comp.Number(Opts(("value", 1d), ("family", "Arial"), ("format", Opts(("locale", "ru-RU"))))));
        Assert.Contains("carries these locales", unknown.Message);
        var placeless = Assert.Throws<ArgumentException>(() => comp.Number(Opts(("value", 1d), ("family", "Arial"), ("format", Opts(("compact", "long"))))));
        Assert.Contains("give format.locale too", placeless.Message);
    }

    /// <summary>Synfig counts the same digits from the .sif: realstring inside join, on the same frames.</summary>
    [Fact]
    public void SynfigCountsTheSameDigits()
    {
        if (SynfigOracle.Find() is not { } synfig || !Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 400d), ("height", 240d), ("fps", 4d), ("duration", 3d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.Number(Opts(("value", motion.Nodes.Animated("real", new object[] { Opts(("time", 0d), ("value", 3.1)), Opts(("time", 3d), ("value", 917.3)) })),
            ("family", "Arial"), ("size", 56d), ("kerning", false), ("color", "#1d3557"), ("origin", new[] { 200d, 80d }),
            ("format", Opts(("decimals", 1d), ("prefix", "$"), ("suffix", "M")))));
        comp.Number(Opts(("value", motion.Nodes.Linear("real", 40d, 7d)), ("family", "Arial"), ("size", 40d), ("kerning", false),
            ("color", "#e63946"), ("origin", new[] { 200d, 170d })));
        // Exact halves, which printf takes to even.
        comp.Number(Opts(("value", 0.125), ("family", "Arial"), ("size", 28d), ("kerning", false), ("origin", new[] { 80d, 215d }), ("format", Opts(("decimals", 2d)))));
        comp.Number(Opts(("value", 2.5), ("family", "Arial"), ("size", 28d), ("kerning", false), ("origin", new[] { 320d, 215d })));

        var theirs = SynfigOracle.Render(synfig, comp, "number", output);
        Assert.Equal(comp.FrameCount, theirs.Length);
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i / comp.Fps);
            using var bitmap = theirs[i];
            SynfigOracle.Dump(ours.SkBitmap, bitmap, $"number-{i}");
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"frame {i}: {differing:P2} differ by more than 24");
            Assert.True(differing < 0.01, $"frame {i} differs from synfig in {differing:P2}");
            Assert.True(Ink(bitmap, "#f2efe8") > 500, $"synfig drew no digits at frame {i}");
        }
    }

    private static int Ink(SKBitmap bmp, string ground = "#ffffff")
    {
        var g = SKColor.Parse(ground);
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (Math.Abs(bmp.GetPixel(x, y).Red - g.Red) > 60) n++;
        return n;
    }

    private static double Differing(SKBitmap a, SKBitmap b, int tolerance)
    {
        var n = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                var (p, q) = (a.GetPixel(x, y), b.GetPixel(x, y));
                if (Math.Abs(p.Red - q.Red) > tolerance || Math.Abs(p.Green - q.Green) > tolerance || Math.Abs(p.Blue - q.Blue) > tolerance) n++;
            }
        return (double)n / (a.Width * a.Height);
    }

    private static bool Installed(string family)
    {
        using var face = SKFontManager.Default.MatchFamily(family);
        return face is not null && face.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase);
    }
    #endregion
}
