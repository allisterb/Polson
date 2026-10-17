namespace Polson.Animation;

using System;
using System.Collections.Generic;

/// <summary>
/// A composition's camera, as an orbit: it looks at <c>target</c> from <c>distance</c>, turned <c>orbit</c> degrees about
/// the vertical and <c>tilt</c> about the horizontal, rolled <c>roll</c>, through a lens of <c>zoom</c>.
/// </summary>
/// <remarks>
/// After Effects' camera by way of EffectCraft (see <c>MotionSpace.cs</c>): world x right, y down, z into the screen; a
/// camera at distance <c>zoom</c> straight in front of a plane at z = 0 shows it at exactly 100%, which is where the
/// defaults put it, so adding a camera changes nothing until it moves. The orbit form is ours: it reads as camera
/// language — a push-in is <c>distance</c> keyed down, a pan is <c>target</c> keyed, a zoom is <c>zoom</c> keyed — and
/// every option is a number or a 2D point, so the nodes already there key it.
/// </remarks>
internal sealed class MotionCamera
{
    #region Constructors
    public MotionCamera(MotionOptions o, double[] view)
    {
        this.view = view;
        var width = Math.Abs(view[2] - view[0]);
        var standard = width * 50 / 36;     // the 50 mm lens on 36 mm film
        if (o.Has("zoom") && o.Has("focalLength"))
            throw new ArgumentException("camera takes zoom or focalLength, not both: focalLength is in millimetres on 36 mm film, zoom the same lens in units.");
        Zoom = o.Has("focalLength")
            ? new MotionScale(MotionType.Real, o.Node("focalLength", MotionType.Real, 50d), new MotionConstant(MotionType.Real, [width / 36]))
            : o.Node("zoom", MotionType.Real, standard);
        target = o.Node("target", MotionType.Vector, new[] { (view[0] + view[2]) / 2, (view[1] + view[3]) / 2 });
        targetZ = o.Node("targetZ", MotionType.Real, 0d);
        // The 50 mm lens's own distance, fixed: keying zoom then magnifies, as a lens does, rather than moving the camera with it.
        distance = o.Node("distance", MotionType.Real, standard);
        orbit = o.Node("orbit", MotionType.Angle, 0d);
        tilt = o.Node("tilt", MotionType.Angle, 0d);
        roll = o.Node("roll", MotionType.Angle, 0d);
    }
    #endregion

    #region Fields
    private readonly double[] view;
    private readonly MotionNode target, targetZ, distance, orbit, tilt, roll;
    #endregion

    #region Properties
    public MotionNode Zoom { get; }

    public IEnumerable<MotionNode> Nodes => [Zoom, target, targetZ, distance, orbit, tilt, roll];
    #endregion

    #region Methods
    /// <summary>What the camera looks at, and from how far, at a time: its target, the target's depth and its distance.</summary>
    public (double X, double Y, double Z, double Distance) TargetAt(double time)
    {
        var t = target.Evaluate(time);
        return (t[0], t[1], targetZ.Evaluate(time)[0], distance.Evaluate(time)[0]);
    }

    /// <summary>The camera at a time in the composition's seconds.</summary>
    public MotionCameraFrame At(double time)
    {
        var t = target.Evaluate(time);
        var (tx, ty, tz) = (t[0], t[1], targetZ.Evaluate(time)[0]);
        var dist = distance.Evaluate(time)[0];
        if (!(dist > MotionCameraFrame.Near))
            throw new ArgumentException($"camera's distance is {MotionTypes.Format(dist)} at {MotionTypes.Format(time)} s: it must be more than {MotionCameraFrame.Near}, in front of its target.");
        var zoom = Zoom.Evaluate(time)[0];
        if (!(zoom > 0)) throw new ArgumentException($"camera's zoom is {MotionTypes.Format(zoom)} at {MotionTypes.Format(time)} s: it must be more than 0.");

        // The eye, out from the target: straight in front (−z) at no orbit or tilt; orbit swings it right, tilt raises it.
        var (so, co) = Math.SinCos(orbit.Evaluate(time)[0] * Math.PI / 180);
        var (st, ct) = Math.SinCos(tilt.Evaluate(time)[0] * Math.PI / 180);
        var eye = (X: tx + dist * so * ct, Y: ty - dist * st, Z: tz - dist * co * ct);

        // EffectCraft's basis_view: forward towards the target, screen-down as close to world-down as it can be, rolled.
        var f = Normalize((tx - eye.X, ty - eye.Y, tz - eye.Z));
        var down = (X: 0d, Y: 1d, Z: 0d);
        var dd = Dot(down, f);
        var d = Normalize((down.X - f.X * dd, down.Y - f.Y * dd, down.Z - f.Z * dd));
        if (double.IsNaN(d.X)) d = Normalize((0, 0, -1));
        var r = Cross(d, f);
        var (sr, cr) = Math.SinCos(roll.Evaluate(time)[0] * Math.PI / 180);
        (r, d) = ((r.X * cr + d.X * sr, r.Y * cr + d.Y * sr, r.Z * cr + d.Z * sr), (d.X * cr - r.X * sr, d.Y * cr - r.Y * sr, d.Z * cr - r.Z * sr));

        var viewMatrix = Mat4.Rows(r.X, r.Y, r.Z, 0, d.X, d.Y, d.Z, 0, f.X, f.Y, f.Z, 0, 0, 0, 0, 1) * Mat4.Translate(-eye.X, -eye.Y, -eye.Z);
        var (cx, cy) = ((view[0] + view[2]) / 2, (view[1] + view[3]) / 2);
        var projection = Mat4.Rows(zoom, 0, cx, 0, 0, zoom, cy, 0, 0, 0, 1, 0, 0, 0, 1, 0) * viewMatrix;
        return new(viewMatrix, projection);
    }

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        var l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return l < 1e-12 ? (double.NaN, double.NaN, double.NaN) : (v.X / l, v.Y / l, v.Z / l);
    }

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    #endregion
}
