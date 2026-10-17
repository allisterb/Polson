namespace Polson.Tests.Animation;

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// The text layer: its layout against Synfig's renderer where it is present, the animators and their selectors, the
/// refusals, and the SVG route.
/// </summary>
public class MotionTextTests(ITestOutputHelper output) : TestsRuntime
{
    #region Oracle
    /// <summary>
    /// Plain text through both renderers: two lines, three alignments, a keyed size, compress and origin — the layout is
    /// Synfig's, so the glyphs should land on the same pixels. Unkerned, because Synfig 1.5.5 never kerns: it does not
    /// size its FreeType face, so the kerning FreeType gives it scales to nothing. Synfig drops an unknown layer silently,
    /// so its frame must also differ from the empty ground.
    /// </summary>
    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0d, 0d)]
    [InlineData(1d, 1d)]
    public void SynfigAndSkiaSetTheSameText(double ox, double oy)
    {
        if (Synfig() is not { } synfig || !Installed("Arial")) return;

        var motion = new MotionToolkit();
        var comp = Ground(motion);
        comp.Text(Opts(("text", "AVATAR WAVE\nType, set."), ("family", "Arial"), ("origin", motion.Nodes.Linear("vector", new[] { 20d, 10d }, new[] { 190d, 115d })),
            ("orient", new[] { ox, oy }), ("color", "#1d3557"), ("kerning", false), ("vcompress", 1.2),
            ("size", motion.Nodes.Linear("real", 12, 36)), ("compress", motion.Nodes.Linear("real", 0.2, 1))));

        Compare(synfig, comp, $"text-{ox}-{oy}", 0.01);
    }

    /// <summary>The same text in a heavier weight, italic, inverted: the parameters Synfig reads as integers and a flag.</summary>
    [Fact]
    public void SynfigAndSkiaSetBoldItalicInverted()
    {
        if (Synfig() is not { } synfig || !Installed("Arial")) return;

        var motion = new MotionToolkit();
        var comp = Ground(motion);
        comp.Text(Opts(("text", "Bold italic"), ("family", "Arial"), ("weight", "bold"), ("style", "italic"), ("size", 40d),
            ("origin", new[] { 200d, 70d }), ("color", "#e63946"), ("kerning", false)));
        comp.Rectangle(Opts(("point1", new[] { 40d, 130d }), ("point2", new[] { 360d, 210d }), ("color", "#1d3557")));
        var g = comp.Group();
        g.Rectangle(Opts(("point1", new[] { 40d, 130d }), ("point2", new[] { 360d, 210d }), ("color", "#f4a261")));
        g.Text(Opts(("text", "Knockout"), ("family", "Arial"), ("weight", 900), ("size", 48d), ("origin", new[] { 200d, 170d }),
            ("color", "#000000"), ("invert", true), ("blend", "alphaOver"), ("kerning", false)));

        Compare(synfig, comp, "text-styles", 0.01);
    }

    /// <summary>Kerning, which Synfig does not do, tightens AV here: the kerned line is narrower.</summary>
    [Fact]
    public void KerningTightensAPair()
    {
        if (!Installed("Arial")) return;
        double Width(bool kerning)
        {
            var comp = new MotionToolkit().Composition(Opts(("width", 300d), ("height", 80d)));
            comp.Text(Opts(("text", "AVAVAV"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 10d, 40d }),
                ("orient", new[] { 0d, 0.5 }), ("kerning", kerning)));
            using var frame = comp.Render(0);
            return Enumerable.Range(0, 300).Last(x => Enumerable.Range(0, 80).Any(y => frame.SkBitmap.GetPixel(x, y).Alpha > 128));
        }

        Assert.True(Width(true) < Width(false) - 5, $"kerned {Width(true)}, unkerned {Width(false)}");
    }

    /// <summary>
    /// Animators written as Synfig can hold them — a group per letter, its motion sampled at the frame rate — rendered by
    /// Synfig against this, at the frames the samples were taken.
    /// </summary>
    [Fact]
    public void SynfigAndSkiaAnimateTheSameLetters()
    {
        if (Synfig() is not { } synfig || !Installed("Arial")) return;

        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 400d), ("height", 240d), ("fps", 4d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.Text(Opts(("text", "Rise and turn"), ("family", "Arial"), ("size", 44d), ("origin", new[] { 200d, 120d }),
            ("color", "#1d3557"), ("kerning", false), ("animators", new object[]
            {
                Opts(("properties", Opts(("position", new[] { 0d, 60d }), ("rotation", 45d), ("opacity", 0d))),
                     // Offset from 100% to −100% over the second: a ramp that sweeps across the line.
                     ("selector", Opts(("shape", "rampUp"), ("offset", motion.Nodes.Linear("real", -200, 100))))),
                Opts(("properties", Opts(("fillColor", "#e63946"), ("scale", 1.4))),
                     ("selector", Opts(("basedOn", "words"), ("start", 50d), ("end", 100d)))),
            })));

        Compare(synfig, comp, "text-animated", 0.015);
    }
    #endregion

    #region Animators
    /// <summary>The classic reveal: opacity 0, the range's start keyed 0 → 100, so letters appear left to right.</summary>
    [Fact]
    public void ARevealShowsLettersLeftToRight()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 80d), ("duration", 1d)));
        comp.Text(Opts(("text", "REVEAL"), ("family", "Arial"), ("size", 48d), ("origin", new[] { 150d, 40d }), ("animators", new object[]
        {
            Opts(("properties", Opts(("opacity", 0d))), ("selector", Opts(("start", motion.Nodes.Linear("real", 100, 0)), ("smoothness", 0d)))),
        })));

        Assert.Equal(0, Ink(comp, 0, 0, 300));                               // all selected: nothing shows
        Assert.True(Ink(comp, 0.5, 0, 110) > 0, "the first letters should show at 0.5 s");
        Assert.Equal(0, Ink(comp, 0.5, 190, 300));                           // the last ones not yet
        Assert.True(Ink(comp, 1, 190, 300) > 0, "everything should show at the end");
    }

    [Fact]
    public void APositionAnimatorMovesSelectedLettersOnly()
    {
        if (!Installed("Arial")) return;
        var comp = new MotionToolkit().Composition(Opts(("width", 300d), ("height", 160d)));
        comp.Text(Opts(("text", "HH HH"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 150d, 40d }), ("kerning", false),
            ("animators", new object[]
            {
                Opts(("properties", Opts(("position", new[] { 0d, 80d }))), ("selector", Opts(("basedOn", "words"), ("start", 50d)))),
            })));

        using var frame = comp.Render(0);
        // The first word stays near y 40; the second drops 80.
        Assert.True(InkRows(frame.SkBitmap, 0, 150).Max() < 70);
        Assert.True(InkRows(frame.SkBitmap, 150, 300).Min() > 90);
    }

    [Fact]
    public void ARandomizedSelectorIsSeededAndRepeatable()
    {
        if (!Installed("Arial")) return;
        string Render(int seed)
        {
            var comp = new MotionToolkit().Composition(Opts(("width", 300d), ("height", 80d)));
            comp.Text(Opts(("text", "SCRAMBLE"), ("family", "Arial"), ("size", 36d), ("origin", new[] { 150d, 40d }), ("animators", new object[]
            {
                Opts(("properties", Opts(("opacity", 0d))), ("selector", Opts(("end", 50d), ("randomize", true), ("seed", seed)))),
            })));
            using var frame = comp.Render(0);
            return Convert.ToBase64String(frame.SkBitmap.Bytes);
        }

        Assert.Equal(Render(3), Render(3));
        Assert.NotEqual(Render(3), Render(4));
    }
    #endregion

    #region Selector maths
    /// <summary>EffectCraft's own cases for the ported maths, which is what the port is held to.</summary>
    [Fact]
    public void TheRangeSelectorMatchesEffectCraftsCases()
    {
        var r = new MotionTextSelectors.Range(0, 0.375);
        Assert.Equal(1, MotionTextSelectors.RangeValue(r, 0, 4), 9);
        Assert.Equal(0.5, MotionTextSelectors.RangeValue(r, 1, 4), 9);
        Assert.Equal(0, MotionTextSelectors.RangeValue(r, 2, 4), 9);
        Assert.Equal(1, MotionTextSelectors.RangeValue(r with { Smoothness = 0 }, 1, 4), 9);
        Assert.Equal(0, MotionTextSelectors.RangeValue(r with { Smoothness = 0, End = 0.3 }, 1, 4), 9);

        var up = Enumerable.Range(0, 5).Select(i => MotionTextSelectors.RangeValue(new(0, 1, Shape: MotionTextSelectors.Shape.RampUp), i, 5)).ToArray();
        Assert.Equal(0.1, up[0], 9);
        Assert.Equal(0.9, up[4], 9);
        Assert.Equal(1, MotionTextSelectors.RangeValue(new(0, 0.2, Shape: MotionTextSelectors.Shape.RampUp), 4, 5), 9);
        Assert.Equal(1, MotionTextSelectors.RangeValue(new(0, 0.2, Offset: 0.6), 3, 5), 9);
        Assert.Equal(-0.5, MotionTextSelectors.RangeValue(new(Amount: -0.5), 2, 5), 9);
    }

    [Fact]
    public void TheModesAndEasesMatchEffectCraftsCases()
    {
        Assert.Equal(1, MotionTextSelectors.Combine(MotionTextSelectors.Mode.Add, 0.5, 0.75));
        Assert.Equal(0.25, MotionTextSelectors.Combine(MotionTextSelectors.Mode.Intersect, 0.5, 0.5));
        Assert.Equal(0.7, MotionTextSelectors.Combine(MotionTextSelectors.Mode.Difference, 0.3, 1), 9);
        Assert.Equal(0, MotionTextSelectors.Combine(MotionTextSelectors.Mode.Subtract, MotionTextSelectors.Initial(MotionTextSelectors.Mode.Subtract), 1));

        Assert.True(MotionTextSelectors.Ease(0.1, 0, 1) < 0.1);
        Assert.True(MotionTextSelectors.Ease(0.9, 1, 0) > 0.9);
        var v = Enumerable.Range(0, 21).Select(i => MotionTextSelectors.Ease(i / 20.0, -1, 1)).ToArray();
        Assert.True(v.Zip(v.Skip(1)).All(p => p.Second >= p.First));

        var order = MotionTextSelectors.RandomOrder(20, 7);
        Assert.Equal(Enumerable.Range(0, 20), order.Order());
        Assert.Equal(order, MotionTextSelectors.RandomOrder(20, 7));
        Assert.NotEqual(order, MotionTextSelectors.RandomOrder(20, 8));

        var w = new MotionTextSelectors.Wiggly(Seed: 3);
        Assert.Equal(MotionTextSelectors.WigglyValue(w, 1.234, 5, 0), MotionTextSelectors.WigglyValue(w, 1.234, 5, 0));
        Assert.True(Math.Abs(MotionTextSelectors.WigglyValue(w, 1, 2, 0) - MotionTextSelectors.WigglyValue(w, 1.001, 2, 0)) < 0.02);
        var c1 = w with { Correlation = 1 };
        Assert.Equal(MotionTextSelectors.WigglyValue(c1, 0.7, 0, 0), MotionTextSelectors.WigglyValue(c1, 0.7, 9, 0));
    }
    #endregion

    #region Refusals
    [Fact]
    public void AMissingFamilyIsRefusedWithWhatToUse()
    {
        var comp = new MotionToolkit().Composition();
        var e = Assert.Throws<ArgumentException>(() => comp.Text(Opts(("text", "x"), ("family", "Polson Nonexistent Face 7Q"))));
        Assert.Contains("not installed", e.Message);
        Assert.Contains("Skia.Font.families()", e.Message);

        if (!Installed("Franklin Gothic")) return;
        e = Assert.Throws<ArgumentException>(() => comp.Text(Opts(("text", "x"), ("family", "Franklin Gothic Heavy"))));
        Assert.Contains("'Franklin Gothic'", e.Message);
        Assert.Contains("weight 900", e.Message);
    }

    [Theory]
    [InlineData("selector", "mystery", "has no 'mystery'")]
    [InlineData("shape", "zigzag", "'zigzag' is not")]
    [InlineData("basedOn", "paragraphs", "'paragraphs' is not")]
    public void ABadSelectorIsRefusedByName(string key, string value, string expected)
    {
        if (!Installed("Arial")) return;
        var selector = key == "selector" ? Opts(("mystery", 1d)) : Opts((key, value));
        var comp = new MotionToolkit().Composition();
        var e = Assert.Throws<ArgumentException>(() => comp.Text(Opts(("text", "x"), ("family", "Arial"), ("animators", new object[]
        {
            Opts(("properties", Opts(("opacity", 0d))), ("selector", selector)),
        }))));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void InvertAndAnimatorsAreRefusedTogether()
    {
        if (!Installed("Arial")) return;
        var comp = new MotionToolkit().Composition();
        var e = Assert.Throws<ArgumentException>(() => comp.Text(Opts(("text", "x"), ("family", "Arial"), ("invert", true),
            ("animators", new object[] { Opts(("properties", Opts(("opacity", 0d)))) }))));
        Assert.Contains("cannot invert and animate", e.Message);
    }
    #endregion

    #region SVG
    /// <summary>Outlines, not text: the SVG needs no font, and draws what this draws, the origin's motion exact.</summary>
    [Fact]
    public void TheSvgRouteWritesOutlinesThatDrawTheSame()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 120d), ("fps", 4d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));
        comp.Text(Opts(("text", "Outlines"), ("family", "Arial"), ("size", 40d), ("color", "#1d3557"),
            ("origin", motion.Nodes.Linear("vector", new[] { 60d, 0d }, new[] { 120d, 60d }))));

        var svg = comp.ToSvg();
        Assert.DoesNotContain("<text", svg);
        Assert.Contains("<path", svg);
        foreach (var t in new[] { 0, 0.4, 1 })
        {
            using var ours = comp.Render(t);
            using var theirs = SKBitmap.Decode(Polson.Drawing.Svg.SvgRenderPipeline.RenderToImage(svg, comp.Width, comp.Height, "png", 100, null, TimeSpan.FromSeconds(t)));
            var differing = Differing(ours.SkBitmap, theirs, 48);
            output.WriteLine($"svg {t}s: {differing:P2} differ by more than 48");
            Assert.True(differing < 0.01, $"{differing:P2} differ at {t}s");
        }
    }

    [Fact]
    public void TheSvgRouteAnimatesLettersAndRefusesWhatItCannotSay()
    {
        if (!Installed("Arial")) return;
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 300d), ("height", 120d), ("fps", 4d), ("duration", 1d)));
        comp.Text(Opts(("text", "Up"), ("family", "Arial"), ("size", 40d), ("origin", new[] { 150d, 60d }), ("animators", new object[]
        {
            Opts(("properties", Opts(("position", new[] { 0d, -30d }))), ("selector", Opts(("offset", motion.Nodes.Linear("real", 100, -100))))),
        })));
        Assert.Contains("animateTransform", comp.ToSvg());

        var keyed = motion.Composition(Opts(("duration", 1d)));
        keyed.Text(Opts(("text", "Grow"), ("family", "Arial"), ("size", motion.Nodes.Linear("real", 10, 20)), ("desc", "title")));
        var e = Assert.Throws<InvalidOperationException>(() => keyed.ToSvg());
        Assert.Contains("'title'", e.Message);
        Assert.Contains("size", e.Message);

        var blurred = motion.Composition(Opts(("duration", 1d)));
        blurred.Text(Opts(("text", "Soft"), ("family", "Arial"), ("animators", new object[] { Opts(("properties", Opts(("blur", 4d)))) })));
        e = Assert.Throws<InvalidOperationException>(() => blurred.ToSvg());
        Assert.Contains("blur", e.Message);
    }
    #endregion

    #region Private
    private static int Ink(MotionComposition comp, double t, int x0, int x1)
    {
        using var frame = comp.Render(t);
        var bmp = frame.SkBitmap;
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = x0; x < x1; x++)
                if (bmp.GetPixel(x, y).Alpha > 64) n++;
        return n;
    }

    private static int[] InkRows(SKBitmap bmp, int x0, int x1) =>
        [.. Enumerable.Range(0, bmp.Height).Where(y => Enumerable.Range(x0, x1 - x0).Any(x => bmp.GetPixel(x, y).Alpha > 128))];

    private static MotionComposition Ground(MotionToolkit motion)
    {
        var comp = motion.Composition(Opts(("width", 400d), ("height", 240d), ("fps", 2d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        return comp;
    }

    private void Compare(string synfig, MotionComposition comp, string name, double bar)
    {
        var theirs = RenderWithSynfig(synfig, comp, name);
        Assert.Equal(comp.FrameCount, theirs.Length);
        var drewAny = 0.0;
        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i / comp.Fps);
            using var bitmap = theirs[i];
            Dump(ours.SkBitmap, bitmap, $"{name}-{i}");

            using var ground = new SKBitmap(bitmap.Width, bitmap.Height);
            using (var c = new SKCanvas(ground)) c.Clear(SKColor.Parse("#f2efe8"));
            var drawn = Differing(ground, bitmap, 24);
            var differing = Differing(ours.SkBitmap, bitmap, 24);
            output.WriteLine($"{name} frame {i}: {differing:P2} differ from synfig by more than 24, "
                + $"{Differing(ours.SkBitmap, bitmap, 96):P2} by more than 96; synfig drew on {drawn:P2}");
            drewAny = Math.Max(drewAny, drawn);
            Assert.True(differing < bar, $"{name} differs from synfig in {differing:P2} at frame {i}");
        }
        // A frame may rightly be empty (every letter faded out); a run in which Synfig drew nothing at all means it dropped the layer.
        Assert.True(drewAny > 0.01, "synfig drew nothing in any frame: did it drop the layer?");
    }

    private static bool Installed(string family)
    {
        using var face = SKFontManager.Default.MatchFamily(family);
        return face is not null && face.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase);
    }

    private SKBitmap[] RenderWithSynfig(string synfig, MotionComposition comp, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "polson-synfig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var sif = comp.SaveSif(Path.Combine(dir, name + ".sif"));
            var start = new ProcessStartInfo(synfig)
            {
                WorkingDirectory = Path.GetDirectoryName(synfig)!,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            foreach (var a in new[] { "-q", sif, "-t", "png", "-o", Path.Combine(dir, name + ".png") }) start.ArgumentList.Add(a);

            using var process = Process.Start(start)!;
            var err = process.StandardError.ReadToEndAsync();
            var outp = process.StandardOutput.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "synfig did not finish in two minutes");
            var log = string.Join("\n", (err.Result + outp.Result).Split('\n')
                .Where(l => !l.Contains("info:") && !l.Contains("Unable to find module") && l.Trim().Length > 0));
            if (log.Length > 0) output.WriteLine(log);
            Assert.True(process.ExitCode == 0, $"synfig exited {process.ExitCode}: {log}");
            Assert.DoesNotContain("error", log, StringComparison.OrdinalIgnoreCase);

            var files = Directory.GetFiles(dir, name + ".*.png").Order(StringComparer.Ordinal).ToArray();
            if (files.Length == 0) files = Directory.GetFiles(dir, name + ".png");
            // Through .NET, not SKBitmap.Decode(path): see MotionBlendTests.
            return [.. files.Select(f => SKBitmap.Decode(File.ReadAllBytes(f)))];
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Dump(SKBitmap ours, SKBitmap theirs, string name)
    {
        if (Environment.GetEnvironmentVariable("POLSON_SYNFIG_DUMP") is not { Length: > 0 } dump) return;
        Directory.CreateDirectory(dump);
        File.WriteAllBytes(Path.Combine(dump, $"ours-{name}.png"), ours.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        File.WriteAllBytes(Path.Combine(dump, $"theirs-{name}.png"), theirs.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }

    private static string? Synfig()
    {
        var path = Environment.GetEnvironmentVariable("POLSON_SYNFIG");
        if (string.IsNullOrEmpty(path))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "reference"))) dir = dir.Parent;
            path = dir is null ? null : Path.Combine(dir.FullName, "reference", "apps", "synfig", "bin", "synfig.exe");
        }

        return path is not null && File.Exists(path) ? path : null;
    }
    #endregion
}
