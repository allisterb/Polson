// Ported from paper.js, src/path/PathFitter.js:
//
//   Paper.js - The Swiss Army Knife of Vector Graphics Scripting. http://paperjs.org/
//   Copyright (c) 2011 - 2020, Jürg Lehni & Jonathan Puckey. http://juerglehni.com/ & https://puckey.studio/
//   Distributed under the MIT license.
//
//   An Algorithm for Automatically Fitting Digitized Curves, by Philip J. Schneider, from "Graphics Gems",
//   Academic Press, 1990. Modifications and optimizations of original algorithm by Jürg Lehni.
//
// Read beside Spillman.SkiaSharp.PathFitter (MIT, Copyright (c) 2020 Dalton Spillman), a C# port of the same file;
// see reference/README.md for where the two differ.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
// persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice
// and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS
// PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
// ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// A few cubic Béziers fitted to many points, each point within <c>error</c> of the curve: Schneider's algorithm as
/// paper.js implements it (<c>Path.simplify</c>), with three deliberate changes.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b><c>error</c> is a distance.</b> paper.js compares the largest <i>squared</i> distance with its tolerance, so
/// <c>simplify(2.5)</c> lets a point stray about 1.58 units and the tolerance scales as a square. Here the comparison is
/// with <c>error²</c>, and every threshold in the algorithm is carried over in the same terms, so <c>error = √t</c>
/// reproduces paper.js's fit for its tolerance <c>t</c>.</item>
/// <item><b>Corners stay corners.</b> paper.js fits one smooth path, taking each split's tangent across its neighbours,
/// so a traced rectangle comes back rounded. Here the points are first cut wherever the direction turns by more than
/// <c>corner</c> degrees, and each run is fitted on its own with one-sided end tangents. With no corner angle an open fit
/// is paper.js's.</item>
/// <item><b>A closed path is fitted round to its own start</b>, not paper.js's way; see <see cref="FitClosed"/>.</item>
/// </list>
/// The arithmetic is double throughout, as JavaScript's is.
/// </remarks>
internal static class MotionCurveFit
{
    #region Constants
    // paper.js Numerical.EPSILON and Numerical.MACHINE_EPSILON.
    private const double Epsilon = 1e-12, MachineEpsilon = 1.12e-16;
    #endregion

    #region Methods
    /// <summary>
    /// The fitted knots for <paramref name="input"/>: each an on-curve point with paper.js's handles, the offsets to its
    /// control points. A closed fit's last knot curves back to its first.
    /// </summary>
    public static List<Knot> Fit(IReadOnlyList<V> input, bool closed, double error, double? cornerDegrees)
    {
        // Copy over points, filtering out adjacent duplicates, and a closed path's repeat of its start.
        var points = new List<V>();
        foreach (var p in input)
            if (points.Count == 0 || points[^1] != p) points.Add(p);
        if (closed && points.Count > 1 && points[^1] == points[0]) points.RemoveAt(points.Count - 1);
        if (points.Count == 0) return [];
        if (points.Count == 1) return [new(points[0])];

        var corners = cornerDegrees is { } deg ? Corners(points, closed, deg) : [];
        if (corners.Count == 0) return closed ? FitClosed(points, error) : FitOpen(points, error);

        // A closed path with corners is an open one from a corner round to the same corner.
        if (closed)
        {
            var (start, n) = (corners[0], points.Count);
            points = [.. points.Skip(start), .. points.Take(start), points[start]];
            corners = [.. corners.Select(c => c - start), n];
        }
        else corners = [0, .. corners, points.Count - 1];

        var knots = new List<Knot>();
        for (var i = 1; i < corners.Count; i++)
        {
            var run = points.GetRange(corners[i - 1], corners[i] - corners[i - 1] + 1);
            var fitted = FitOpen(run, error);
            if (knots.Count > 0)
            {
                // The corner is the end of one run and the start of the next: its handle in from the one, out from the other.
                knots[^1] = knots[^1] with { HandleOut = fitted[0].HandleOut };
                fitted.RemoveAt(0);
            }
            knots.AddRange(fitted);
        }

        if (closed)
        {
            knots[0] = knots[0] with { HandleIn = knots[^1].HandleIn };
            knots.RemoveAt(knots.Count - 1);
        }
        return knots;
    }

