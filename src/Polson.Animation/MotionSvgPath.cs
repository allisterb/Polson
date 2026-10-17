namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using SkiaSharp;

/// <summary>
/// SVG path data as Synfig spline points: one contour per subpath, each point with its own incoming and outgoing tangent,
/// so a corner stays a corner and a curve keeps its exact Bézier controls.
/// </summary>
/// <remarks>
/// Skia parses the data, so every command SVG has is read, arcs included. Lines and cubics are exact; a quadratic is its
/// exact cubic; an arc comes out of the parser as conics of at most 90°, each written as one cubic whose handles are
/// <c>4w / 3(1 + w)</c> of the way to the conic's control point — for a circular arc that is the usual 0.5523, off by at
/// most 0.03% of the radius.
/// </remarks>
internal static class MotionSvgPath
{
    #region Methods
    /// <summary>
    /// The contours of <paramref name="d"/>, outer before inner, each marked a hole when <paramref name="evenOdd"/> or the
    /// nonzero rule leaves its inside empty. For a <paramref name="fill"/>, contours that cross are redrawn so they do not.
    /// </summary>
    public static List<Contour> Read(object? d, bool evenOdd, string who, bool fill = true)
    {
        if (d is not string data || data.Trim().Length == 0)
            throw new ArgumentException($"{who}'s path is SVG path data, a string such as 'M10 10 Q50 0 90 10 Z'.");
        using var path = SKPath.ParseSvgPathData(data)
            ?? throw new ArgumentException($"{who}'s path could not be read as SVG path data: '{Clip(data)}'.");

        var contours = Contours(path);
        if (contours.Count == 0)
            throw new ArgumentException($"{who}'s path draws nothing: '{Clip(data)}' has no segment.");

        // Holes by nesting are exact only while no contour crosses another. Where one does, the fill is re-drawn as
        // contours that do not cross — different points from the data's, so only then.
        if (fill && Crosses(contours, evenOdd))
        {
            path.FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
            using var simple = path.Simplify();
            if (simple is not null && Contours(simple) is { Count: > 0 } plain)
                return Classify(plain, simple.FillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd);
        }
        return Classify(contours, evenOdd);

        static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "…";
    }

