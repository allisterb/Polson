// The camera and 3D layer model follow EffectCraft (crates/render/src/three_d/camera.rs, crates/render/src/eval.rs,
// crates/geom), Copyright the ArtCraft Team and contributors, MIT OR Apache-2.0: a layer's world matrix
// translate(position) · rotZ · rotY · rotX · scale · translate(−anchor), a camera's view from an eye, a forward
// direction and a screen-down hint, the projection x′ = zoom·x/z + w/2, the default 50 mm camera at (w/2, h/2, −zoom),
// and a plane's projective 3 × 3 taken from rows and columns 0, 1 and 3 of camera · world.

namespace Polson.Animation;

using System;
using System.Linq;

using SkiaSharp;

/// <summary>A 4 × 4 matrix on column vectors, double precision: world and camera transforms.</summary>
internal readonly struct Mat4
{
    #region Constructors
    private Mat4(double[] m) => this.m = m;
    #endregion

    #region Fields
    private readonly double[] m;   // row-major
    #endregion

    #region Properties
    public static Mat4 Identity => new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

    public double this[int r, int c] => m[r * 4 + c];
    #endregion

    #region Methods
    public static Mat4 Rows(params double[] values) => new(values);

    public static Mat4 Translate(double x, double y, double z) => Rows(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1);

    public static Mat4 RotateX(double degrees)
    {
        var (s, c) = Math.SinCos(degrees * Math.PI / 180);
        return Rows(1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0, 0, 0, 0, 1);
    }

    public static Mat4 RotateY(double degrees)
    {
        var (s, c) = Math.SinCos(degrees * Math.PI / 180);
        return Rows(c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0, 0, 0, 0, 1);
    }

    /// <summary>A 2D affine map lifted into the z = 0 plane, z untouched.</summary>
    public static Mat4 Lift(SKMatrix a) =>
        Rows(a.ScaleX, a.SkewX, 0, a.TransX, a.SkewY, a.ScaleY, 0, a.TransY, 0, 0, 1, 0, 0, 0, 0, 1);

    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        var r = new double[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                r[i * 4 + j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j] + a[i, 3] * b[3, j];
        return new(r);
    }

    public (double X, double Y, double Z) Apply(double x, double y, double z)
    {
        var w = m[12] * x + m[13] * y + m[14] * z + m[15];
        return ((m[0] * x + m[1] * y + m[2] * z + m[3]) / w, (m[4] * x + m[5] * y + m[6] * z + m[7]) / w, (m[8] * x + m[9] * y + m[10] * z + m[11]) / w);
    }

    /// <summary>The z = 0 plane's map as a projective 3 × 3: rows and columns 0, 1 and 3, as EffectCraft's <c>plane_to_mat3</c>.</summary>
    public SKMatrix Plane() => new(
        (float)m[0], (float)m[1], (float)m[3],
        (float)m[4], (float)m[5], (float)m[7],
        (float)m[12], (float)m[13], (float)m[15]);
    #endregion
}

/// <summary>
/// Where a stack's units go: into the world (3D), and onto the composition's plane (units, possibly projective).
/// <paramref name="Warped"/>: a corner pin lies between, which the world cannot follow.
/// </summary>
internal readonly record struct MotionSpace(Mat4 World, SKMatrix Plane, bool Warped)
{
    public static MotionSpace Root => new(Mat4.Identity, SKMatrix.Identity, false);
}

/// <summary>A corner pin: rectangle <see cref="From"/> drawn onto the corners <see cref="To"/> (tl, tr, br, bl).</summary>
internal sealed record MotionPin(double[] From, MotionNode[] To, bool Clip)
{
    public SKRect Rect => new((float)Math.Min(From[0], From[2]), (float)Math.Min(From[1], From[3]), (float)Math.Max(From[0], From[2]), (float)Math.Max(From[1], From[3]));

    public static MotionPin Read(MotionOptions o)
    {
        if (o.Raw("from") is not System.Collections.IList from || from.Count != 4)
            throw new ArgumentException("warp's from is the rectangle pinned, [left, top, right, bottom], in the layers' units.");
        var rect = from.Cast<object?>().Select((v, i) => MotionTypes.Read(v, MotionType.Real, $"warp's from[{i}]")[0]).ToArray();
        if (Math.Abs(rect[2] - rect[0]) < 1e-9 || Math.Abs(rect[3] - rect[1]) < 1e-9)
            throw new ArgumentException("warp's from has no area: left and right, or top and bottom, are the same.");
        if (o.Raw("to") is not System.Collections.IList to || to.Count != 4)
            throw new ArgumentException("warp's to is the four corners the rectangle lands on, [topLeft, topRight, bottomRight, bottomLeft], each a point or a node.");
        string[] names = ["topLeft", "topRight", "bottomRight", "bottomLeft"];
        var corners = to.Cast<object?>().Select((v, i) => MotionNodeFactory.Node(v, MotionType.Vector, $"warp's {names[i]} corner")).ToArray();
        var pin = new MotionPin(rect, corners, o.Bool("clip", true));
        if (pin.At(0) is null)
            throw new ArgumentException("warp's corners make no picture at 0 s: three lie in a line, or the quad folds through itself. Give them in order round the shape: tl, tr, br, bl.");
        return pin;
    }

