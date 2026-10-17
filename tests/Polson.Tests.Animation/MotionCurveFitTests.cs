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
/// Curve fitting (<c>Motion.fitPoints</c>, paper.js's <c>PathFitter</c> ported): how few points, how close, corners kept
/// or rounded, a closed seam, degenerate input, and the refusals.
/// </summary>
public class MotionCurveFitTests(ITestOutputHelper output) : TestsRuntime
{
    #region Methods
    /// <summary>
    /// An ellipse sampled at 200 points comes back as a handful, every sample within <c>error</c> of the curve and the
    /// curve itself close to the true ellipse between samples, open or closed. A closed fit is smooth across its seam —
    /// paper.js's own closed fit was measured 3.4 units out there.
    /// </summary>
    [Theory]
    [InlineData(false, 0.1, 10)]
    [InlineData(true, 0.1, 10)]
    [InlineData(true, 1.0, 6)]
    public void AnEllipseFitsInAFewPointsWithinTheError(bool closed, double error, int most)
    {
        var samples = Ellipse(200);
        var fitted = Fit(samples, closed, error, corner: false);

        var (fromPoints, fromEllipse) = (samples.Max(p => Distance(fitted, closed, p)), Curve(fitted, closed).Max(EllipseError));
        output.WriteLine($"closed {closed}, error {error}: {fitted.Count} points; samples within {fromPoints:0.000}, curve within {fromEllipse:0.000} of the ellipse");
        Assert.InRange(fitted.Count, 3, most);
        Assert.True(fromPoints <= error * 1.01, $"a sample lies {fromPoints:0.000} from the curve");
        Assert.True(fromEllipse <= error * 1.5, $"the curve strays {fromEllipse:0.000} from the ellipse");
        if (closed) Assert.True(Smooth(fitted[0]), "the seam is a corner");
    }

    /// <summary>The error is a distance and scales as one: a tenth of the error keeps every sample within a tenth.</summary>
    [Fact]
    public void ErrorIsADistance()
    {
        var samples = Wave(300);
        foreach (var error in new[] { 0.05, 0.5, 5 })
        {
            var fitted = Fit(samples, false, error, corner: false);
            var worst = samples.Max(p => Distance(fitted, false, p));
            output.WriteLine($"error {error}: {fitted.Count} points, worst {worst:0.000}");
            Assert.True(worst <= error * 1.01);
            Assert.True(worst > error / 10, "the fit used little of its error: not a distance?");
        }
    }

    /// <summary>A traced square keeps its four corners sharp; with <c>corner: false</c> it is fitted smooth all round, as paper.js does.</summary>
    [Fact]
    public void CornersStaySharpUnlessToldOtherwise()
    {
        var square = Square(20);
        var sharp = Fit(square, true, 0.1, corner: 60d);
        var corners = sharp.Where(k => !Smooth(k)).Select(k => ((double[])k["point"]!)).ToList();
        Assert.Equal(4, corners.Count);
        Assert.All(new[] { (0d, 0d), (100d, 0d), (100d, 100d), (0d, 100d) },
            c => Assert.Contains(corners, p => Math.Abs(p[0] - c.Item1) < 1e-9 && Math.Abs(p[1] - c.Item2) < 1e-9));
        Assert.True(square.Max(p => Distance(sharp, true, p)) <= 0.1 * 1.01);
        Assert.Equal(4, sharp.Count);                       // a side is one straight segment

        var round = Fit(square, true, 0.1, corner: false);
        Assert.All(round, k => Assert.True(Smooth(k), "corner: false left a corner"));
    }

    /// <summary>Adjacent duplicates and a closed path's repeat of its start are dropped before fitting.</summary>
    [Fact]
    public void DuplicatesAndARepeatedStartAreDropped()
    {
        var samples = Ellipse(60);
        var messy = samples.SelectMany((p, i) => i % 7 == 0 ? new[] { p, p } : [p]).Append(samples[0]).ToList();
        Assert.Equal(Points(Fit(samples, true, 0.2, false)), Points(Fit(messy, true, 0.2, false)));
    }

    [Fact]
    public void TwoPointsAndAStraightLineFitStraight()
    {
        var two = Fit([[0d, 0d], [30d, 40d]], false, 0.5, 60d);
        Assert.Equal(2, two.Count);

        var line = Fit([.. Enumerable.Range(0, 50).Select(i => new[] { i * 2d, i * 1d })], false, 0.5, 60d);
        Assert.Equal(2, line.Count);
        var t2 = (double[])line[0]["t2"]!;
        Assert.Equal(0, t2[0] * 1 - t2[1] * 2, 6);         // the handle lies along the line
    }