    /// <summary>
    /// One run between two known tangents — paper.js's <c>fitCubic</c> over the whole run, which is what refitting a curve
    /// whose ends we already know needs. <paramref name="tan1"/> leaves the first point, <paramref name="tan2"/> leaves
    /// the last point backwards.
    /// </summary>
    public static List<Knot> FitRun(IReadOnlyList<V> points, V tan1, V tan2, double error)
    {
        var fitter = new Fitter(points, error);
        var knots = new List<Knot> { new(points[0]) };
        if (points.Count > 1) fitter.FitCubic(knots, 0, points.Count - 1, tan1, tan2);
        return knots;
    }

    /// <summary>paper.js's <c>fit</c> for an open path: one run, its end tangents across the end points' neighbours.</summary>
    private static List<Knot> FitOpen(List<V> points, double error) =>
        FitRun(points, points[1] - points[0], points[^2] - points[^1], error);

    /// <summary>
    /// A closed path fitted open from its first point round to the same point, the tangent there taken across its two
    /// neighbours so the seam is smooth, and the repeated end merged into the start.
    /// </summary>
    /// <remarks>
    /// <b>Not paper.js's way.</b> paper.js puts the last point before the first and the first after the last, fits that,
    /// and drops the two extra knots — but a fit places its knots where its error is largest, rarely on those two points, so
    /// the closing curve is drawn with handles aimed at knots that are no longer there. Measured: an ellipse 25 × 15,
    /// sampled at 200 points and fitted to 0.1, came back 3.4 units out at the seam that way; this way, within 0.1.
    /// </remarks>
    private static List<Knot> FitClosed(List<V> points, double error)
    {
        if (points.Count == 2) return FitOpen(points, error);
        var across = points[1] - points[^1];
        var knots = FitRun([.. points, points[0]], across, across * -1, error);
        knots[0] = knots[0] with { HandleIn = knots[^1].HandleIn };
        knots.RemoveAt(knots.Count - 1);
        return knots;
    }

    /// <summary>The indices where the direction turns by more than <paramref name="degrees"/>; a closed path's start may be one.</summary>
    private static List<int> Corners(List<V> points, bool closed, double degrees)
    {
        var corners = new List<int>();
        var n = points.Count;
        for (var i = closed ? 0 : 1; i < (closed ? n : n - 1); i++)
        {
            var a = points[i] - points[(i - 1 + n) % n];
            var b = points[(i + 1) % n] - points[i];
            var turn = Math.Abs(Math.Atan2(a.X * b.Y - a.Y * b.X, a.Dot(b))) * 180 / Math.PI;
            if (turn > degrees) corners.Add(i);
        }
        return corners;
    }
    #endregion

