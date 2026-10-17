namespace Polson.Tests.Animation;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

using static MotionCompositionTests;

/// <summary>
/// The blur layer: each ported type against Synfig's renderer where it is present, the frame's edge, the group
/// boundary, the refusals, and the SVG route.
/// </summary>
public class MotionBlurTests(ITestOutputHelper output) : TestsRuntime
{
    #region Oracle
    /// <summary>
    /// Each type, its size keyed across three frames, over hard shapes — one of them running off the frame's right
    /// edge, so the blur there reads what lies beyond. Synfig drops a layer it does not know without a word, so
    /// Synfig's frame must also differ from the same scene unblurred.
    /// </summary>
    [Theory]
    [InlineData("gaussian", 2d, 10d)]
    [InlineData("gaussian", 40d, 60d)]      // past Synfig's 32 px pattern limit: its FFT path, Skia's Gaussian here
    [InlineData("fastGaussian", 2d, 14d)]
    [InlineData("box", 2d, 14d)]
    [InlineData("cross", 2d, 10d)]
    public void SynfigAndSkiaBlurTheSame(string type, double from, double to)
    {
        if (Synfig() is not { } synfig) return;

        var motion = new MotionToolkit();
        var comp = Scene(motion);
        comp.Blur(Opts(("type", type), ("size", motion.Nodes.Linear("vector", new[] { to - from, to - from }, new[] { from, from }))));

        var sharp = Scene(new MotionToolkit());
        var theirs = RenderWithSynfig(synfig, comp, "blur-" + type);
        Assert.Equal(comp.FrameCount, theirs.Length);

        for (var i = 0; i < theirs.Length; i++)
        {
            using var ours = comp.Render(i / comp.Fps);
            using var plain = sharp.Render(i / comp.Fps);
            using var bitmap = theirs[i];
            Dump(ours.SkBitmap, bitmap, $"blur-{type}-{from}-{i}");

            var differing = Differing(ours.SkBitmap, bitmap, 24);
            var blurred = Differing(plain.SkBitmap, bitmap, 24);
            output.WriteLine($"{type} {from}->{to} frame {i}: {differing:P2} differ from synfig by more than 24, "
                + $"{Differing(ours.SkBitmap, bitmap, 2):P2} by more than 2; synfig differs from sharp in {blurred:P2}");
            Assert.True(blurred > 0.01, $"synfig's frame {i} is not blurred: did it drop the layer?");
            Assert.True(differing < 0.02, $"{type} differs from synfig in {differing:P2} at frame {i}");
        }
    }
    #endregion

    #region Behaviour
    /// <summary>A full-frame fill stays full to the corner: the stack is drawn past the frame for the blur to read.</summary>
    [Fact]
    public void TheFramesEdgeDoesNotFadeIn()
    {
        var comp = new MotionToolkit().Composition(Opts(("width", 60d), ("height", 40d)));
        comp.Fill(Opts(("color", "#2a6f97")));
        comp.Blur(Opts(("type", "gaussian"), ("size", 12d)));

        using var frame = comp.Render(0);
        Assert.Equal(SKColor.Parse("#2a6f97"), frame.SkBitmap.GetPixel(0, 0));
        Assert.Equal(SKColor.Parse("#2a6f97"), frame.SkBitmap.GetPixel(59, 39));
    }