    /// <summary>Each subpath as points with Synfig tangents: <c>t2</c> leaves a point toward its next control, <c>t1</c> arrives from its previous one.</summary>
    private static List<Contour> Contours(SKPath path)
    {
        var result = new List<Contour>();
        List<Node>? nodes = null;
        var loop = false;
        var pts = new SKPoint[4];
        using var it = path.CreateRawIterator();

        for (var verb = it.Next(pts); verb != SKPathVerb.Done; verb = it.Next(pts))
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Flush();
                    nodes = [new(pts[0])];
                    break;
                case SKPathVerb.Line:
                    To(pts[0], pts[1], pts[1]);
                    break;
                case SKPathVerb.Quad:
                    To(pts[0] + Scale(pts[1] - pts[0], 2f / 3), pts[2] + Scale(pts[1] - pts[2], 2f / 3), pts[2]);
                    break;
                case SKPathVerb.Conic:
                    Conic(pts[0], pts[1], pts[2], it.ConicWeight(), 0);
                    break;
                case SKPathVerb.Cubic:
                    To(pts[1], pts[2], pts[3]);
                    break;
                case SKPathVerb.Close:
                    loop = true;
                    break;
            }
        }

        Flush();
        return result;

        // One cubic per conic of at most 90° — the parser's arcs — and a wider one, which a simplified path has, halved
        // until it is: the cubic's error grows fast past a quarter turn.
        void Conic(SKPoint p0, SKPoint p1, SKPoint p2, float w, int depth)
        {
            if (w < 0.7f && depth < 4)
            {
                var m = Scale(p0 + Scale(p1, 2 * w) + p2, 1 / (2 * (1 + w)));
                var half = MathF.Sqrt((1 + w) / 2);
                Conic(p0, Scale(p0 + Scale(p1, w), 1 / (1 + w)), m, half, depth + 1);
                Conic(m, Scale(Scale(p1, w) + p2, 1 / (1 + w)), p2, half, depth + 1);
                return;
            }
            var k = 4 * w / (3 * (1 + w));
            To(p0 + Scale(p1 - p0, k), p2 + Scale(p1 - p2, k), p2);
        }

        void To(SKPoint c1, SKPoint c2, SKPoint end)
        {
            var last = nodes![^1];
            if (Near(last.P, end) && Near(last.P, c1) && Near(last.P, c2)) return;
            last.T2 = Scale(c1 - last.P, 3);
            nodes.Add(new(end) { T1 = Scale(end - c2, 3) });
        }

        void Flush()
        {
            if (nodes is { Count: > 1 })
            {
                // A path that comes back to its start before Z closes on that point, not with a second one on top of it.
                if (loop && Near(nodes[0].P, nodes[^1].P))
                {
                    nodes[0].T1 = nodes[^1].T1;
                    nodes.RemoveAt(nodes.Count - 1);
                }
                if (nodes.Count > 1) result.Add(new([.. nodes], loop));
            }
            (nodes, loop) = (null, false);
        }
    }

    /// <summary>
    /// Orders the contours outer first and marks each a hole or not by the winding just inside it: the sum of the
    /// directions of every contour around that place, itself included.
    /// </summary>
    /// <remarks>
    /// Drawn in that order, a filled contour painting and a hole erasing, nested contours come out as the fill rule draws
    /// them. Contours that cross one another are taken as nested by a vote of points along each.
    /// </remarks>
    private static List<Contour> Classify(List<Contour> contours, bool evenOdd)
    {
        var shapes = contours.Select(c => c.ToPath()).ToArray();
        try
        {
            var signs = contours.Select(c => Math.Sign(c.SignedArea()) is var s && s == 0 ? 1 : s).ToArray();
            var inside = contours.Select((c, i) => Enumerable.Range(0, contours.Count)
                .Where(j => j != i && Contains(shapes[j], c)).ToArray()).ToArray();

            return [.. contours.Select((c, i) =>
            {
                var winding = signs[i] + inside[i].Sum(j => signs[j]);
                return (Contour: c with { Hole = evenOdd ? (inside[i].Length + 1) % 2 == 0 : winding == 0 }, Depth: inside[i].Length, Index: i);
            }).OrderBy(x => x.Depth).ThenBy(x => x.Index).Select(x => x.Contour)];
        }
        finally
        {
            foreach (var s in shapes) s.Dispose();
        }

        static bool Contains(SKPath outer, Contour c)
        {
            var samples = c.Samples(5);
            return samples.Count(p => outer.Contains(p.X, p.Y)) * 2 > samples.Length;
        }
    }

    /// <summary>
    /// Whether any two contours overlap without one lying inside the other, or, under even-odd, a contour crosses itself —
    /// the cases a stack of nested regions cannot draw. A single region fills nonzero, so under that rule a contour may
    /// cross itself freely.
    /// </summary>
    private static bool Crosses(List<Contour> contours, bool evenOdd)
    {
        var shapes = contours.Select(c => c.ToPath()).ToArray();
        try
        {
            if (evenOdd && shapes.Any(SelfCrossing)) return true;
            for (var i = 0; i < shapes.Length; i++)
            {
                for (var j = i + 1; j < shapes.Length; j++)
                {
                    if (!shapes[i].TightBounds.IntersectsWith(shapes[j].TightBounds)) continue;
                    if (Empty(shapes[i], shapes[j], SKPathOp.Intersect)) continue;
                    if (!Empty(shapes[i], shapes[j], SKPathOp.Difference) && !Empty(shapes[j], shapes[i], SKPathOp.Difference)) return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (var s in shapes) s.Dispose();
        }

        static bool SelfCrossing(SKPath shape)
        {
            using var odd = new SKPath(shape) { FillType = SKPathFillType.EvenOdd };
            return !Empty(odd, shape, SKPathOp.Xor);
        }

        // Empty but for slivers along a shared edge, which path ops can leave behind.
        static bool Empty(SKPath a, SKPath b, SKPathOp op)
        {
            using var result = a.Op(b, op);
            if (result is null || result.IsEmpty) return true;
            var box = result.TightBounds;
            return box.Width * box.Height < 0.05f || Math.Abs(Area(result)) < 0.05;
        }

        static double Area(SKPath p)
        {
            using var measure = new SKPathMeasure(p, true);
            double sum = 0;
            do
            {
                const int n = 64;
                var q = Enumerable.Range(0, n).Select(i => measure.GetPosition(measure.Length * i / n)).ToArray();
                sum += Math.Abs(Enumerable.Range(0, n).Sum(i => (double)q[i].X * q[(i + 1) % n].Y - (double)q[(i + 1) % n].X * q[i].Y)) / 2;
            }
            while (measure.NextContour());
            return sum;
        }
    }

    private static bool Near(SKPoint a, SKPoint b) => Math.Abs(a.X - b.X) < 1e-4 && Math.Abs(a.Y - b.Y) < 1e-4;

    private static SKPoint Scale(SKPoint p, float k) => new(p.X * k, p.Y * k);
    #endregion

    #region Types
    internal sealed class Node(SKPoint p)
    {
        public SKPoint P { get; } = p;

        public SKPoint T1 { get; set; }

        public SKPoint T2 { get; set; }
    }

    /// <summary>One subpath's points, whether it closes, and whether its inside is empty under the fill rule.</summary>
    internal sealed record Contour(Node[] Points, bool Loop, bool Hole = false)
    {
        /// <summary>The points as a region or outline takes them: <c>{ point, t1, t2 }</c>, plain values a script can change.</summary>
        public List<Dictionary<string, object?>> ToJs() =>
            [.. Points.Select(n => new Dictionary<string, object?>
            {
                ["point"] = new double[] { n.P.X, n.P.Y },
                ["t1"] = new double[] { n.T1.X, n.T1.Y },
                ["t2"] = new double[] { n.T2.X, n.T2.Y },
            })];

        /// <summary>The contour closed, as a fill sees it whether or not it loops.</summary>
        public SKPath ToPath()
        {
            var path = new SKPath();
            path.MoveTo(Points[0].P);
            for (var i = 1; i <= Points.Length; i++)
            {
                if (i == Points.Length && !Loop) break;
                var (a, b) = (Points[i - 1], Points[i % Points.Length]);
                path.CubicTo(a.P + Scale(a.T2, 1f / 3), b.P - Scale(b.T1, 1f / 3), b.P);
            }
            path.Close();
            return path;
        }

        /// <summary>Points spread along the contour, between its vertices so a shared vertex does not decide a vote.</summary>
        public SKPoint[] Samples(int count)
        {
            using var path = ToPath();
            using var measure = new SKPathMeasure(path, true);
            return [.. Enumerable.Range(0, count).Select(i => measure.GetPosition(measure.Length * (i + 0.37f) / count))];
        }

        /// <summary>Twice the area enclosed, signed by direction, from the outline sampled finely.</summary>
        public double SignedArea()
        {
            using var path = ToPath();
            using var measure = new SKPathMeasure(path, true);
            const int n = 256;
            var p = Enumerable.Range(0, n).Select(i => measure.GetPosition(measure.Length * i / n)).ToArray();
            return Enumerable.Range(0, n).Sum(i => (double)p[i].X * p[(i + 1) % n].Y - (double)p[(i + 1) % n].X * p[i].Y);
        }
    }
    #endregion
}
