namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Linq;

using SkiaSharp;

/// <summary>Where a stack's shapes are at a time: <c>comp.boundsAt</c> and <c>group.boundsAt</c>.</summary>
/// <remarks>
/// becool5 placed a stylus tip on a groove under a keyed camera by rendering three probe frames and reading pixels:
/// <c>text.boundsAt</c> answered for type, and nothing answered for a shape. Read from the layers' geometry, with no
/// rendering.
/// </remarks>
public abstract partial class MotionLayerList
{
    #region Methods
    /// <summary>
    /// The ink box of what this stack draws at the film's <paramref name="time"/> seconds, through every group in it, as
    /// <c>{ x, y, width, height }</c>; null when nothing shows. <c>options</c>: <c>{ desc?, frame? }</c> — <c>desc</c> counts
    /// only the layers, or groups, named so; <c>frame</c> true for the rendered frame's pixels, through every group above
    /// and the camera, otherwise this stack's own units.
    /// </summary>
    /// <remarks>
    /// Circles, rectangles, ellipses, regions, outlines (stroke, caps and trim included), pictures (their whole rectangle) and
    /// text count; fills, gradients, noise, filters and drawn layers have no outline of their own and do not. A layer whose
    /// <c>amount</c>, or a group's above it, is 0 shows nothing, as a switch not showing it does. A blur's spread is not
    /// included.
    /// </remarks>
    public Dictionary<string, object?>? BoundsAt(double time, object? options = null)
    {
        var o = new MotionOptions(options, "boundsAt", "desc", "frame");
        var desc = o.Has("desc") ? o.Raw("desc")?.ToString() : null;
        SKMatrix outer;
        if (o.Bool("frame", false)) outer = Root?.ViewMatrix() ?? SKMatrix.Identity;
        else if (!SpaceAt(time).Space.Plane.TryInvert(out outer)) return null;

        var leaves = Leaves(this, desc is null).ToList();
        if (desc is not null && !leaves.Any(l => l.Named))
            throw new ArgumentException($"boundsAt: no layer or group here has desc '{desc}'. Name the layer with desc when you add it.");

        SKRect? box = null;
        foreach (var (stack, layer, named) in leaves)
        {
            if (!named) continue;
            var (space, local, own, visible) = stack.SpaceAt(time);
            if (!visible || stack is MotionGroup g && !g.Layer.Shows(own, layer) || stack.AmountAbove(time) <= 0) continue;
            var at = stack.TimeFor(layer, local);
            if (layer.AmountAt(at) <= 0) continue;
            using var ink = layer.Ink(at);
            if (ink is null || ink.IsEmpty) continue;
            ink.Transform(SKMatrix.Concat(outer, space.Plane));
            var r = ink.TightBounds;
            box = box is { } b ? SKRect.Union(b, r) : r;
        }

        return box is { } found ? new()
        {
            ["x"] = (double)found.Left,
            ["y"] = (double)found.Top,
            ["width"] = (double)found.Width,
            ["height"] = (double)found.Height,
        } : null;

        // Every layer below, depth first, each with whether it or a group above it carries the desc asked for.
        IEnumerable<(MotionLayerList Stack, MotionLayer Layer, bool Named)> Leaves(MotionLayerList stack, bool named)
        {
            foreach (var layer in stack.layers)
            {
                var mine = named || desc is not null && layer.Desc == desc;
                if (layer is MotionLayer.GroupLayer g)
                {
                    foreach (var leaf in Leaves(g.Children, mine)) yield return leaf;
                    if (mine && desc is not null) yield return (stack, layer, true);   // a named group counts as found, even empty
                }
                else yield return (stack, layer, mine);
            }
        }
    }
    #endregion
}