    /// <summary>
    /// The points a script gets back draw, as a region, exactly the curve they describe. (Against Skia's own oval the same
    /// region differs in 0.27% of pixels while lying within 0.014 px of it: Skia antialiases a conic oval and a cubic path
    /// differently, so the geometry is checked by the ellipse tests above, not here.)
    /// </summary>
    [Fact]
    public void FittedPointsFeedARegion()
    {
        var motion = new MotionToolkit();
        var comp = motion.Composition(Opts(("width", 200d), ("height", 120d), ("duration", 0d)));
        comp.Fill(Opts(("color", "#ffffff")));
        var fitted = motion.FitPoints(Ellipse(200).Select(p => new[] { p[0] - 25, p[1] + 10 }).ToList(), Opts(("closed", true)));
        comp.Region(Opts(("points", fitted.Cast<object>().ToArray()), ("color", "#000000")));

        using var ours = comp.Render(0);
        using var theirs = new SKBitmap(200, 120);
        theirs.Erase(SKColors.White);
        using (var canvas = new SKCanvas(theirs))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        using (var path = new SKPath())
        {
            static SKPoint P(double x, double y) => new((float)x, (float)y);
            var start = (double[])fitted[0]["point"]!;
            path.MoveTo(P(start[0], start[1]));
            for (var i = 0; i < fitted.Count; i++)
            {
                var (a, b) = (fitted[i], fitted[(i + 1) % fitted.Count]);
                var (p0, p3, t2, t1) = ((double[])a["point"]!, (double[])b["point"]!, (double[])a["t2"]!, (double[])b["t1"]!);
                path.CubicTo(P(p0[0] + t2[0] / 3, p0[1] + t2[1] / 3), P(p3[0] - t1[0] / 3, p3[1] - t1[1] / 3), P(p3[0], p3[1]));
            }
            path.Close();
            canvas.DrawPath(path, paint);
        }
        Assert.Equal(0, Differing(ours.SkBitmap, theirs, 0));
    }

    [Theory]
    [InlineData("few", "at least two points")]
    [InlineData("error", "more than 0")]
    [InlineData("corner", "or false for no corners")]
    [InlineData("option", "has no 'tolerance'")]
    public void BadInputIsRefusedWithWhatIsWrong(string what, string message)
    {
        var motion = new MotionToolkit();
        var points = Ellipse(20).Cast<object>().ToArray();
        var e = Assert.Throws<ArgumentException>(() => what switch
        {
            "few" => motion.FitPoints(new object[] { new[] { 1d, 1d } }),
            "error" => motion.FitPoints(points, Opts(("error", 0d))),
            "corner" => motion.FitPoints(points, Opts(("corner", true))),
            _ => motion.FitPoints(points, Opts(("tolerance", 1d))),
        });
        Assert.Contains(message, e.Message);
    }

    private static List<Dictionary<string, object?>> Fit(IEnumerable<double[]> points, bool closed, double error, object corner) =>
        new MotionToolkit().FitPoints(points.Cast<object>().ToList(), Opts(("closed", closed), ("error", error), ("corner", corner)));

    private static List<string> Points(List<Dictionary<string, object?>> fitted) =>
        [.. fitted.Select(k => string.Join(",", ((double[])k["point"]!).Concat((double[])k["t1"]!).Concat((double[])k["t2"]!).Select(v => v.ToString("0.######"))))];

    private static List<double[]> Ellipse(int n) =>
        [.. Enumerable.Range(0, n).Select(i => i * 2 * Math.PI / n).Select(a => new[] { 125 + 25 * Math.Cos(a), 50 + 15 * Math.Sin(a) })];

    private static List<double[]> Wave(int n) =>
        [.. Enumerable.Range(0, n).Select(i => new[] { i * 1d, 40 * Math.Sin(i / 25d) + 15 * Math.Sin(i / 7d) })];

    private static List<double[]> Square(int perSide) =>
        [.. new[] { (0d, 0d, 1d, 0d), (100d, 0d, 0d, 1d), (100d, 100d, -1d, 0d), (0d, 100d, 0d, -1d) }
            .SelectMany(s => Enumerable.Range(0, perSide).Select(i => new[] { s.Item1 + s.Item3 * 100 * i / perSide, s.Item2 + s.Item4 * 100 * i / perSide }))];

    /// <summary>The fitted curve, finely sampled.</summary>
    private static IEnumerable<double[]> Curve(List<Dictionary<string, object?>> fitted, bool closed)
    {
        var count = closed ? fitted.Count : fitted.Count - 1;
        for (var s = 0; s < count; s++)
        {
            var (a, b) = (fitted[s], fitted[(s + 1) % fitted.Count]);
            var (p0, p3) = ((double[])a["point"]!, (double[])b["point"]!);
            var (t2, t1) = ((double[])a["t2"]!, (double[])b["t1"]!);
            for (var k = 0; k <= 400; k++)
            {
                var t = k / 400d;
                var u = 1 - t;
                yield return [.. Enumerable.Range(0, 2).Select(c =>
                    p0[c] * u * u * u + (p0[c] + t2[c] / 3) * 3 * u * u * t + (p3[c] - t1[c] / 3) * 3 * u * t * t + p3[c] * t * t * t)];
            }
        }
    }

    private static double Distance(List<Dictionary<string, object?>> fitted, bool closed, double[] p) =>
        Curve(fitted, closed).Min(q => Math.Sqrt((q[0] - p[0]) * (q[0] - p[0]) + (q[1] - p[1]) * (q[1] - p[1])));

    // Radial distance from the 25 × 15 ellipse at (125, 50), close to the true distance at these proportions.
    private static double EllipseError(double[] p) =>
        Math.Abs(Math.Sqrt(Math.Pow((p[0] - 125) / 25, 2) + Math.Pow((p[1] - 50) / 15, 2)) - 1) * 15;

    /// <summary>Whether a point's tangents in and out point the same way (or there is no curve to speak of).</summary>
    private static bool Smooth(Dictionary<string, object?> k)
    {
        var (a, b) = ((double[])k["t1"]!, (double[])k["t2"]!);
        var (la, lb) = (Math.Sqrt(a[0] * a[0] + a[1] * a[1]), Math.Sqrt(b[0] * b[0] + b[1] * b[1]));
        if (la < 1e-9 || lb < 1e-9) return false;
        return (a[0] * b[0] + a[1] * b[1]) / (la * lb) > 0.9999;
    }
    #endregion
}
