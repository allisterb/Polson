namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Xml.Linq;

/// <summary>
/// A point on a spring: a tip that hangs off its origin at rest and, when the origin moves, lags, overshoots and
/// settles, as a weight on a springy stalk does. Follow-through for an antenna, an earring, a tail, a feather.
/// </summary>
/// <remarks>
/// <para>
/// Synfig's <c>dynamic</c> converter (<c>valuenode_dynamic.cpp</c>, <c>valuenode_dynamic.h</c>,
/// <c>valuenode_derivative.cpp</c>; Synfig, GPL-2-or-later), with its model unchanged: the tip's distance and angle from
/// the origin are each a damped spring toward the rest offset <c>tip</c> (<c>spring</c> and <c>damping</c> along the
/// stalk, <c>torsion</c> and <c>friction</c> around it, against <c>mass</c> and <c>inertia</c> + mass × distance²), pushed by
/// <c>force</c> and <c>torque</c>, and seen from the origin, whose acceleration (Synfig's five-point second difference,
/// h = 0.01 s) is taken off. Integrated by adaptive Cash–Karp Runge–Kutta to 1e-5, as Synfig does.
/// </para>
/// <para>
/// <b>Where it differs is the clock, on purpose.</b> Synfig integrates on from the last time it was asked and starts
/// again from 0 when asked for an earlier one, so its value depends on the order frames are rendered in. Here the
/// motion is always integrated from 0 on a fixed grid of checkpoints, each remembered once reached, and a time between
/// two is integrated on from the one before it: any frame, in any order, gives the same value. It is still a
/// simulation, so the first request for a late time pays for every second before it.
/// </para>
/// <para>
/// <b>And it runs in Synfig's units, 60 pixels by default</b>, because its angular equation is only well scaled there
/// (see <see cref="unit"/>). The <c>.sif</c> carries the same scaling as Synfig's own <c>scale</c> converters around it, so
/// Synfig computes what this does.
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. See <see cref="MotionNode"/>.
/// </para>
/// </remarks>
public sealed class MotionDynamic : MotionNode
{
    #region Constructors
    internal MotionDynamic(MotionNode tip, MotionNode origin, MotionNode force, MotionNode torque, MotionNode damping, MotionNode friction,
        MotionNode spring, MotionNode torsion, MotionNode mass, MotionNode inertia, bool springRigid, bool torsionRigid, bool originDragsTip,
        double unit = 1)
        : base(MotionType.Vector)
    {
        // Lengths go in and come out in pixels, and the simulation runs in units of `unit` pixels: see Unit.
        this.unit = unit;
        MotionNode InUnits(MotionNode n) => unit == 1 ? n : new MotionScale(MotionType.Vector, n, new MotionConstant(MotionType.Real, [1 / unit]));
        (tip, origin, force) = (InUnits(tip), InUnits(origin), InUnits(force));
        (this.tip, this.origin, this.force, this.torque, this.damping, this.friction) = (tip, origin, force, torque, damping, friction);
        (this.spring, this.torsion, this.mass, this.inertia) = (spring, torsion, mass, inertia);
        (this.springRigid, this.torsionRigid, this.originDragsTip) = (springRigid, torsionRigid, originDragsTip);
        checkpoints.Add(Rest());
    }
    #endregion

    #region Fields
    /// <summary>Seconds between remembered states.</summary>
    internal const double Grid = 0.02;

    /// <summary>How far a simulation may be asked to run.</summary>
    internal const double Longest = 600;

    const double Tolerance = 1e-5, Minimum = 1e-7, DerivativeStep = 0.01;

    /// <summary>
    /// Pixels per simulation unit. Synfig's angular equation is not scale-free — it takes the origin's acceleration off the
    /// angle's as it is, and weighs the stalk by mass × length² — so its defaults are tuned for lengths of about one of its
    /// own units, 60 pixels by default. Run in pixels, a 60-pixel stalk spins like a propeller.
    /// </summary>
    private readonly double unit;
    private readonly MotionNode tip, origin, force, torque, damping, friction, spring, torsion, mass, inertia;
    private readonly bool springRigid, torsionRigid, originDragsTip;
    /// <summary>The state at each multiple of <see cref="Grid"/> reached so far: distance, its speed, angle (radians), its speed.</summary>
    private readonly List<double[]> checkpoints = [];
    private readonly object gate = new();
    #endregion

