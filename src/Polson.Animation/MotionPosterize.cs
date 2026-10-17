namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Xml.Linq;

using Polson.Drawing.Skia;

/// <summary>
/// Synfig's stroboscope layer, After Effects' Posterize Time: every layer below it in its stack sees time stepped to
/// <c>fps</c> steps a second, <c>floor(t·fps)/fps</c> — motion on twos at 12 on 24, the stepped look of hand animation.
/// </summary>
/// <remarks>
/// <b>Ported from Synfig</b> (<c>modules/lyr_std/stroboscope.cpp</c>, GPL-2-or-later): an invisible layer that sets the
/// time of its context; at a frequency of 0 or less it holds the start. Synfig keeps both the frequency and the time in
/// single precision, so the step is taken in floats here too, and a frame exactly on a step falls on the same side.
/// </remarks>
internal sealed class MotionPosterizeLayer(MotionOptions o) : MotionLayer(o)
{
    #region Fields
    private readonly MotionNode fps = o.Node("fps", MotionType.Real, 2d);
    #endregion

    #region Methods
    public override IEnumerable<MotionNode> Nodes() => [Amount, fps];

    /// <summary>The time the layers below see when this one sees <paramref name="time"/>.</summary>
    public double Map(double time)
    {
        var f = (float)fps.Evaluate(time)[0];
        if (!(f > 0)) return 0;
        return 1.0 / f * Math.Floor((float)time * f);
    }

    protected override void Draw(SkiaCanvas surface, double time, double amount) { }

    public override XElement ToSif(SifWriter sif) =>
        new("layer", new XAttribute("type", "stroboscope"), new XAttribute("active", "true"), new XAttribute("version", "0.1"),
            Desc is null ? null : new XAttribute("desc", Desc),
            SifWriter.Param("z_depth", new XElement("real", new XAttribute("value", "0"))),
            sif.Param("frequency", fps));

    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map) =>
        throw new InvalidOperationException($"The posterize time{(Desc is null ? "" : $" '{Desc}'")} cannot be written as animated SVG: SMIL has no "
            + "way to step time. Write a .sif, or capture the composition as frames.");
    #endregion
}