    /// <summary>The map at a time; null when the corners make none then.</summary>
    public SKMatrix? At(double time)
    {
        SKPoint P(int i) { var v = To[i].Evaluate(time); return new((float)v[0], (float)v[1]); }
        return MotionHomography.RectToQuad(From, P(0), P(1), P(2), P(3));
    }

    /// <summary>
    /// Synfig's warp layer: the source rectangle as its two corners, the four destinations linked to their nodes. Linear
    /// resampling (1), not Synfig's default cubic (3): cubic overshoots at a hard edge and rings a light halo round a pinned
    /// shape, where this draws it as vectors with none.
    /// </summary>
    public System.Xml.Linq.XElement ToSif(SifWriter sif)
    {
        static System.Xml.Linq.XElement V(double x, double y) => SifWriter.Value(MotionType.Vector, [x, y]);
        return new System.Xml.Linq.XElement("layer", new System.Xml.Linq.XAttribute("type", "warp"),
            new System.Xml.Linq.XAttribute("active", "true"), new System.Xml.Linq.XAttribute("version", "0.2"),
            SifWriter.Param("z_depth", new System.Xml.Linq.XElement("real", new System.Xml.Linq.XAttribute("value", "0"))),
            SifWriter.Param("src_tl", V(From[0], From[1])),
            SifWriter.Param("src_br", V(From[2], From[3])),
            sif.Param("dest_tl", To[0]), sif.Param("dest_tr", To[1]), sif.Param("dest_br", To[2]), sif.Param("dest_bl", To[3]),
            SifWriter.Param("clip", SifWriter.Bool(Clip)),
            SifWriter.Param("interpolation", new System.Xml.Linq.XElement("integer", new System.Xml.Linq.XAttribute("value", "1"))));
    }
}

/// <summary>The camera at one time: world → camera space, and the projection onto the composition's units.</summary>
internal sealed record MotionCameraFrame(Mat4 View, Mat4 Projection)
{
    /// <summary>Closest a plane renders, in units of camera depth, as EffectCraft's <c>NEAR</c>.</summary>
    public const double Near = 1;

    public double Depth(double x, double y, double z) => View.Apply(x, y, z).Z;
}

/// <summary>Homographies: the map from a rectangle onto any four corners.</summary>
internal static class MotionHomography
{
    #region Methods
    /// <summary>
    /// The projective map taking rectangle <paramref name="from"/> (left, top, right, bottom) to the corners
    /// <paramref name="tl"/>, <paramref name="tr"/>, <paramref name="br"/>, <paramref name="bl"/>; null when the corners
    /// make no such map (three in a line, or a quad that folds through itself).
    /// </summary>
    /// <remarks>
    /// Heckbert's unit square to quadrilateral, after the rectangle is scaled onto the unit square. Four point pairs fix a
    /// homography up to scale, so this is the matrix Synfig's warp layer builds by its own route (<c>make_matrix</c>).
    /// </remarks>
    public static SKMatrix? RectToQuad(double[] from, SKPoint tl, SKPoint tr, SKPoint br, SKPoint bl)
    {
        var (w, h) = (from[2] - from[0], from[3] - from[1]);
        if (Math.Abs(w) < 1e-12 || Math.Abs(h) < 1e-12) return null;

        double x0 = tl.X, y0 = tl.Y, x1 = tr.X, y1 = tr.Y, x2 = br.X, y2 = br.Y, x3 = bl.X, y3 = bl.Y;
        double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
        double a, b, c, d, e, f, g, k;
        if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12)
        {
            (a, b, c, d, e, f, g, k) = (x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0);
        }
        else
        {
            double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
            var den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) < 1e-12) return null;
            g = (sx * dy2 - dx2 * sy) / den;
            k = (dx1 * sy - sx * dy1) / den;
            (a, b, c) = (x1 - x0 + g * x1, x3 - x0 + k * x3, x0);
            (d, e, f) = (y1 - y0 + g * y1, y3 - y0 + k * y3, y0);
        }

        // Unit square → quad, after rectangle → unit square.
        var square = new SKMatrix((float)a, (float)b, (float)c, (float)d, (float)e, (float)f, (float)g, (float)k, 1);
        var unit = SKMatrix.Concat(SKMatrix.CreateScale((float)(1 / w), (float)(1 / h)), SKMatrix.CreateTranslation((float)-from[0], (float)-from[1]));
        var map = SKMatrix.Concat(square, unit);

        // Every corner of the rectangle must land in front (w > 0), or the quad folds through the horizon.
        foreach (var (px, py) in new[] { (from[0], from[1]), (from[2], from[1]), (from[2], from[3]), (from[0], from[3]) })
            if (map.Persp0 * px + map.Persp1 * py + map.Persp2 <= 0) return null;
        return map.IsInvertible ? map : null;
    }
    #endregion
}