    #region Properties
    internal override IEnumerable<MotionNode> Children => [tip, origin, force, torque, damping, friction, spring, torsion, mass, inertia];
    #endregion

    #region Methods
    internal override double[] Evaluate(double time)
    {
        // Synfig's first frame: the origin plus the rest offset, whatever originDragsTip says.
        if (time == 0)
        {
            var (o0, rest) = (origin.Evaluate(0), checkpoints[0]);
            return [unit * (o0[0] + rest[0] * Math.Cos(rest[2])), unit * (o0[1] + rest[0] * Math.Sin(rest[2]))];
        }

        var state = time < 0 ? checkpoints[0] : StateAt(time);
        var t = tip.Evaluate(time);
        var r = springRigid ? Math.Sqrt(t[0] * t[0] + t[1] * t[1]) : state[0];
        var a = torsionRigid ? Math.Atan2(t[1], t[0]) : state[2];
        var o = originDragsTip ? origin.Evaluate(time) : [0d, 0d];
        return [unit * (o[0] + r * Math.Cos(a)), unit * (o[1] + r * Math.Sin(a))];
    }

    // In units, the inputs carry their own scale converters, and the result is scaled back to pixels by Synfig's own.
    internal override XElement ToSif(SifWriter sif) => unit == 1 ? Dynamic(sif)
        : new("scale", new XAttribute("type", Type), new XElement("link", Dynamic(sif)),
            new XElement("scalar", SifWriter.Value(MotionType.Real, [unit])));

    private XElement Dynamic(SifWriter sif) =>
        new("dynamic", new XAttribute("type", Type),
            sif.Link("tip_static", tip), sif.Link("origin", origin), sif.Link("force", force), sif.Link("torque", torque),
            sif.Link("damping", damping), sif.Link("friction", friction), sif.Link("spring", spring), sif.Link("torsion", torsion),
            sif.Link("mass", mass), sif.Link("inertia", inertia),
            new XElement("spring_rigid", SifWriter.Bool(springRigid)),
            new XElement("torsion_rigid", SifWriter.Bool(torsionRigid)),
            new XElement("origin_drags_tip", SifWriter.Bool(originDragsTip)));

    /// <summary>At rest: the tip where <c>tip</c> puts it at time 0, not moving.</summary>
    private double[] Rest()
    {
        var t = tip.Evaluate(0);
        return [Math.Sqrt(t[0] * t[0] + t[1] * t[1]), 0, Math.Atan2(t[1], t[0]), 0];
    }

    private double[] StateAt(double time)
    {
        if (time > Longest)
            throw new ArgumentException($"Motion.nodes.dynamic simulates from 0, and {MotionTypes.Time(time)} is past the {MotionTypes.Time(Longest)} it will run.");
        lock (gate)
        {
            var k = (int)Math.Floor(time / Grid);
            while (checkpoints.Count <= k)
            {
                var i = checkpoints.Count;
                checkpoints.Add(Integrate(checkpoints[i - 1], (i - 1) * Grid, i * Grid));
            }
            return Integrate(checkpoints[k], k * Grid, time);
        }
    }

    /// <summary>Adaptive Cash–Karp Runge–Kutta from <paramref name="t0"/> to <paramref name="t1"/>, as Synfig steps it.</summary>
    private double[] Integrate(double[] start, double t0, double t1)
    {
        var y = (double[])start.Clone();
        var t = t0;
        var h = (t1 - t0) / 4;
        while (t1 - t > 1e-12)
        {
            h = Math.Min(h, t1 - t);
            var (next, error) = Step(y, t, h);
            var factor = error == 0 ? 5 : 0.9 * Math.Pow(Tolerance / error, 0.2);
            if (error > Tolerance && h > 1e-9)
            {
                h *= Math.Max(0.2, factor);
                continue;
            }
            (y, t) = (next, t + h);
            h *= Math.Min(5, factor);
        }
        return y;
    }