    #region Types
    /// <summary>A plain double vector: the fitter's arithmetic, kept off <c>SKPoint</c>'s floats.</summary>
    public readonly record struct V(double X, double Y)
    {
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y);

        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y);

        public static V operator *(V a, double k) => new(a.X * k, a.Y * k);

        public double Dot(V b) => X * b.X + Y * b.Y;

        public double Length => Math.Sqrt(X * X + Y * Y);

        public double DistanceTo(V b) => (this - b).Length;

        /// <summary>This direction at <paramref name="length"/>; zero stays zero, as in paper.js.</summary>
        public V Normalize(double length)
        {
            var current = Length;
            return this * (current != 0 ? length / current : 0);
        }
    }

    /// <summary>An on-curve point and its handles, offsets to the control points either side, as paper.js's segment.</summary>
    public readonly record struct Knot(V Point, V HandleIn = default, V HandleOut = default);

    /// <summary>paper.js's <c>PathFitter</c> methods over one list of points.</summary>
    private sealed class Fitter(IReadOnlyList<V> points, double error)
    {
        // paper.js compares squared distances with its tolerance; ours is a distance, so the tolerance is its square.
        private readonly double tolerance = error * error;

        // Fit a Bezier curve to a (sub)set of digitized points.
        public void FitCubic(List<Knot> knots, int first, int last, V tan1, V tan2)
        {
            // Use heuristic if region only has two points in it.
            if (last - first == 1)
            {
                var (pt1, pt2) = (points[first], points[last]);
                var dist = pt1.DistanceTo(pt2) / 3;
                AddCurve(knots, [pt1, pt1 + tan1.Normalize(dist), pt2 + tan2.Normalize(dist), pt2]);
                return;
            }

            // Parameterize points, and attempt to fit curve.
            var uPrime = ChordLengthParameterize(first, last);
            var maxError = Math.Max(tolerance, tolerance * tolerance);
            var split = 0;
            var parametersInOrder = true;

            // Try 4 iterations.
            for (var i = 0; i <= 4; i++)
            {
                var curve = GenerateBezier(first, last, uPrime, tan1, tan2);
                // Find max deviation of points to fitted curve.
                var (max, index) = FindMaxError(first, last, curve, uPrime);
                if (max < tolerance && parametersInOrder)
                {
                    AddCurve(knots, curve);
                    return;
                }
                split = index;
                // If error not too large, try reparameterization and iteration.
                if (max >= maxError) break;
                parametersInOrder = Reparameterize(first, last, uPrime, curve);
                maxError = max;
            }

            // Fitting failed -- split at max error point and fit recursively.
            var tanCenter = points[split - 1] - points[split + 1];
            FitCubic(knots, first, split, tan1, tanCenter);
            FitCubic(knots, split, last, tanCenter * -1, tan2);
        }

        private static void AddCurve(List<Knot> knots, V[] curve)
        {
            knots[^1] = knots[^1] with { HandleOut = curve[1] - curve[0] };
            knots.Add(new(curve[3], curve[2] - curve[3]));
        }

        // Use least-squares method to find Bezier control points for region.
        private V[] GenerateBezier(int first, int last, double[] uPrime, V tan1, V tan2)
        {
            var (pt1, pt2) = (points[first], points[last]);
            // Create the C and X matrices.
            double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
            for (int i = 0, l = last - first + 1; i < l; i++)
            {
                var u = uPrime[i];
                var t = 1 - u;
                var b = 3 * u * t;
                var (b0, b1, b2, b3) = (t * t * t, b * t, b * u, u * u * u);
                var a1 = tan1.Normalize(b1);
                var a2 = tan2.Normalize(b2);
                var tmp = points[first + i] - pt1 * (b0 + b1) - pt2 * (b2 + b3);
                c00 += a1.Dot(a1);
                c01 += a1.Dot(a2);
                c11 += a2.Dot(a2);
                x0 += a1.Dot(tmp);
                x1 += a2.Dot(tmp);
            }
            var c10 = c01;

            // Compute the determinants of C and X.
            var detC0C1 = c00 * c11 - c10 * c01;
            double alpha1, alpha2;
            if (Math.Abs(detC0C1) > Epsilon)
            {
                // Kramer's rule.
                var detC0X = c00 * x1 - c10 * x0;
                var detXC1 = x0 * c11 - x1 * c01;
                // Derive alpha values.
                alpha1 = detXC1 / detC0C1;
                alpha2 = detC0X / detC0C1;
            }
            else
            {
                // Matrix is under-determined, try assuming alpha1 == alpha2.
                var (s0, s1) = (c00 + c01, c10 + c11);
                alpha1 = alpha2 = Math.Abs(s0) > Epsilon ? x0 / s0 : Math.Abs(s1) > Epsilon ? x1 / s1 : 0;
            }

            // If alpha negative, use the Wu/Barsky heuristic (see text). (If alpha is 0, you get coincident control
            // points that lead to divide by zero in any subsequent NewtonRaphsonRootFind() call.)
            var segLength = pt2.DistanceTo(pt1);
            var eps = Epsilon * segLength;
            V? handle1 = null, handle2 = null;
            if (alpha1 < eps || alpha2 < eps)
            {
                // Fall back on standard (probably inaccurate) formula, and subdivide further if needed.
                alpha1 = alpha2 = segLength / 3;
            }
            else
            {
                // Check if the found control points are in the right order when projected onto the line through pt1 and
                // pt2. Control points 1 and 2 are positioned an alpha distance out on the tangent vectors.
                var line = pt2 - pt1;
                handle1 = tan1.Normalize(alpha1);
                handle2 = tan2.Normalize(alpha2);
                if (handle1.Value.Dot(line) - handle2.Value.Dot(line) > segLength * segLength)
                {
                    // Fall back to the Wu/Barsky heuristic above.
                    alpha1 = alpha2 = segLength / 3;
                    handle1 = handle2 = null;
                }
            }

            // First and last control points of the Bezier curve are positioned exactly at the first and last data points.
            return [pt1, pt1 + (handle1 ?? tan1.Normalize(alpha1)), pt2 + (handle2 ?? tan2.Normalize(alpha2)), pt2];
        }

        // Given set of points and their parameterization, try to find a better parameterization.
        private bool Reparameterize(int first, int last, double[] u, V[] curve)
        {
            for (var i = first; i <= last; i++) u[i - first] = FindRoot(curve, points[i], u[i - first]);
            // Detect if the new parameterization has reordered the points. In that case, we would fit the points of the
            // path in the wrong order.
            for (var i = 1; i < u.Length; i++)
                if (u[i] <= u[i - 1]) return false;
            return true;
        }

        // Use Newton-Raphson iteration to find better root.
        private static double FindRoot(V[] curve, V point, double u)
        {
            // Generate control vertices for Q' and Q''.
            V[] curve1 = [.. Enumerable.Range(0, 3).Select(i => (curve[i + 1] - curve[i]) * 3)];
            V[] curve2 = [.. Enumerable.Range(0, 2).Select(i => (curve1[i + 1] - curve1[i]) * 2)];
            // Compute Q(u), Q'(u) and Q''(u).
            var pt = Evaluate(3, curve, u);
            var pt1 = Evaluate(2, curve1, u);
            var pt2 = Evaluate(1, curve2, u);
            var diff = pt - point;
            var df = pt1.Dot(pt1) + diff.Dot(pt2);
            // u = u - f(u) / f'(u)
            return Math.Abs(df) <= MachineEpsilon ? u : u - diff.Dot(pt1) / df;
        }

        // Evaluate a bezier curve at a particular parameter value.
        private static V Evaluate(int degree, V[] curve, double t)
        {
            var tmp = (V[])curve.Clone();
            // Triangle computation.
            for (var i = 1; i <= degree; i++)
                for (var j = 0; j <= degree - i; j++)
                    tmp[j] = tmp[j] * (1 - t) + tmp[j + 1] * t;
            return tmp[0];
        }

        // Assign parameter values to digitized points using relative distances between points.
        private double[] ChordLengthParameterize(int first, int last)
        {
            var u = new double[last - first + 1];
            for (var i = first + 1; i <= last; i++) u[i - first] = u[i - first - 1] + points[i].DistanceTo(points[i - 1]);
            for (int i = 1, m = last - first; i <= m; i++) u[i] /= u[m];
            return u;
        }

        // Find the maximum squared distance of digitized points to fitted curve.
        private (double Error, int Index) FindMaxError(int first, int last, V[] curve, double[] u)
        {
            var index = (last - first + 1) / 2;
            var maxDist = 0d;
            for (var i = first + 1; i < last; i++)
            {
                var v = Evaluate(3, curve, u[i - first]) - points[i];
                var dist = v.X * v.X + v.Y * v.Y;
                if (dist >= maxDist) (maxDist, index) = (dist, i);
            }
            return (maxDist, index);
        }
    }
    #endregion
}