    [Fact]
    public void ABlurStopsAtItsGroup()
    {
        // A black bar below the group, far from the group's orange; the group blurs only its own contents.
        var comp = new MotionToolkit().Composition(Opts(("width", 60d), ("height", 40d)));
        comp.Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 20d, 20d }), ("color", "#000000")));
        var g = comp.Group();
        g.Rectangle(Opts(("point1", new[] { 40d, 0d }), ("point2", new[] { 60d, 40d }), ("color", "#e76f51")));
        g.Blur(Opts(("size", 8d)));

        using var frame = comp.Render(0);
        Assert.Equal(SKColors.Black, frame.SkBitmap.GetPixel(10, 19));          // below the group: its edge still hard
        Assert.Equal(0, frame.SkBitmap.GetPixel(10, 20).Alpha);
        Assert.True(frame.SkBitmap.GetPixel(39, 20).Alpha > 0, "the group's own edge should be soft");
    }

    [Fact]
    public void AmountFadesBackToTheSharpStack()
    {
        var comp = new MotionToolkit().Composition(Opts(("width", 40d), ("height", 20d)));
        comp.Rectangle(Opts(("point1", new[] { 0d, 0d }), ("point2", new[] { 20d, 20d }), ("color", "#000000")));
        comp.Blur(Opts(("size", 10d), ("amount", 0d)));

        using var frame = comp.Render(0);
        Assert.Equal(SKColors.Black, frame.SkBitmap.GetPixel(19, 10));
        Assert.Equal(0, frame.SkBitmap.GetPixel(20, 10).Alpha);
    }

    [Fact]
    public void ToSifWritesSynfigsBlurLayer()
    {
        var comp = new MotionToolkit().Composition();
        comp.Blur(Opts(("type", "box"), ("size", new[] { 3d, 5d }), ("desc", "focus")));

        var sif = comp.ToSif();
        Assert.Contains("type=\"blur\"", sif);
        Assert.Contains("version=\"0.3\"", sif);
        Assert.Contains("<integer value=\"0\"", sif);          // box
        Assert.Contains("name=\"blend_method\"", sif);
    }
    #endregion

    #region Refusals
    [Theory]
    [InlineData("disc", "not ported yet")]
    [InlineData("gausian", "'gausian' is not")]
    public void AnUnknownOrUnportedTypeIsRefusedByName(string type, string expected)
    {
        var comp = new MotionToolkit().Composition();
        var e = Assert.Throws<ArgumentException>(() => comp.Blur(Opts(("type", type))));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void ABlurTakesAmountNotBlend()
    {
        var comp = new MotionToolkit().Composition();
        var e = Assert.Throws<ArgumentException>(() => comp.Blur(Opts(("blend", "multiply"))));
        Assert.Contains("has no 'blend'", e.Message);
        Assert.Contains("amount", e.Message);
    }
    #endregion

    #region SVG
    [Fact]
    public void TheSvgRouteWrapsWhatIsBelowInAGaussianBlur()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 80d), ("height", 40d), ("fps", 4d), ("duration", 1d)));
        comp.Rectangle(Opts(("point1", new[] { 10d, 10d }), ("point2", new[] { 40d, 30d }), ("color", "#2a6f97")));
        comp.Blur(Opts(("type", "gaussian"), ("size", motion.Nodes.Linear("vector", new[] { -8d, -8d }, new[] { 8d, 8d }))));
        comp.Circle(Opts(("origin", new[] { 60d, 20d }), ("radius", 8d), ("color", "#e76f51")));

        var svg = comp.ToSvg();
        Assert.Contains("<feGaussianBlur", svg);
        Assert.Contains("stdDeviation=\"3 3\"", svg);                  // 8 × 3⁄8 at the start
        Assert.Contains("attributeName=\"stdDeviation\"", svg);         // and animated
        // The circle is drawn after the blur, outside its filter.
        Assert.True(svg.IndexOf("<circle", StringComparison.Ordinal) > svg.LastIndexOf("filter=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("box", "Gaussian")]
    [InlineData("cross", "Gaussian")]
    public void TheSvgRouteRefusesANonGaussianBlur(string type, string expected)
    {
        var comp = new MotionToolkit().Composition();
        comp.Blur(Opts(("type", type), ("desc", "haze")));
        var e = Assert.Throws<InvalidOperationException>(() => comp.ToSvg());
        Assert.Contains("'haze'", e.Message);
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void TheSvgRouteRefusesAPartialAmount()
    {
        var comp = new MotionToolkit().Composition();
        comp.Blur(Opts(("type", "gaussian"), ("amount", 0.5)));
        var e = Assert.Throws<InvalidOperationException>(() => comp.ToSvg());
        Assert.Contains("amount other than 1", e.Message);
    }
    #endregion

    #region Private
    /// <summary>A light ground, a dark bar, a disc, and an orange block running off the right edge.</summary>
    private static MotionComposition Scene(MotionToolkit motion)
    {
        var comp = motion.Composition(Opts(("width", 240d), ("height", 160d), ("fps", 2d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#f2efe8")));
        comp.Rectangle(Opts(("point1", new[] { 30d, 20d }), ("point2", new[] { 60d, 140d }), ("color", "#1d3557")));
        comp.Circle(Opts(("origin", new[] { 120d, 80d }), ("radius", 30d), ("color", "#e63946")));
        comp.Rectangle(Opts(("point1", new[] { 190d, 50d }), ("point2", new[] { 260d, 110d }), ("color", "#f4a261")));
        return comp;
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