    private (double[] Next, double Error) Step(double[] y, double t, double h)
    {
        double[] At(double[] k1, double c1, double[]? k2 = null, double c2 = 0, double[]? k3 = null, double c3 = 0,
                    double[]? k4 = null, double c4 = 0, double[]? k5 = null, double c5 = 0)
        {
            var r = new double[4];
            for (var i = 0; i < 4; i++)
                r[i] = y[i] + h * (c1 * k1[i] + c2 * (k2?[i] ?? 0) + c3 * (k3?[i] ?? 0) + c4 * (k4?[i] ?? 0) + c5 * (k5?[i] ?? 0));
            return r;
        }

        var a1 = Derivative(t, y);
        var a2 = Derivative(t + h / 5, At(a1, 1d / 5));
        var a3 = Derivative(t + h * 3 / 10, At(a1, 3d / 40, a2, 9d / 40));
        var a4 = Derivative(t + h * 3 / 5, At(a1, 3d / 10, a2, -9d / 10, a3, 6d / 5));
        var a5 = Derivative(t + h, At(a1, -11d / 54, a2, 5d / 2, a3, -70d / 27, a4, 35d / 27));
        var a6 = Derivative(t + h * 7 / 8, At(a1, 1631d / 55296, a2, 175d / 512, a3, 575d / 13824, a4, 44275d / 110592, a5, 253d / 4096));

        var next = new double[4];
        var error = 0d;
        for (var i = 0; i < 4; i++)
        {
            var fifth = h * (37d / 378 * a1[i] + 250d / 621 * a3[i] + 125d / 594 * a4[i] + 512d / 1771 * a6[i]);
            var fourth = h * (2825d / 27648 * a1[i] + 18575d / 48384 * a3[i] + 13525d / 55296 * a4[i] + 277d / 14336 * a5[i] + 0.25 * a6[i]);
            next[i] = y[i] + fifth;
            error = Math.Max(error, Math.Abs(fifth - fourth));
        }
        return (next, error);
    }

    /// <summary>Synfig's oscillator: the rates of change of distance, its speed, angle and its speed.</summary>
    private double[] Derivative(double time, double[] x)
    {
        double R(MotionNode n) => n.Evaluate(time)[0];
        var (f, s, p) = (force.Evaluate(time), OriginAcceleration(time), tip.Evaluate(time));
        var (c, mu, k, kappa, m, inertiaAt) = (R(damping), R(friction), R(spring), R(torsion), R(mass), R(inertia));

        var (ux, uy) = (Math.Cos(x[2]), Math.Sin(x[2]));
        var (fr, fa) = (f[0] * ux + f[1] * uy, -f[0] * uy + f[1] * ux);
        var (sr, sa) = (s[0] * ux + s[1] * uy, -s[0] * uy + s[1] * ux);
        var (r0, a0) = (Math.Sqrt(p[0] * p[0] + p[1] * p[1]), Math.Atan2(p[1], p[0]));

        var radial = springRigid || Math.Abs(m) <= Minimum ? 0 : (fr - c * x[1] - k * (x[0] - r0)) / m - sr;
        var moment = inertiaAt + m * x[0] * x[0];
        var angular = torsionRigid || Math.Abs(moment) <= Minimum ? 0 : (R(torque) + fa * x[0] - mu * x[3] - kappa * (x[2] - a0)) / moment - sa;
        return [x[1], radial, x[3], angular];
    }

    /// <summary>The origin's acceleration: Synfig's second derivative at its default, the five-point stencil with h = 0.01 s.</summary>
    private double[] OriginAcceleration(double time)
    {
        const double h = DerivativeStep;
        var (m2, m1, z, p1, p2) = (origin.Evaluate(time - 2 * h), origin.Evaluate(time - h), origin.Evaluate(time), origin.Evaluate(time + h), origin.Evaluate(time + 2 * h));
        return [Second(0), Second(1)];
        double Second(int i) => (-m2[i] + 16 * m1[i] - 30 * z[i] + 16 * p1[i] - p2[i]) / (12 * h * h);
    }
    #endregion
}
