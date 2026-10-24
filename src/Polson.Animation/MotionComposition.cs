namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Polson.Drawing.Skia;

using SkiaSharp;

/// <summary>
/// An ordered stack of layers, bottom first: what a composition and a group both are.
/// </summary>
/// <remarks>
/// <b>Spike.</b> Synfig's layer model, for the four layers the spike covers plus a solid fill. Each
/// option is a value or a <see cref="MotionNode"/>, so any parameter can be keyed, computed or shared.
/// Coordinates are the composition's view-box units, pixels with y down unless the composition says
/// otherwise; angles are degrees, clockwise on screen when y is down. An option the layer does not have
/// is refused by name.
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase
/// spelling onto them, so a script calling <c>x.doThing()</c> reaches <c>DoThing()</c>. The camelCase
/// form is the one documented in <c>docs/Polson.core.md</c> and the studio manuals.
/// </para>
/// </remarks>
public abstract partial class MotionLayerList
{
    #region Fields
    private protected readonly List<MotionLayer> layers = [];
    #endregion

    #region Properties
    /// <summary>How many layers it holds.</summary>
    public int LayerCount => layers.Count;
    #endregion

    #region Methods
    /// <summary>A flat fill over the whole frame: <c>{ color, amount?, blend?, desc? }</c>.</summary>
    public MotionLayerList Fill(object? options)
    {
        var o = new MotionOptions(options, "fill", "color", "amount", "blend", "desc");
        layers.Add(new MotionLayer.SolidColor(o));
        return this;
    }

    /// <summary>A disc: <c>{ origin, radius, color, invert?, amount?, blend?, desc? }</c>.</summary>
    public MotionLayerList Circle(object? options)
    {
        var o = new MotionOptions(options, "circle", "origin", "radius", "color", "invert", "amount", "blend", "desc");
        layers.Add(new MotionLayer.CircleLayer(o));
        return this;
    }

    /// <summary>
    /// A gradient over the whole plane along a line: <c>{ point1, point2, stops?, loop?, zigzag?, amount?, blend?, desc? }</c>.
    /// <c>stops</c> are colours spread evenly, or <c>[[pos, color], ...]</c> from 0 at <c>point1</c> to 1 at <c>point2</c>.
    /// To fill a shape with it, put both in a group and give the gradient <c>blend: 'onto'</c>.
    /// </summary>
    public MotionLayerList LinearGradient(object? options)
    {
        var o = new MotionOptions(options, "linearGradient", "point1", "point2", "stops", "loop", "zigzag", "amount", "blend", "desc");
        layers.Add(new MotionGradientLayer(o, radial: false));
        return this;
    }

    /// <summary>
    /// A gradient over the whole plane by distance from a centre: <c>{ origin, radius, stops?, loop?, zigzag?, amount?, blend?, desc? }</c>,
    /// stop 0 at <c>origin</c> and 1 at <c>radius</c> from it. Fills a shape as <see cref="LinearGradient"/> does.
    /// </summary>
    public MotionLayerList RadialGradient(object? options)
    {
        var o = new MotionOptions(options, "radialGradient", "origin", "radius", "stops", "loop", "zigzag", "amount", "blend", "desc");
        layers.Add(new MotionGradientLayer(o, radial: true));
        return this;
    }

    /// <summary>
    /// A rectangle between two corners, in either order:
    /// <c>{ point1, point2, expand?, radius?, color, invert?, amount?, blend?, desc? }</c>. <c>expand</c> grows it on
    /// every side; <c>radius</c> rounds its corners, Synfig's bevel, at most half the shorter side. The bar of a bar chart:
    /// keyed corners stay exact in SVG.
    /// </summary>
    public MotionLayerList Rectangle(object? options)
    {
        var o = new MotionOptions(options, "rectangle",
            "point1", "point2", "expand", "radius", "color", "invert", "amount", "blend", "desc");
        layers.Add(new MotionLayer.RectangleLayer(o));
        return this;
    }

    /// <summary>
    /// An ellipse: <c>{ origin, radius: [rx, ry], angle?, color, invert?, amount?, blend?, desc? }</c>. <c>radius</c> is a
    /// vector, or a number for a circle; <c>angle</c> turns it about <c>origin</c>.
    /// </summary>
    /// <remarks>
    /// Synfig has no ellipse layer. This is what Synfig Studio's own circle tool leaves under a scaled group: a unit circle
    /// in a group offset to <c>origin</c>, turned by <c>angle</c> and scaled by <c>radius</c>, so every parameter stays
    /// keyable and the <c>.sif</c> stays exact.
    /// </remarks>
    public MotionLayerList Ellipse(object? options)
    {
        var o = new MotionOptions(options, "ellipse", "origin", "radius", "angle", "color", "invert", "amount", "blend", "desc");
        var radius = o.Raw("radius") switch
        {
            null => new[] { 10d, 10d },
            MotionNode { Kind: not MotionType.Vector } n => throw new ArgumentException(
                $"ellipse's radius is a vector, [rx, ry], and this node is a {n.Type}. Key a vector node, or use comp.circle for a keyed round radius."),
            double or int or float or long => (object)Enumerable.Repeat(Convert.ToDouble(o.Raw("radius")), 2).ToArray(),
            var r => r
        };
        var group = new Hashtable { ["scale"] = radius };
        if (o.Has("origin")) group["offset"] = o.Raw("origin");
        if (o.Has("angle")) group["angle"] = o.Raw("angle");
        foreach (var key in new[] { "amount", "blend", "desc" }.Where(o.Has)) group[key] = o.Raw(key);
        var circle = o.Subset("color", "invert");
        circle["radius"] = 1d;
        Group(group).Circle(circle);
        return this;
    }

    /// <summary>
    /// A filled spline: <c>{ points | path, fillRule?, loop?, origin?, color, invert?, amount?, blend?, desc? }</c>.
    /// <c>path</c> takes SVG path data in place of <c>points</c>; see <see cref="FromPath"/>.
    /// </summary>
    public MotionLayerList Region(object? options)
    {
        var o = new MotionOptions(options, "region",
            "points", "path", "fillRule", "loop", "origin", "color", "invert", "amount", "blend", "desc");
        if (FromPath(o) is not { } contours)
        {
            layers.Add(new MotionLayer.RegionLayer(o));
            return this;
        }

        if (contours.Count == 1) return Region(Contour(o, contours[0], "origin", "color", "invert", "amount", "blend", "desc"));

        // Several contours draw in a group of their own, outer first, a hole erasing what is under it; inverted, the
        // group starts filled and each contour does the opposite.
        var group = Group(o.Subset("amount", "blend", "desc"));
        var invert = o.Bool("invert", false);
        if (invert) group.Fill(o.Subset("color"));
        foreach (var c in contours)
        {
            var paints = c.Hole == invert;
            var region = Contour(o, c, paints ? ["origin", "color"] : ["origin"]);
            if (!paints) region["blend"] = "alphaOver";
            group.Region(region);
        }
        return this;
    }

    /// <summary>
    /// A stroked spline whose width can vary along it:
    /// <c>{ points | path, loop?, origin?, width?, color, sharpCusps?, roundTips?, trimStart?, trimEnd?, trimOffset?, amount?, blend?, desc? }</c>.
    /// <c>path</c> takes SVG path data in place of <c>points</c>, a group of outlines when it has several subpaths.
    /// <c>trimStart</c>, <c>trimEnd</c> and <c>trimOffset</c> show only part of it, as fractions of its length: a write-on
    /// keys <c>trimEnd</c> from 0 to 1.
    /// </summary>
    public MotionLayerList Outline(object? options)
    {
        var o = new MotionOptions(options, "outline",
            "points", "path", "loop", "origin", "width", "color", "sharpCusps", "roundTips", "trimStart", "trimEnd", "trimOffset", "amount", "blend", "desc");
        if (FromPath(o, fill: false) is not { } contours)
        {
            layers.Add(new MotionLayer.OutlineLayer(o));
            return this;
        }

        string[] stroke = ["origin", "width", "color", "sharpCusps", "roundTips", "trimStart", "trimEnd", "trimOffset"];
        if (contours.Count == 1) return Outline(Contour(o, contours[0], [.. stroke, "amount", "blend", "desc"]));

        var group = Group(o.Subset("amount", "blend", "desc"));
        foreach (var c in contours) group.Outline(Contour(o, c, stroke));
        return this;
    }

    /// <summary>
    /// The contours of a <c>path</c> option, or null when <c>points</c> is used instead. Every SVG command is read; each
    /// subpath is a contour that loops when it ends in <c>Z</c>, and its points keep SVG's corners and curves exactly.
    /// <c>fillRule</c> is SVG's, <c>'nonzero'</c> by default or <c>'evenodd'</c>, and decides which contours are holes.
    /// </summary>
    private static List<MotionSvgPath.Contour>? FromPath(MotionOptions o, bool fill = true)
    {
        if (!o.Has("path"))
        {
            if (o.Has("fillRule")) throw new ArgumentException($"{o.Who}'s fillRule goes with path, which it takes in place of points.");
            return null;
        }
        if (o.Has("points")) throw new ArgumentException($"{o.Who} takes points or path, not both.");
        if (o.Has("loop")) throw new ArgumentException($"{o.Who}'s path says whether each subpath closes, with Z: drop loop.");

        var rule = o.Has("fillRule") ? o.Raw("fillRule") as string : "nonzero";
        if (rule is not ("nonzero" or "evenodd"))
            throw new ArgumentException($"{o.Who}'s fillRule is 'nonzero' or 'evenodd', as in SVG.");
        return MotionSvgPath.Read(o.Raw("path"), rule == "evenodd", o.Who, fill);
    }

    /// <summary>The options for one contour's layer: its points and loop, and the given keys of the call's own options.</summary>
    private static Hashtable Contour(MotionOptions o, MotionSvgPath.Contour c, params string[] keys)
    {
        var options = o.Subset(keys);
        options["points"] = c.ToJs();
        options["loop"] = c.Loop;
        return options;
    }

    /// <summary>
    /// A picture placed between two corners: <c>{ image, tl?, br?, interpolation?, amount?, blend?, desc? }</c>.
    /// <c>image</c> is a bitmap, a canvas, or anything carrying its own pixels (a cutout cell, a photograph);
    /// it is copied when the layer is made, so drawing on the canvas afterwards does not change it.
    /// <c>tl</c> and <c>br</c> default to the picture's own size at the origin; swapping them mirrors it.
    /// </summary>
    public MotionLayerList Image(object? options)
    {
        var o = new MotionOptions(options, "image", "image", "tl", "br", "interpolation", "amount", "blend", "desc");
        layers.Add(new MotionLayer.ImageLayer(o));
        return this;
    }

    /// <summary>
    /// A piece cut out of a picture, as a group that can be posed: the picture, and an inverted region
    /// that erases everything outside <paramref name="points"/>. <c>options</c> takes the image's
    /// <c>tl</c>, <c>br</c> and <c>interpolation</c> and the group's <c>origin</c>, <c>offset</c>,
    /// <c>angle</c>, <c>skewAngle</c>, <c>scale</c>, <c>bone</c>, <c>amount</c>, <c>blend</c>, <c>timeOffset</c>,
    /// <c>timeDilation</c> and <c>desc</c>. With <c>bone</c> the piece follows the bone from the rest pose.
    /// </summary>
    /// <remarks>
    /// Synfig Studio's Cutout tool builds exactly this: an import layer under a region with
    /// <c>invert</c> on and blend method alpha over (<c>state_lasso.cpp</c>). The outline is in the
    /// composition's units, where the picture is placed — not in the picture's pixels — so a piece
    /// placed at its own size at the origin takes its outline in pixels.
    /// </remarks>
    public MotionGroup Cutout(object? image, object? points, object? options = null)
    {
        var o = new MotionOptions(options, "cutout",
            "tl", "br", "interpolation",
            "origin", "offset", "angle", "skewAngle", "scale", "bone", "amount", "blend", "timeOffset", "timeDilation", "desc");
        var group = Group(o.Subset("origin", "offset", "angle", "skewAngle", "scale", "bone", "amount", "blend", "timeOffset", "timeDilation", "desc"));
        var picture = o.Subset("tl", "br", "interpolation");
        picture["image"] = image;
        group.Image(picture);
        group.Region(new Hashtable { ["points"] = points, ["invert"] = true, ["blend"] = "alphaOver", ["desc"] = "cut" });
        return group;
    }

    /// <summary>
    /// A group with its own transformation and clock, returned so layers can be added to it:
    /// <c>{ origin?, offset?, angle?, skewAngle?, scale?, bone?, amount?, blend?, timeOffset?, timeDilation?, desc? }</c>.
    /// With <c>bone</c> the group takes its transform from the bone instead, carried from the rest pose.
    /// </summary>
    public MotionGroup Group(object? options = null)
    {
        var o = new MotionOptions(options, "group",
            "origin", "offset", "angle", "skewAngle", "scale", "z", "rotateX", "rotateY", "keepSize", "bone", "amount", "blend", "timeOffset", "timeDilation", "desc");
        var group = new MotionGroup(o) { Root = Root, Parent = this };
        layers.Add(group.Layer);
        return group;
    }

    /// <summary>
    /// A group whose layers are pinned to four corners: <c>{ from: [left, top, right, bottom], to: [tl, tr, br, bl], clip?,
    /// ...the group's options }</c>, returned so layers can be added to it. The rectangle <c>from</c>, in the layers' units,
    /// is drawn onto the corners <c>to</c>, each keyable; <c>clip</c> (true) shows nothing outside the rectangle.
    /// </summary>
    /// <remarks>
    /// Synfig's warp layer ("Perspective"), on top of the layers in a group of their own. The pinned layers are drawn as
    /// vectors through the perspective, so type stays sharp.
    /// </remarks>
    public MotionGroup Warp(object? options)
    {
        var o = new MotionOptions(options, "warp",
            "from", "to", "clip", "origin", "offset", "angle", "skewAngle", "scale", "amount", "blend", "timeOffset", "timeDilation", "desc");
        var group = new MotionGroup(o, pin: MotionPin.Read(o)) { Root = Root, Parent = this };
        layers.Add(group.Layer);
        return group;
    }

    /// <summary>
    /// A group that shows one of its layers at a time, chosen by name: <c>{ active, ...the group's options }</c>, returned so
    /// layers can be added to it, each named with <c>desc</c>. <c>active</c> is a name, or held keys
    /// <c>[{ time, value: name }]</c> or <c>[[time, name]]</c>; <c>''</c> shows nothing. Synfig's switch layer.
    /// </summary>
    /// <remarks>
    /// A mouth chart, a hand's poses, a feature drawn in detail for the close-up and plainly for the wide shot: draw each
    /// once, name it, and key which is showing. Names do not blend, so a key holds until the next. Where two layers share
    /// a name the topmost (the last added) shows, as in Synfig; a name no layer has is refused when the switch first draws.
    /// </remarks>
    public MotionGroup Switch(object? options)
    {
        var o = new MotionOptions(options, "switch",
            "active", "origin", "offset", "angle", "skewAngle", "scale", "z", "rotateX", "rotateY", "keepSize", "bone", "amount", "blend", "timeOffset", "timeDilation", "desc");
        var group = new MotionGroup(o, MotionSwitchTrack.Read(o.Raw("active"), "switch")) { Root = Root, Parent = this };
        layers.Add(group.Layer);
        return group;
    }

    /// <summary>
    /// A layer the SDK draws: <c>draw(ctx, v, t)</c> is called for every frame with a context in the
    /// layer's frame, <c>v</c> holding each of <c>options.values</c> evaluated at <c>t</c>.
    /// <c>options</c>: <c>{ values?, amount?, desc? }</c>.
    /// </summary>
    /// <remarks>
    /// The bridge between the model and the drawing toolkits: nodes say when, the toolkit says what.
    /// <c>t</c> is the layer's own time, so a group's <c>timeOffset</c> and <c>timeDilation</c> reach
    /// it. The canvas is saved before the call and restored after, so nothing the function does to the
    /// transform, clip or state reaches the next layer.
    /// </remarks>
    public MotionLayerList Drawn(Action<CanvasRenderingContext2D, object?, double>? draw, object? options = null)
    {
        if (draw is null)
            throw new ArgumentException("drawn(draw, options?) takes a function first: (ctx, v, t) => { ... }.");
        var o = new MotionOptions(options, "drawn", "values", "amount", "blend", "desc");
        layers.Add(new MotionLayer.DrawnLayer(o, draw));
        return this;
    }

    /// <summary>
    /// A face, rebuilt every frame from its channels:
    /// <c>{ origin?, height?, yaw?, pitch?, construction?, character?, poses?, channels?, look?, amount?, blend?, desc? }</c>.
    /// Returns the face, which can report its head at any time.
    /// </summary>
    /// <remarks>
    /// <c>channels</c> takes Action Units (<c>AU12</c>), expressions (<c>joy</c>) and Hamm's eye-wheel settings
    /// (<c>upperLid</c>, <c>browTopInner</c>, ...), each a number or a node, prefixed <c>near.</c> or <c>far.</c> for one
    /// side; <c>character</c> takes <c>createParametricHead</c>'s parameters the same way. <c>poses</c> is a library of
    /// named channel sets, each blended in by a channel of its name, as blend shapes are. <c>look</c> passes drawing
    /// options to each feature drawer.
    /// </remarks>
    public MotionFace Face(object? options = null)
    {
        var layer = new MotionFaceLayer(new MotionOptions(options, "face", MotionFaceLayer.Options));
        layers.Add(layer);
        return new MotionFace(layer);
    }

    /// <summary>
    /// Catches <c>drawn({ ... })</c> with no function, which would otherwise fail with the binder's
    /// generic "no public methods" message instead of saying what is missing.
    /// </summary>
    /// <remarks>
    /// Typed as the object literal Jint passes, not <c>object</c>: as <c>object</c> it also caught <c>drawn(fn)</c> —
    /// a lone function arrives as a <c>Func</c>, which an <c>object</c> takes, and one argument matched this overload
    /// sooner than the one whose options are optional — so the documented short form threw "takes a function first".
    /// </remarks>
    public MotionLayerList Drawn(System.Dynamic.ExpandoObject options) =>
        throw new ArgumentException("drawn(draw, options?) takes a function first: comp.drawn((ctx, v, t) => { ... }, { values }).");

    /// <summary>
    /// A bone deformation of everything below it in this stack:
    /// <c>{ bones?, point1?, point2?, xSubdivisions?, ySubdivisions?, amount?, desc? }</c>. <c>bones</c>
    /// defaults to all the composition's bones, the corners to their rest capsules' bounds, the grid to 32 × 32.
    /// </summary>
    /// <remarks>
    /// Synfig's skeleton deformation layer. A bone's <c>width</c> and <c>tipwidth</c> are its reach: only what
    /// lies inside the bones' rest capsules is kept and bent, so give each bone the width of the artwork it carries.
    /// </remarks>
    public MotionLayerList SkeletonDeformation(object? options = null)
    {
        var o = new MotionOptions(options, "skeletonDeformation",
            "bones", "point1", "point2", "xSubdivisions", "ySubdivisions", "amount", "desc");
        layers.Add(new MotionDeformationLayer(o, Root?.Bones ?? []));
        return this;
    }

    /// <summary>
    /// A line or lines of type, Synfig's text layer:
    /// <c>{ text, family, weight?, style?, size?, origin?, orient?, color?, compress?, vcompress?, kerning?, invert?, animators?, amount?, blend?, desc? }</c>.
    /// <c>size</c> is the font's em in units; <c>orient</c> aligns the block on <c>origin</c>, <c>[0, 0]</c> its top left and
    /// the default <c>[0.5, 0.5]</c> its centre. <c>animators</c> move, scale, turn, fade, recolour, track and blur letters,
    /// words or lines by how far a selector picks them; see the SDK reference. Returns the text, which can report where its
    /// letters are at any time.
    /// </summary>
    public MotionText Text(object? options)
    {
        var o = new MotionOptions(options, "text", "text", "family", "weight", "style", "size", "origin", "orient", "color",
            "compress", "vcompress", "kerning", "invert", "animators", "amount", "blend", "desc");
        var layer = new MotionTextLayer(o, Root?.YDown ?? true, Root);
        layers.Add(layer);
        var handle = new MotionText(layer, this);
        Root?.TextHandles.Add(handle);
        return handle;
    }

    /// <summary>
    /// A blur of everything below it in this stack: <c>{ size?, type?, amount?, desc? }</c>. <c>size</c> is in units, a
    /// number for both axes or <c>[x, y]</c>, and keyable — a focus pull is a keyed size. <c>type</c> is
    /// <c>'gaussian'</c> (the default), <c>'fastGaussian'</c>, <c>'box'</c> or <c>'cross'</c>.
    /// </summary>
    /// <remarks>
    /// Synfig's blur layer. Like Synfig's it stops at its own group: put it in a group with what it should blur.
    /// It replaces the stack below at full <c>amount</c> and fades back to the sharp stack as <c>amount</c> falls.
    /// </remarks>
    public MotionLayerList Blur(object? options = null)
    {
        var o = new MotionOptions(options, "blur", "size", "type", "amount", "desc");
        layers.Add(new MotionBlurLayer(o));
        return this;
    }

    /// <summary>
    /// Steps time for everything below it in this stack: <c>{ fps, desc? }</c>. Those layers see <c>floor(t·fps)/fps</c>, so
    /// at 12 on a 24 fps film they move on twos, the stepped look of hand animation; <c>fps</c> keys.
    /// </summary>
    /// <remarks>Synfig's stroboscope layer, After Effects' Posterize Time. It stops at its own group.</remarks>
    public MotionLayerList PosterizeTime(object? options)
    {
        var o = new MotionOptions(options, "posterizeTime", "fps", "desc");
        if (!o.Has("fps")) throw new ArgumentException("posterizeTime takes fps: the steps a second the layers below it move in, 12 for twos on a 24 fps film.");
        layers.Add(new MotionPosterizeLayer(o));
        return this;
    }

    /// <summary>
    /// A wipe of everything below it in this stack, hidden as <c>completion</c> runs 0 → 1:
    /// <c>{ type, completion, feather?, angle?, over?, startAngle?, center?, sweep?, points?, innerRadius?, rotation?, invert?, width?, amount?, desc? }</c>.
    /// <c>type</c> is <c>'linear'</c> (<c>angle</c>, 90 left to right; <c>over</c>, a box <c>[x0, y0, x1, y1]</c> or
    /// <c>{ x, y, width, height }</c>, to cross that and erase only inside it), <c>'radial'</c> (<c>startAngle</c> clockwise from up,
    /// <c>center</c>, <c>sweep</c> clockwise, counterclockwise or both), <c>'iris'</c> (<c>center</c>, a circle or <c>points</c>
    /// with <c>innerRadius</c> and <c>rotation</c>, <c>invert</c> to close down instead) or <c>'blinds'</c> (<c>angle</c>,
    /// <c>width</c>). A reveal keys completion 1 → 0. It stops at its own group.
    /// </summary>
    /// <remarks>EffectCraft's wipes, written to .sif as Synfig layers that erase (alpha over).</remarks>
    public MotionLayerList Wipe(object? options)
    {
        var given = JsInterop.AsDict(options) ?? throw new ArgumentException("wipe takes an options object: { type, completion, ... }.");
        if (given.Contains("blend")) throw new ArgumentException("wipe has no 'blend': it erases what is below it, Synfig's alpha over.");
        var withBlend = new Hashtable();
        foreach (DictionaryEntry e in given) withBlend[e.Key] = e.Value;
        withBlend["blend"] = "alphaOver";
        var o = new MotionOptions(withBlend, "wipe", "type", "completion", "feather", "angle", "over", "startAngle", "center", "sweep",
            "points", "innerRadius", "rotation", "invert", "width", "amount", "blend", "desc");
        layers.Add(new MotionWipeLayer(o, this));
        return this;
    }

    /// <summary>
    /// A colour correction of everything below it in this stack: <c>{ hue?, brightness?, contrast?, exposure?, saturation?,
    /// gamma?, desc? }</c>, each keyable. <c>hue</c> turns hues by degrees, <c>exposure</c> is in stops (× eˣ),
    /// <c>brightness</c> adds, <c>contrast</c> scales about mid-grey, <c>saturation</c> scales towards each colour's own
    /// brightest channel, <c>gamma</c> bends the midtones. At the defaults (0, 0, 1, 0, 1, 1) it changes nothing.
    /// </summary>
    /// <remarks>Synfig's colour correct layer. It stops at its own group: grade a whole film at the top, or one layer in a group.</remarks>
    public MotionLayerList ColorCorrect(object? options = null)
    {
        var o = new MotionOptions(options, "colorCorrect", "hue", "brightness", "contrast", "exposure", "saturation", "gamma", "desc");
        layers.Add(new MotionColorCorrectLayer(o));
        return this;
    }

    /// <summary>
    /// The time <paramref name="layer"/> sees when its stack sees <paramref name="time"/>: stepped by every posterize above it.
    /// </summary>
    internal double TimeFor(MotionLayer layer, double time)
    {
        for (var i = layers.Count - 1; i >= 0 && !ReferenceEquals(layers[i], layer); i--)
            if (layers[i] is MotionPosterizeLayer p) time = p.Map(time);
        return time;
    }

    /// <summary>Each layer's time when the stack sees <paramref name="time"/>; the identity with no posterize here.</summary>
    private Func<MotionLayer, double> Times(double time)
    {
        if (!layers.Any(l => l is MotionPosterizeLayer)) return _ => time;
        var at = new Dictionary<MotionLayer, double>(ReferenceEqualityComparer.Instance);
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            at[layers[i]] = time;
            if (layers[i] is MotionPosterizeLayer p) time = p.Map(time);
        }
        return l => at[l];
    }

    /// <summary>
    /// A shadow of everything below it in this stack: <c>{ offset?, size?, type?, color?, invert?, amount?, blend?, desc? }</c>.
    /// The stack's shape in <c>color</c> (black), moved by <c>offset</c> ([6, 6]) and blurred by <c>size</c> (6, a number or
    /// <c>[x, y]</c>, keyable, the blur's kinds), blended <c>behind</c> the stack at <c>amount</c> 0.75 unless told otherwise.
    /// An inner shadow is <c>invert: true, blend: 'onto'</c>.
    /// </summary>
    /// <remarks>Synfig's shade layer. Like the blur it stops at its own group: put it in a group with what casts it.</remarks>
    public MotionLayerList Shadow(object? options = null)
    {
        var given = JsInterop.AsDict(options) ?? (options is null ? new Hashtable() : throw new ArgumentException("shadow takes an options object."));
        var withDefaults = new Hashtable();
        foreach (DictionaryEntry e in given) withDefaults[e.Key] = e.Value;
        if (!withDefaults.Contains("amount") || withDefaults["amount"] is null) withDefaults["amount"] = 0.75;
        if (!withDefaults.Contains("blend") || withDefaults["blend"] is null) withDefaults["blend"] = "behind";
        var o = new MotionOptions(withDefaults, "shadow", "offset", "size", "type", "color", "invert", "amount", "blend", "desc");
        layers.Add(new MotionShadowLayer(o));
        return this;
    }

    /// <summary>
    /// Rigs one drawing to bend: bones from its landmarks, and the drawing in a group with a skeleton deformation
    /// over it. <c>landmarks</c> is <c>Character.detect(image)</c> or <c>{ leftShoulder: [x, y], ... }</c>.
    /// <c>options</c>: <c>{ tl?, br?, poses?, turns?, move?, prefix?, subdivisions?, desc? }</c>.
    /// </summary>
    /// <remarks>
    /// The bones carry <c>Character.jointMap</c>'s body-part names under a <c>hips</c> root. Motion is given up front:
    /// <c>poses</c> as whole-pose keys, <c>[{ time, ease?, leftUpperArm: 40, ... }]</c>, or <c>turns</c> as a node per
    /// bone; <c>move</c> carries the whole figure.
    /// </remarks>
    public MotionRig RigFromDrawing(object? image, object? landmarks, object? options = null) =>
        MotionRigBuilder.Build(this, Root ?? throw new InvalidOperationException("This stack belongs to no composition."), image, landmarks, options);

    /// <summary>The composition this stack belongs to, whose bones it can use.</summary>
    internal MotionComposition? Root { get; private protected set; }

    /// <summary>The stack a group was added to; null for the composition itself.</summary>
    internal MotionLayerList? Parent { get; private protected set; }

    /// <summary>
    /// Where this stack's units go at a time in the composition's seconds, through every group above: its space, the time
    /// its layers see, the time of the stack it sits in, and whether it shows (a switch shows one layer; a pin with no map
    /// shows nothing).
    /// </summary>
    internal (MotionSpace Space, double Time, double OwnTime, bool Visible) SpaceAt(double rootTime)
    {
        if (this is not MotionGroup g || g.Parent is null) return (MotionSpace.Root, rootTime, rootTime, true);
        var (ps, pt, pOwn, pv) = g.Parent.SpaceAt(rootTime);
        var own = g.Parent.TimeFor(g.Layer, pt);
        var (s, t) = g.Layer.Enter(ps, Root?.CameraAt(rootTime), own);
        var shown = g.Parent is not MotionGroup pg || pg.Layer.Shows(pOwn, g.Layer);
        return (s ?? ps, t, own, pv && shown && s is not null);
    }

    /// <summary>
    /// How much of this stack reaches the frame at a time: every group above's <c>amount</c>, multiplied, each read at the
    /// time its own stack sees, as <see cref="SpaceAt"/> reads their transforms. 1 at the root.
    /// </summary>
    internal double AmountAbove(double rootTime)
    {
        if (this is not MotionGroup g || g.Parent is null) return 1;
        var own = g.Parent.TimeFor(g.Layer, g.Parent.SpaceAt(rootTime).Time);
        return g.Layer.AmountAt(own) * g.Parent.AmountAbove(rootTime);
    }

    /// <summary>Whether a skeleton deformation sits here or in any group below.</summary>
    internal bool HasDeformationDeep =>
        layers.Any(l => l is MotionDeformationLayer || l is MotionLayer.GroupLayer g && g.Children.HasDeformationDeep);

    /// <summary>
    /// The layers in drawing order: each run of consecutive 3D groups sorted far to near by its anchor's camera depth, as
    /// After Effects sorts a run of 3D layers; ties, and everything 2D, keep the stack's order.
    /// </summary>
    internal List<MotionLayer> Ordered(MotionSpace space, MotionCameraFrame? camera, double time) =>
        Ordered(space, camera, Times(time));

    private List<MotionLayer> Ordered(MotionSpace space, MotionCameraFrame? camera, Func<MotionLayer, double> at)
    {
        if (camera is null || !layers.Any(l => l is MotionLayer.GroupLayer { Is3D: true })) return layers;
        var result = new List<MotionLayer>();
        var run = new List<MotionLayer.GroupLayer>();
        foreach (var layer in layers.Append(null))
        {
            if (layer is MotionLayer.GroupLayer { Is3D: true } g)
            {
                run.Add(g);
                continue;
            }
            result.AddRange(run.Select((r, i) => (r, i, d: r.AnchorDepth(space, camera, at(r))))
                .OrderByDescending(x => x.d).ThenBy(x => x.i).Select(x => (MotionLayer)x.r));
            run.Clear();
            if (layer is not null) result.Add(layer);
        }
        return result;
    }

    /// <summary>Adds a layer built elsewhere — the text layer's export builds its per-letter groups this way.</summary>
    internal void Add(MotionLayer layer) => layers.Add(layer);

    /// <summary>Every layer here and in the groups below.</summary>
    internal IEnumerable<MotionLayer> AllLayers() =>
        layers.SelectMany(l => l is MotionLayer.GroupLayer g ? g.Children.AllLayers().Prepend(l) : [l]);

    internal void RenderLayers(SkiaCanvas surface, double time, MotionLayer? only = null)
    {
        // A switch draws one of its layers and nothing else.
        if (only is not null)
        {
            only.Render(surface, time);
            return;
        }

        // A filter that does nothing at this time is passed over, and a stack whose filters all do nothing is drawn in
        // place: drawing it apart and back costs a frame-sized surface for no change.
        var canvas = surface.SkCanvas;
        var frame = Tangent(canvas.TotalMatrix, surface.Width / 2f, surface.Height / 2f);
        var at = Times(time);
        var ordered = Root?.Frame is { } camera ? Ordered(Root.Space, camera, at) : layers;
        var active = layers.OfType<IMotionStackFilter>().Where(f => !f.IsIdle(frame, at((MotionLayer)f))).ToHashSet();
        if (active.Count == 0)
        {
            foreach (var layer in ordered.Where(l => l is not IMotionStackFilter)) layer.Render(surface, at(layer));
            return;
        }

        // A stack filter (a deformation, a blur) works on what is below it in the stack, so the stack is drawn on a
        // surface of its own, in device pixels under the same transform, and each filter replaces that surface with
        // its result.
        // Padded by the widest filter's margin, so a blur near the frame's edge reads what lies beyond it.
        var (mx, my) = active.Select(f => f.Margin(frame, at((MotionLayer)f)))
            .Aggregate((X: 0, Y: 0), (a, b) => (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        var shift = SKMatrix.CreateTranslation(mx, my);
        var matrix = SKMatrix.Concat(shift, canvas.TotalMatrix);
        var filterMatrix = SKMatrix.Concat(shift, frame);
        var stack = Fresh(surface, matrix, mx, my);
        try
        {
            foreach (var layer in ordered)
            {
                if (layer is not IMotionStackFilter filter)
                {
                    layer.Render(stack, at(layer));
                    continue;
                }
                if (!active.Contains(filter)) continue;

                var filtered = filter.Apply(stack, filterMatrix, at(layer));
                stack.Dispose();
                stack = filtered;
                stack.SkCanvas.SetMatrix(matrix);
            }

            canvas.Save();
            canvas.ResetMatrix();
            canvas.DrawBitmap(stack.SkBitmap, -mx, -my);
            canvas.Restore();
        }
        finally
        {
            stack.Dispose();
        }

        static SkiaCanvas Fresh(SkiaCanvas like, SKMatrix matrix, int mx, int my)
        {
            var c = new SkiaCanvas(like.Width + 2 * mx, like.Height + 2 * my, like.SkBitmap.ColorType);
            c.SkCanvas.Clear(SKColors.Transparent);
            c.SkCanvas.SetMatrix(matrix);
            return c;
        }
    }

    /// <summary>
    /// The layers as <c>.sif</c>, a run of 3D groups in the order the camera sorts them. Synfig's order is fixed, so a run
    /// whose order changes during the film is refused, naming the two groups and when they cross.
    /// </summary>
    internal IEnumerable<XElement> LayersToSif(SifWriter sif)
    {
        if (Root?.HasCamera != true || !layers.Any(l => l is MotionLayer.GroupLayer { Is3D: true }))
            return [.. layers.Select(l => l.ToSif(sif))];

        List<MotionLayer>? first = null;
        for (var f = 0; f < Root.FrameCount; f++)
        {
            var t = f / Root.Fps;
            var (space, local, _, _) = SpaceAt(t);
            var order = Ordered(space, Root.CameraAt(t), local);
            if (first is null) first = order;
            else if (!order.SequenceEqual(first))
            {
                var i = Enumerable.Range(0, order.Count).First(k => !ReferenceEquals(order[k], first[k]));
                throw new InvalidOperationException(
                    $"The 3D groups {Name(first[i])} and {Name(order[i])} change places in depth at {MotionTypes.Format(t)} s, and a .sif "
                    + "holds one order for the whole film. Keep one in front of the other throughout (its z), or render the film as video.");
            }
        }
        return [.. first!.Select(l => l.ToSif(sif))];

        static string Name(MotionLayer l) => l.Desc is null ? "(a group with no desc)" : $"'{l.Desc}'";
    }

    /// <summary>
    /// A frame as a filter reads it: itself when affine; under a perspective (a 3D group, a pin), its tangent where the
    /// device point (<paramref name="x"/>, <paramref name="y"/>) falls — the plane's scale and direction there — so a blur
    /// or shadow inside works on the picture as the camera sees it, sized as at the frame's centre.
    /// </summary>
    /// <remarks>
    /// <b>A plane's matrix arrives homogeneous</b>: every term multiplied by the camera's w (about 2,467 for a 1920-wide
    /// frame at the default lens), which Skia divides out when it draws. Read as affine it is a 2,467× scale, so a 26 px
    /// blur in a plane asked for a 92,516 px margin and a 1080p frame failed to allocate. Divided out first, it is the
    /// matrix it draws as.
    /// </remarks>
    internal static SKMatrix Tangent(SKMatrix m, float x, float y)
    {
        if (m.Persp2 != 0 && m.Persp2 != 1)
        {
            var w = m.Persp2;
            m = new SKMatrix(m.ScaleX / w, m.SkewX / w, m.TransX / w, m.SkewY / w, m.ScaleY / w, m.TransY / w,
                m.Persp0 / w, m.Persp1 / w, 1);
        }
        if (Math.Abs(m.Persp0) < 1e-12 && Math.Abs(m.Persp1) < 1e-12) return m;
        if (!m.TryInvert(out var back)) return m;
        var at = back.MapPoint(x, y);
        var (p, px, py) = (m.MapPoint(at), m.MapPoint(at.X + 1, at.Y), m.MapPoint(at.X, at.Y + 1));
        var (ax, ay) = (px - p, py - p);
        return new SKMatrix(ax.X, ay.X, p.X - ax.X * at.X - ay.X * at.Y, ax.Y, ay.Y, p.Y - ax.Y * at.X - ay.Y * at.Y, 0, 0, 1);
    }

    internal IEnumerable<XElement> LayersToSvg(SvgAnimationWriter svg, MotionTimeMap map) =>
        [.. svg.Stack([.. layers.Select(l => (l.Blend, l.Desc, l.ToSvg(svg, map)))])];

    internal IEnumerable<MotionNode> Nodes() => layers.SelectMany(l => l.Nodes());

    /// <summary>The topmost layer named <paramref name="name"/>, as Synfig's switch picks it; none for an empty name.</summary>
    internal MotionLayer? Named(string name, string who)
    {
        if (name.Length == 0) return null;
        return layers.LastOrDefault(l => l.Desc == name) ?? throw new ArgumentException(
            $"{who} has no layer named '{name}'. " + (layers.Any(l => l.Desc is not null)
                ? $"Its layers are named: {string.Join(", ", layers.Where(l => l.Desc is not null).Select(l => $"'{l.Desc}'").Distinct())}."
                : "Name each layer added to it with desc."));
    }

    /// <summary>The layers as SVG, each in a group shown only while it is the one <paramref name="shown"/> picks.</summary>
    internal IEnumerable<XElement> SwitchedToSvg(SvgAnimationWriter svg, MotionTimeMap inner, Func<double, MotionLayer?> shown, string? desc)
    {
        if (HasBlendedLayers)
            throw new InvalidOperationException($"The switch{(desc is null ? "" : $" '{desc}'")} cannot be written as animated SVG while a layer in it "
                + "blends other than by compositing: SVG shows or hides its layers one by one, and a blend needs the others beneath it.");
        return [.. layers.Select(layer =>
        {
            var g = new XElement(SvgAnimationWriter.Svg + "g", layer.ToSvg(svg, inner));
            svg.Attribute(g, "display", t => ReferenceEquals(shown(t), layer) ? "inline" : "none");
            return g;
        })];
    }

    /// <summary>Whether a layer here blends other than by compositing, so the stack must be drawn on its own.</summary>
    internal bool HasBlendedLayers => layers.Any(l => !l.Blend.IsComposite);

    /// <summary>
    /// Whether any layer here at <paramref name="time"/> blends other than by compositing and changes something, so the
    /// stack must be drawn on its own: a wipe at completion 0 does not.
    /// </summary>
    internal bool HasBlendedLayersAt(double time)
    {
        if (!HasBlendedLayers) return false;
        var at = Times(time);
        return layers.Any(l => !l.Blend.IsComposite && !l.IsVoid(at(l)));
    }

    /// <summary>
    /// Whether any layer here or in a group below blends by a formula that can leave [0, 1] — a subtract, a divide — so the
    /// film must be drawn on a float surface. The Porter–Duff blends (behind, onto, alpha over) cannot, and stay on 8 bits:
    /// trailer3's one iris, shown for 16 frames, had put all 361 on a half-float surface.
    /// </summary>
    internal bool NeedsFloatDeep =>
        layers.Any(l => l.Blend.Native is null) || layers.OfType<MotionLayer.GroupLayer>().Any(g => g.Children.NeedsFloatDeep);
    #endregion
}

/// <summary>A group of layers under one transformation: what <c>composition.group(...)</c> returns.</summary>
/// <remarks>
/// Synfig's group (paste canvas). Its transformation is <c>offset · rotate(angle) · skew · scale</c>
/// about <c>origin</c>, and its children see time as <c>t × timeDilation + timeOffset</c> — which is
/// how one animation is reused at another moment or speed. Exposed to the JavaScript sandbox. See
/// <see cref="MotionLayerList"/>.
/// </remarks>
public sealed class MotionGroup : MotionLayerList
{
    #region Constructors
    internal MotionGroup(MotionOptions o, MotionSwitchTrack? active = null, MotionPin? pin = null) => Layer = new MotionLayer.GroupLayer(o, this, active, pin);
    #endregion

    #region Properties
    internal MotionLayer.GroupLayer Layer { get; }
    #endregion
}

/// <summary>
/// A whole animation as a graph of layers and value nodes: <c>Motion.composition(...)</c>.
/// </summary>
/// <remarks>
/// <b>Spike.</b> Synfig's animation model with Skia drawing it: every parameter is a function of time,
/// so any frame is rendered directly, in any order, and the same graph is written out as a <c>.sif</c>
/// file Synfig's own renderer reads. That second path is the check on the first, not the delivery
/// route. Exposed to the JavaScript sandbox. See <see cref="MotionLayerList"/>.
/// </remarks>
public sealed class MotionComposition : MotionLayerList
{
    #region Constructors
    internal MotionComposition(object? options, MotionToolkit? motion, string? projectRoot)
    {
        this.motion = motion;
        this.projectRoot = projectRoot;
        Root = this;

        var o = new MotionOptions(options, "Motion.composition", "width", "height", "fps", "duration", "view");
        Width = (int)o.Number("width", 480);
        Height = (int)o.Number("height", 270);
        Fps = o.Number("fps", 24);
        Duration = o.Number("duration", 2);

        if (Width < 1 || Height < 1 || Width > 8192 || Height > 8192)
            throw new ArgumentException($"Motion.composition: {Width}x{Height} is not a size between 1 and 8192.");
        if (Fps <= 0 || Fps > 240) throw new ArgumentException($"Motion.composition: fps {Fps} is not between 0 and 240.");
        if (Duration < 0) throw new ArgumentException("Motion.composition: duration cannot be negative.");

        view = o.Has("view")
            ? (o.Raw("view") as IList is { Count: 4 } v
                ? [.. v.Cast<object?>().Select((x, i) => MotionTypes.Read(x, MotionType.Real, $"view[{i}]")[0])]
                : throw new ArgumentException("Motion.composition: view is [left, top, right, bottom] in the units layers use."))
            : [0, 0, Width, Height];

        if (view[2] == view[0] || view[3] == view[1])
            throw new ArgumentException("Motion.composition: view has no width or no height.");
    }
    #endregion

    #region Fields
    /// <summary>
    /// How near a whole frame an end time must land, in frames, to count as that frame. A time written to three places
    /// for the last frame of a 24 fps film — the manual's own <c>{ to: 14.958 }</c>, frame 359 at 14.9583 s — is
    /// 0.008 frames short of it, and flooring lost the frame (productad1 encoded 119 of 120 per part). A twentieth
    /// still leaves a <c>.98</c> end well clear of the next frame, so parts ending there do not overlap at a join.
    /// </summary>
    private const double FrameSnap = 0.05;

    private readonly MotionToolkit? motion;
    private readonly string? projectRoot;
    private readonly double[] view;
    private readonly List<MotionBone> bones = [];
    #endregion

    #region Properties
    public int Width { get; }

    public int Height { get; }

    /// <summary>Frames per second.</summary>
    public double Fps { get; }

    /// <summary>Length in seconds.</summary>
    public double Duration { get; }

    /// <summary>How many frames <see cref="Capture"/> renders: one per frame, both ends included.</summary>
    public int FrameCount => (int)Math.Floor(Duration * Fps + 1e-9) + 1;

    /// <summary>Whether y grows down the screen in the units layers use: the view's top is above its bottom.</summary>
    internal bool YDown => view[3] > view[1];

    /// <summary>The frame in the composition's units: its view, with left below right and top below bottom.</summary>
    internal SKRect ViewRect => new((float)Math.Min(view[0], view[2]), (float)Math.Min(view[1], view[3]),
        (float)Math.Max(view[0], view[2]), (float)Math.Max(view[1], view[3]));

    /// <summary>Every text added anywhere in this composition, groups included, in the order added.</summary>
    internal List<IShownText> TextHandles { get; } = [];
    #endregion

    #region Methods
    /// <summary>
    /// Every text the film shows and when: <c>[{ text, desc, from, to, seconds, intervals }]</c> in the order the texts were
    /// added. <c>intervals</c> are the spans of film seconds in which any of its letters is visible on the frame — every
    /// group above, its clock, switch and <c>amount</c> counted — sampled twelve times a second; <c>from</c> and <c>to</c> are
    /// the first and last of them, <c>seconds</c> their total. A text never visible has no intervals and null times.
    /// </summary>
    /// <remarks>What the film sets, read from the composition itself: the ground truth a read-back of the encoded film is
    /// compared with (<c>Verify.film</c>), and a list a script need not keep by hand.</remarks>
    public object?[] Texts()
    {
        var step = Math.Max(1, (int)Math.Round(Fps / 12));
        var frames = Enumerable.Range(0, FrameCount).Where(f => f % step == 0 || f == FrameCount - 1).ToArray();
        return [.. TextHandles.SelectMany(t => t.Counts ? Held(t) : [Item(t, t.Text, Spans(t, frames), step / Fps)])];

        // A counting number by the values it holds on screen for at least half a second, each its own item; read every
        // frame, since its string can change on any of them.
        IEnumerable<object?> Held(IShownText t)
        {
            var shown = Enumerable.Range(0, FrameCount).Select(f => t.VisibleOnFrame(f / Fps) ? t.TextAt(f / Fps) : null).ToArray();
            if (shown.All(s => s is null)) yield return Item(t, t.Text, [], 1 / Fps);   // never shown, as a text is reported
            for (var f = 0; f < shown.Length;)
            {
                var g = f;
                while (g + 1 < shown.Length && shown[g + 1] == shown[f]) g++;
                if (shown[f] is { } s && (g - f + 1) / Fps >= HeldSeconds - 1e-9) yield return Item(t, s, [[f / Fps, g / Fps]], 1 / Fps);
                f = g + 1;
            }
        }
    }

    /// <summary>How long a counting number must show a value for <see cref="Texts"/> to list it.</summary>
    internal const double HeldSeconds = 0.5;

    /// <summary>Each counting number while it counts on the frame: a reading in its format and range then is not a stray.</summary>
    internal MotionNumber.Count[] Counting() =>
        [.. TextHandles.OfType<MotionNumber>().Select(n => n.Counting()).OfType<MotionNumber.Count>()];

    private double[][] Spans(IShownText t, int[] frames)
    {
        var spans = new List<double[]>();
        double? start = null, last = null;
        foreach (var f in frames)
        {
            var time = f / Fps;
            if (t.VisibleOnFrame(time)) { start ??= time; last = time; }
            else if (start is not null) { spans.Add([start.Value, last!.Value]); start = null; }
        }
        if (start is not null) spans.Add([start.Value, last!.Value]);
        return [.. spans];
    }

    private static object? Item(IShownText t, string text, double[][] intervals, double step) => new Dictionary<string, object?>
    {
        ["text"] = text,
        ["kind"] = t.Kind,
        ["desc"] = t.Desc,
        ["from"] = intervals.Length == 0 ? null : intervals[0][0],
        ["to"] = intervals.Length == 0 ? null : intervals[^1][1],
        ["seconds"] = Math.Round(intervals.Sum(i => i[1] - i[0] + step), 3),
        ["intervals"] = intervals.Select(i => (object?)i).ToArray(),
    };

    /// <summary>
    /// A bone of the composition's skeleton:
    /// <c>{ name?, parent?, from, to, turn?, stretch?, width?, tipwidth? }</c>, or Synfig's own fields
    /// <c>{ name?, parent?, origin, angle, length?, scalelx?, scalex?, width?, tipwidth? }</c>.
    /// </summary>
    /// <remarks>
    /// <c>from</c> and <c>to</c> are the joints in the rest pose, in composition coordinates; <c>turn</c>
    /// (degrees) rotates the bone about <c>from</c> relative to its parent, and <c>stretch</c> lengthens it,
    /// carrying its children along. Synfig's fields are in the parent's frame instead: <c>[parent's length, 0]</c>
    /// is the parent's tip. A layer or point bound to the bone follows it from the rest pose.
    /// </remarks>
    public MotionBone Bone(object? options)
    {
        var bone = new MotionBone(options, bones.Count, bones);
        if (bones.Any(b => b.Name == bone.Name)) throw new ArgumentException($"There is already a bone named '{bone.Name}'.");
        bones.Add(bone);
        return bone;
    }

    /// <summary>The composition's bones, in the order they were made.</summary>
    public IReadOnlyList<MotionBone> Bones => bones;

    /// <summary>
    /// A layer drawing every bone as a capsule from origin to tip, so a rig can be seen:
    /// <c>{ color?, amount?, blend?, desc? }</c>. It draws the bones there are when it renders.
    /// </summary>
    public MotionLayerList Skeleton(object? options = null)
    {
        var o = new MotionOptions(options, "skeleton", "color", "amount", "blend", "desc");
        layers.Add(new MotionLayer.SkeletonLayer(o, bones));
        return this;
    }

    /// <summary>
    /// The composition's camera: <c>{ target?, targetZ?, distance?, orbit?, tilt?, roll?, zoom? | focalLength? }</c>. Groups
    /// given <c>z</c>, <c>rotateX</c> or <c>rotateY</c> become planes it sees in perspective. It looks at <c>target</c> (the
    /// frame's centre) from <c>distance</c> (the lens's own, which shows z = 0 at 100%), swung <c>orbit</c> degrees round
    /// the vertical and raised <c>tilt</c> degrees, rolled <c>roll</c>; <c>zoom</c> is the lens in units, or
    /// <c>focalLength</c> in millimetres on 36 mm film (50). Every option keys. Returns the composition.
    /// </summary>
    public MotionComposition Camera(object? options = null)
    {
        if (camera is not null) throw new InvalidOperationException("This composition has a camera already; key its options rather than adding another.");
        camera = new MotionCamera(new MotionOptions(options, "camera",
            "target", "targetZ", "distance", "orbit", "tilt", "roll", "zoom", "focalLength"), View);
        return this;
    }

    internal bool HasCamera => camera is not null;

    internal MotionCameraFrame? CameraAt(double time) => camera?.At(time);

    /// <summary>The camera's target, its depth and the camera's distance from it at a time; null with no camera.</summary>
    internal (double X, double Y, double Z, double Distance)? CameraTargetAt(double time) => camera?.TargetAt(time);

    /// <summary>The camera at the frame being drawn, null with no camera.</summary>
    internal MotionCameraFrame? Frame { get; private set; }

    /// <summary>The space of the stack being drawn: pushed by each group as it draws its layers.</summary>
    internal MotionSpace Space => spaces.Count > 0 ? spaces.Peek() : MotionSpace.Root;

    internal void PushSpace(MotionSpace space) => spaces.Push(space);

    internal void PopSpace() => spaces.Pop();

    private void BeginFrame(double time)
    {
        spaces.Clear();
        Frame = camera?.At(time);
    }

    private MotionCamera? camera;
    private readonly Stack<MotionSpace> spaces = new();

    /// <summary>The composition at a time in seconds, on a new canvas.</summary>
    public SkiaCanvas Render(double time) => Render(time, 1);

    /// <summary>The composition at a time, drawn <paramref name="scale"/> times its size — a whole-film check at a fraction of the cost.</summary>
    internal SkiaCanvas Render(double time, double scale)
    {
        var (w, h) = (Math.Max(1, (int)Math.Round(Width * scale)), Math.Max(1, (int)Math.Round(Height * scale)));
        var frame = SKMatrix.Concat(SKMatrix.CreateScale((float)scale, (float)scale), ViewMatrix());
        var canvas = new SkiaCanvas(w, h);
        canvas.SkCanvas.Clear(SKColors.Transparent);
        BeginFrame(time);
        if (!NeedsFloatDeep)
        {
            canvas.SkCanvas.Save();
            canvas.SkCanvas.Concat(frame);
            RenderLayers(canvas, time);
            canvas.SkCanvas.Restore();
            return canvas;
        }

        // Synfig's surfaces are float, so a subtract can go below zero and a divide above one and carry
        // that into the next layer. Blend on a float surface and clamp once, at the end.
        using var working = new SkiaCanvas(w, h, SKColorType.RgbaF16);
        working.SkCanvas.Clear(SKColors.Transparent);
        working.SkCanvas.Concat(frame);
        RenderLayers(working, time);
        canvas.SkCanvas.DrawBitmap(working.SkBitmap, 0, 0);
        return canvas;
    }

    /// <summary>
    /// Draws the composition at a time onto an existing context, under its current transform, so an
    /// animated passage sits inside a drawing made with the rest of the SDK.
    /// </summary>
    public void Draw(CanvasRenderingContext2D ctx, double time)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        BeginFrame(time);
        var c = ctx.Canvas.SkCanvas;
        c.Save();
        // The composition's own frame and nothing outside it: a fill paints everything it can reach, so unclipped, one
        // frame drawn onto a sheet erased every frame drawn before it.
        c.ClipRect(new SKRect(0, 0, Width, Height));
        c.Concat(ViewMatrix());

        // A blend reaches only the composition's own layers, never the drawing it is placed in.
        var isolate = HasBlendedLayersAt(time);
        if (isolate) c.SaveLayer();
        RenderLayers(ctx.Canvas, time);
        if (isolate) c.Restore();
        c.Restore();
    }

    /// <summary>
    /// WCAG 2.3.1, three flashes or below, measured over the film: <c>{ passed, generalFlashes, redFlashes, worstAt,
    /// transitions, frames, width }</c>. <c>options</c>: <c>{ from?, to?, width?, background? }</c>; every frame is drawn
    /// <c>width</c> pixels wide (160) over <c>background</c> (white, as the video), so a 15 s film takes a few seconds.
    /// </summary>
    public Dictionary<string, object?> Flashes(object? options = null) => MotionFlashes.Measure(this, options);

    /// <summary>
    /// Where the film shows nothing: <c>{ passed, stretches, blankSeconds, longestAllowed, frames, width }</c>, each stretch
    /// <c>{ from, to, seconds }</c>. A frame is blank when almost every pixel is its most common colour; <c>passed</c> is no
    /// stretch longer than <c>longest</c> seconds (1). <c>options</c>: <c>{ from?, to?, longest?, width?, fps?, background? }</c>,
    /// frames drawn <c>width</c> wide (320), <c>fps</c> a second (6), over <c>background</c> (white, as the video).
    /// </summary>
    public Dictionary<string, object?> Blank(object? options = null) => MotionBlank.Measure(this, options);

    /// <summary>
    /// Renders every frame into <c>Motion</c>'s frame buffer, ready for <c>Motion.sheet</c> or
    /// <c>Motion.save</c>. Returns how many it added. <c>options</c>: <c>{ fps?, from?, to? }</c>, in
    /// seconds; a lower <c>fps</c> samples the same timeline more sparsely.
    /// </summary>
    public int Capture(object? options = null)
    {
        if (motion is null) throw new InvalidOperationException("This composition has no Motion frame buffer to capture into.");
        var o = new MotionOptions(options, "composition.capture", "fps", "from", "to");
        var fps = o.Number("fps", Fps);
        var from = o.Number("from", 0);
        var to = o.Number("to", Duration);
        if (fps <= 0) throw new ArgumentException("composition.capture: fps must be positive.");

        var count = 0;
        for (var i = 0; ; i++)
        {
            var t = from + i / fps;
            if (t > to + FrameSnap / fps) break;
            using var frame = Render(t);
            motion.FrameAt(frame, (int)Math.Round(t * Fps), t);
            count++;
        }

        return count;
    }

    /// <summary>
    /// The composition as an animated SVG: SMIL animation on ordinary elements, playing in a browser and
    /// in an <c>&lt;img&gt;</c>. <c>options</c>: <c>{ loop?, fps? }</c> — <c>loop</c> repeats instead of
    /// holding the last frame; <c>fps</c> is the rate for values that cannot be written exactly.
    /// </summary>
    public string ToSvg(object? options = null)
    {
        var o = new MotionOptions(options, "composition.toSvg", "loop", "fps");
        var fps = o.Number("fps", Fps);
        if (fps <= 0 || fps > 240) throw new ArgumentException($"composition.toSvg: fps {fps} is not between 0 and 240.");
        return new SvgAnimationWriter(this, o.Bool("loop", false), fps).Write();
    }

    /// <summary>Writes the animated SVG into the project and returns the path written.</summary>
    public string SaveSvg(string filePath, object? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var svg = ToSvg(options);
        var full = ProjectPath.Resolve(projectRoot, filePath, nameof(filePath), "Write to");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new UTF8Encoding(false).GetBytes(svg);
        File.WriteAllBytes(full, bytes);

        motion?.Record("render", full, new()
        {
            ["motion"] = "svg",
            ["format"] = "svg",
            ["bytes"] = (long)bytes.Length,
            ["durationMs"] = Duration * 1000,
            ["width"] = Width,
            ["height"] = Height
        });
        return full;
    }

    /// <summary>
    /// Renders the composition straight into a video with sound, an MP4 (H.264, AAC) or a WebM (VP9, Opus) by the path's
    /// extension: <c>{ fps?, from?, to?, quality?, background?, audio? }</c>.
    /// </summary>
    /// <remarks>
    /// Frames go to the encoder one at a time and are not held, so the film is not bounded by <c>Motion</c>'s frame
    /// buffer. <c>audio</c> is a path, a line of speech timed by a recording (it plays from the line's <c>start</c>),
    /// <c>{ path, start }</c>, or a list of these, mixed; starts are composition times, so <c>from</c> moves the sound
    /// with the picture. Video has no alpha, so frames are drawn over <c>background</c>, white by default.
    /// </remarks>
    public Dictionary<string, object?> SaveVideo(string filePath, object? options = null)
    {
        var o = new MotionOptions(options, "comp.saveVideo", "fps", "from", "to", "quality", "background", "audio");
        var fps = o.Number("fps", Fps);
        var from = o.Number("from", 0);
        var to = o.Number("to", Duration);
        if (fps <= 0) throw new ArgumentException("comp.saveVideo: fps must be positive.");
        if (to < from) throw new ArgumentException($"comp.saveVideo: to ({to}) is before from ({from}).");

        // Both ends included, as capture does, with an end within FrameSnap of a frame counted as that frame.
        var count = (int)Math.Floor((to - from) * fps + FrameSnap) + 1;
        IEnumerable<SKBitmap> Frames()
        {
            for (var i = 0; i < count; i++)
            {
                using var frame = Render(from + i / fps);
                yield return frame.SkBitmap;
            }
        }

        return MotionVideo.Save(filePath, Frames(), count, Width, Height, fps, from, o, projectRoot, motion);
    }

    /// <summary>
    /// The composition as Synfig's <c>.sif</c> XML. A composition with an image layer is refused: its
    /// pictures have to be written beside the file, which <see cref="SaveSif"/> does.
    /// </summary>
    public string ToSif() => new SifWriter(this, null).Write();

    /// <summary>
    /// Writes the <c>.sif</c> into the project and returns the path written. Each image layer's picture is
    /// written beside it as <c>&lt;name&gt;-image&lt;n&gt;.png</c>, where Synfig looks for it.
    /// </summary>
    public string SaveSif(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var full = ProjectPath.Resolve(projectRoot, filePath, nameof(filePath), "Write to");
        var (bytes, pictures) = WriteSif(full);

        // Not a render: nothing in a browser plays a .sif. Recorded as an export so the file the run
        // left behind is still accounted for, pictures included.
        motion?.Record("export", full, new()
        {
            ["motion"] = "sif",
            ["format"] = "sif",
            ["bytes"] = bytes,
            ["pictures"] = pictures.Select(p => (object?)p).ToList()
        });
        return full;
    }

    /// <summary>
    /// Writes the <c>.sif</c> and its pictures to a path already resolved — the project's, for <see cref="SaveSif"/>, or
    /// the one <c>polson eval --sif</c> was given. Returns the bytes written and the pictures' paths.
    /// </summary>
    internal (long Bytes, string[] Pictures) WriteSif(string full)
    {
        var dir = Path.GetDirectoryName(full)!;
        var writer = new SifWriter(this, Path.GetFileNameWithoutExtension(full));
        var xml = writer.Write();
        Directory.CreateDirectory(dir);
        foreach (var (name, png) in writer.Pictures) File.WriteAllBytes(Path.Combine(dir, name), png);
        var bytes = new UTF8Encoding(false).GetBytes(xml);
        File.WriteAllBytes(full, bytes);
        return (bytes.Length, [.. writer.Pictures.Select(p => Path.Combine(dir, p.Name))]);
    }

    internal double[] View => view;

    /// <summary>Every node the composition holds, the bones' included.</summary>
    internal IEnumerable<MotionNode> AllNodes() => bones.SelectMany(b => b.Nodes).Concat(Nodes());

    internal SKMatrix ViewMatrix()
    {
        var sx = Width / (view[2] - view[0]);
        var sy = Height / (view[3] - view[1]);
        return new SKMatrix((float)sx, 0, (float)(-view[0] * sx), 0, (float)sy, (float)(-view[1] * sy), 0, 0, 1);
    }
    #endregion
}

/// <summary>
/// A layer that works on what is below it in its stack — Synfig's <c>Layer_CompositeFork</c> family: the skeleton
/// deformation, the blur. Its stack draws everything before it onto a surface of its own and hands it over.
/// </summary>
/// <remarks>Like Synfig's, it stops at its own group: a group's stack is all it is given.</remarks>
internal interface IMotionStackFilter
{
    /// <summary>
    /// Takes <paramref name="below"/> — the stack so far, in device pixels, drawn under <paramref name="matrix"/> — and
    /// returns a new surface to replace it. The caller disposes <paramref name="below"/>.
    /// </summary>
    SkiaCanvas Apply(SkiaCanvas below, SKMatrix matrix, double time);

    /// <summary>How far beyond the frame, in device pixels, the stack must be drawn for this filter to read it.</summary>
    (int X, int Y) Margin(SKMatrix matrix, double time) => (0, 0);

    /// <summary>
    /// Whether the filter changes nothing at this time — its amount is 0, or a blur's size is — so the stack need not be
    /// drawn apart for it. trailer2's blur, keyed off after its focus pull, still cost 12 ms a frame for the rest of the film.
    /// </summary>
    bool IsIdle(SKMatrix matrix, double time) => false;
}

/// <summary>One layer of a composition, its parameters held as nodes.</summary>
internal abstract class MotionLayer
{
    #region Constructors
    protected MotionLayer(MotionOptions o)
    {
        Desc = o.Has("desc") ? o.Raw("desc")?.ToString() : null;
        Amount = o.Node("amount", MotionType.Real, 1d);
        Blend = MotionBlend.Parse(o.Raw("blend"), o.Who);
    }
    #endregion

    #region Properties
    public string? Desc { get; }

    public MotionNode Amount { get; }

    public MotionBlend Blend { get; }
    #endregion

    #region Methods
    /// <summary>
    /// Draws the layer at a time. A composited layer is drawn straight onto the canvas at its
    /// <c>amount</c>; any other blend draws it at full strength into a layer of its own and lets the
    /// blender combine that with the backdrop, <c>amount</c> and all, as Synfig's formula does.
    /// </summary>
    public void Render(SkiaCanvas surface, double time)
    {
        if (IsVoid(time)) return;
        var amount = AmountAt(time);
        if (Blend.IsComposite)
        {
            if (amount > 0) Draw(surface, time, amount);
            return;
        }

        var canvas = surface.SkCanvas;
        if (Blend.Native is { } mode)
        {
            using var native = new SKPaint { BlendMode = mode, ColorF = new SKColorF(1, 1, 1, (float)amount) };
            canvas.SaveLayer(native);
            Draw(surface, time, 1);
            canvas.Restore();
            return;
        }

        using var blender = Blend.Blender(amount);
        using var paint = new SKPaint { Blender = blender };
        canvas.SaveLayer(paint);
        Draw(surface, time, 1);
        canvas.Restore();
    }

    /// <summary>
    /// Whether the layer changes nothing at this time, so it is passed over — no layer of its own, and no isolation of its
    /// group for it. An iris at completion 0 still cost trailer3 25 ms a frame before this.
    /// </summary>
    internal virtual bool IsVoid(double time) => false;

    /// <summary>Draws the layer's content at a time, at <paramref name="amount"/> opacity.</summary>
    protected abstract void Draw(SkiaCanvas surface, double time, double amount);

    public abstract XElement ToSif(SifWriter sif);

    public abstract XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map);

    public abstract IEnumerable<MotionNode> Nodes();

    /// <summary>The layer's <c>amount</c> as SVG <c>opacity</c>, omitted while it is 1.</summary>
    protected void Opacity(XElement element, SvgAnimationWriter svg, MotionTimeMap map)
    {
        var track = svg.Track(Amount, 0, map);
        if (track.Values.Any(v => v < 0 || v > 1)) track = svg.Track(Amount, 0, map, v => Math.Clamp(v, 0, 1));
        if (track.IsStatic && Math.Abs(track.Values[0] - 1) < 1e-12) return;
        svg.Attribute(element, "opacity", track);
    }

    /// <summary>A colour as <c>fill</c> or <c>stroke</c> plus its opacity: the RGB sampled, the alpha exact where it can be.</summary>
    protected static void Paint(XElement element, string attribute, MotionNode color, SvgAnimationWriter svg, MotionTimeMap map)
    {
        svg.Attribute(element, attribute, t =>
        {
            var c = MotionTypes.ToSkColor(color.Evaluate(map.Local(t)));
            return $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
        });

        var alpha = svg.Track(color, 3, map);
        if (alpha.IsStatic && Math.Abs(alpha.Values[0] - 1) < 1e-12) return;
        svg.Attribute(element, attribute + "-opacity", alpha);
    }

    protected XElement SvgElement(string name, params object?[] content) =>
        new(SvgAnimationWriter.Svg + name, Desc is null ? null : new XAttribute("data-desc", Desc), content);

    protected XElement Element(string type, string version, SifWriter sif, params object?[] content) =>
        new("layer", new XAttribute("type", type), new XAttribute("active", "true"), new XAttribute("version", version),
            Desc is null ? null : new XAttribute("desc", Desc),
            SifWriter.Param("z_depth", new XElement("real", new XAttribute("value", "0"))),
            sif.Param("amount", Amount),
            SifWriter.Param("blend_method", new XElement("integer", new XAttribute("value", SifBlendMethod))),
            content);

    /// <summary>The <c>blend_method</c> written: the layer's blend, unless the layer composites some other way.</summary>
    protected virtual int SifBlendMethod => Blend.Synfig;

    protected internal double AmountAt(double time) => Math.Clamp(Amount.Evaluate(time)[0], 0, 1);

    protected static string P(SKPoint p) => $"{SvgAnimationWriter.N(p.X)} {SvgAnimationWriter.N(p.Y)}";

    // An affine map keeps a track exact: a keySpline shapes progress between two values, not the values.
    protected static MotionTrack Affine(MotionTrack t, double k, double c) => t with { Values = [.. t.Values.Select(v => k * v + c)] };

    protected static SKPaint Paint(double[] color, double amount) =>
        new() { Color = MotionTypes.ToSkColor(color, amount), IsAntialias = true, Style = SKPaintStyle.Fill };
    #endregion

    #region Types
    public sealed class SolidColor(MotionOptions o) : MotionLayer(o)
    {
        private readonly MotionNode color = o.Node("color", MotionType.Color, "#000000");

        protected override void Draw(SkiaCanvas surface, double time, double amount)
        {
            using var paint = Paint(color.Evaluate(time), amount);
            surface.SkCanvas.DrawPaint(paint);
        }

        public override XElement ToSif(SifWriter sif) => Element("solid_color", "0.1", sif, sif.Param("color", color));

        public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
        {
            // At the root a fill covers the view; inside a group it must survive the group's transform.
            var v = svg.View;
            var (x, y, w, h) = svg.Depth == 0
                ? (Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Abs(v[2] - v[0]), Math.Abs(v[3] - v[1]))
                : (-100000d, -100000d, 200000d, 200000d);
            var rect = SvgElement("rect", new XAttribute("x", SvgAnimationWriter.N(x)), new XAttribute("y", SvgAnimationWriter.N(y)),
                new XAttribute("width", SvgAnimationWriter.N(w)), new XAttribute("height", SvgAnimationWriter.N(h)));
            Paint(rect, "fill", color, svg, map);
            Opacity(rect, svg, map);
            return rect;
        }

        public override IEnumerable<MotionNode> Nodes() => [Amount, color];
    }

    public sealed class CircleLayer(MotionOptions o) : MotionLayer(o)
    {
        private readonly MotionNode origin = o.Node("origin", MotionType.Vector, new[] { 0d, 0d });
        private readonly MotionNode radius = o.Node("radius", MotionType.Real, 10d);
        private readonly MotionNode color = o.Node("color", MotionType.Color, "#000000");
        private readonly bool invert = o.Bool("invert", false);

        protected override void Draw(SkiaCanvas surface, double time, double amount)
        {
            var p = origin.Evaluate(time);
            using var paint = Paint(color.Evaluate(time), amount);
            using var path = new SKPath { FillType = invert ? SKPathFillType.InverseWinding : SKPathFillType.Winding };
            path.AddCircle((float)p[0], (float)p[1], (float)Math.Abs(radius.Evaluate(time)[0]));
            surface.SkCanvas.DrawPath(path, paint);
        }

        public override XElement ToSif(SifWriter sif) => Element("circle", "0.2", sif,
            sif.Param("color", color), sif.Param("radius", radius),
            SifWriter.Param("feather", new XElement("real", new XAttribute("value", "0"))),
            sif.Param("origin", origin),
            SifWriter.Param("invert", SifWriter.Bool(invert)));

        public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
        {
            if (invert)
            {
                // An inverted disc has no SVG element of its own: everything, less a circle drawn as two arcs.
                var path = SvgElement("path", new XAttribute("fill-rule", "evenodd"));
                svg.Attribute(path, "d", t =>
                {
                    var local = map.Local(t);
                    var p = origin.Evaluate(local);
                    var r = Math.Abs(radius.Evaluate(local)[0]);
                    string N(double v) => SvgAnimationWriter.N(v);
                    return $"{SvgAnimationWriter.Everything}M{N(p[0] - r)} {N(p[1])}A{N(r)} {N(r)} 0 1 0 {N(p[0] + r)} {N(p[1])}A{N(r)} {N(r)} 0 1 0 {N(p[0] - r)} {N(p[1])}Z";
                });
                Paint(path, "fill", color, svg, map);
                Opacity(path, svg, map);
                return path;
            }

            var circle = SvgElement("circle");
            svg.Attribute(circle, "cx", svg.Track(origin, 0, map));
            svg.Attribute(circle, "cy", svg.Track(origin, 1, map));
            var r = svg.Track(radius, 0, map);
            if (r.Values.Any(v => v < 0)) r = svg.Track(radius, 0, map, Math.Abs);
            svg.Attribute(circle, "r", r);
            Paint(circle, "fill", color, svg, map);
            Opacity(circle, svg, map);
            return circle;
        }

        public override IEnumerable<MotionNode> Nodes() => [Amount, origin, radius, color];
    }

    public sealed class RectangleLayer(MotionOptions o) : MotionLayer(o)
    {
        private readonly MotionNode point1 = o.Node("point1", MotionType.Vector, new[] { 0d, 0d });
        private readonly MotionNode point2 = o.Node("point2", MotionType.Vector, new[] { 10d, 10d });
        private readonly MotionNode expand = o.Node("expand", MotionType.Real, 0d);
        private readonly MotionNode radius = o.Node("radius", MotionType.Real, 0d);
        private readonly MotionNode color = o.Node("color", MotionType.Color, "#000000");
        private readonly bool invert = o.Bool("invert", false);

        public override IEnumerable<MotionNode> Nodes() => [Amount, point1, point2, expand, radius, color];

        protected override void Draw(SkiaCanvas surface, double time, double amount)
        {
            using var paint = Paint(color.Evaluate(time), amount);
            using var path = new SKPath { FillType = invert ? SKPathFillType.InverseWinding : SKPathFillType.Winding };
            var r = Bounds(time);
            var b = (float)Bevel(time);
            if (b <= 0) path.AddRect(r);
            else
            {
                // Synfig's rounded rectangle: each corner a quadratic whose control point is the corner, so a quarter of the
                // radius short of a circular arc at the corner's middle (about 4% of the radius inside SVG's rx).
                path.MoveTo(r.Right - b, r.Top);
                path.QuadTo(r.Right, r.Top, r.Right, r.Top + b);
                path.LineTo(r.Right, r.Bottom - b);
                path.QuadTo(r.Right, r.Bottom, r.Right - b, r.Bottom);
                path.LineTo(r.Left + b, r.Bottom);
                path.QuadTo(r.Left, r.Bottom, r.Left, r.Bottom - b);
                path.LineTo(r.Left, r.Top + b);
                path.QuadTo(r.Left, r.Top, r.Left + b, r.Top);
                path.Close();
            }
            surface.SkCanvas.DrawPath(path, paint);
        }

        /// <summary>The outline as SVG path data at a time: four sides, or four sides with Synfig's quadratic corners.</summary>
        private string Outline(double time)
        {
            var r = Bounds(time);
            var b = (float)Bevel(time);
            static string N(double v) => SvgAnimationWriter.N(v);
            if (b <= 0) return $"M{N(r.Left)} {N(r.Top)}H{N(r.Right)}V{N(r.Bottom)}H{N(r.Left)}Z";
            return $"M{N(r.Right - b)} {N(r.Top)}Q{N(r.Right)} {N(r.Top)} {N(r.Right)} {N(r.Top + b)}V{N(r.Bottom - b)}"
                + $"Q{N(r.Right)} {N(r.Bottom)} {N(r.Right - b)} {N(r.Bottom)}H{N(r.Left + b)}"
                + $"Q{N(r.Left)} {N(r.Bottom)} {N(r.Left)} {N(r.Bottom - b)}V{N(r.Top + b)}"
                + $"Q{N(r.Left)} {N(r.Top)} {N(r.Left + b)} {N(r.Top)}Z";
        }

        /// <summary>The corners' radius now, at most half the shorter side: a pill at its largest.</summary>
        private double Bevel(double time)
        {
            var (_, w) = Corners(time, 0);
            var (_, h) = Corners(time, 1);
            return Math.Clamp(radius.Evaluate(time)[0], 0, Math.Min(w, h) / 2);
        }

        /// <summary>
        /// Synfig's <c>bevel</c> for the radius: a fraction of the shorter side, so that with <c>bevCircle</c> each corner is
        /// <c>min(w, h)·bevel/2</c> along both sides.
        /// </summary>
        private double SifBevel(double time)
        {
            var (_, w) = Corners(time, 0);
            var (_, h) = Corners(time, 1);
            var shorter = Math.Min(w, h);
            return shorter <= 0 ? 0 : Math.Clamp(2 * Bevel(time) / shorter, 0, 1);
        }

        private SKRect Bounds(double time)
        {
            var (x, w) = Corners(time, 0);
            var (y, h) = Corners(time, 1);
            return SKRect.Create((float)x, (float)y, (float)w, (float)h);
        }

        public override XElement ToSif(SifWriter sif) => Element("rectangle", "0.2", sif,
            sif.Param("color", color), sif.Param("point1", point1), sif.Param("point2", point2), sif.Param("expand", expand),
            SifWriter.Param("invert", SifWriter.Bool(invert)),
            SifWriter.Param("feather_x", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("feather_y", new XElement("real", new XAttribute("value", "0"))),
            BevelToSif(sif),
            SifWriter.Param("bevCircle", SifWriter.Bool(true)));

        /// <summary>The bevel: fixed when nothing it depends on moves, otherwise sampled a frame at a time and compacted.</summary>
        private XElement BevelToSif(SifWriter sif)
        {
            var count = (int)Math.Floor(sif.Duration * sif.Fps + 1e-9) + 1;
            var times = Enumerable.Range(0, count).Select(f => f / sif.Fps).ToArray();
            var bevel = MotionTextLayer.Keys(MotionType.Real, times, [.. times.Select(t => new[] { SifBevel(t) })]);
            return sif.Param("bevel", bevel);
        }

        public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
        {
            // Rounded corners go out as a path with the same quadratic corners as the render and Synfig, not as rx, whose
            // corners are circular arcs.
            var rounded = svg.Track(radius, 0, map) is not { IsStatic: true } still || still.Values[0] > 0;
            if (invert || rounded)
            {
                var path = invert ? SvgElement("path", new XAttribute("fill-rule", "evenodd")) : SvgElement("path");
                svg.Attribute(path, "d", t => (invert ? SvgAnimationWriter.Everything : "") + Outline(map.Local(t)));
                Paint(path, "fill", color, svg, map);
                Opacity(path, svg, map);
                return path;
            }

            var rect = SvgElement("rect");
            Side(svg, map, rect, 0, "x", "width");
            Side(svg, map, rect, 1, "y", "height");
            Paint(rect, "fill", color, svg, map);
            Opacity(rect, svg, map);
            return rect;
        }

        /// <summary>
        /// One axis: its start and its size. When one corner holds still and the other is keyed and never
        /// crosses it, both are affine in the keyed one and stay exact; otherwise both are sampled.
        /// </summary>
        private void Side(SvgAnimationWriter svg, MotionTimeMap map, XElement rect, int c, string start, string size)
        {
            var a = svg.Track(point1, c, map);
            var b = svg.Track(point2, c, map);
            var e = svg.Track(expand, 0, map);
            if (e.IsStatic && (a.IsStatic || b.IsStatic))
            {
                var (still, moving) = a.IsStatic ? (a.Values[0], b) : (b.Values[0], a);
                var ex = Math.Abs(e.Values[0]);
                if (moving.Values.All(v => v >= still))
                {
                    svg.Attribute(rect, start, Affine(a.IsStatic ? a : b, 1, -ex));
                    svg.Attribute(rect, size, Affine(moving, 1, 2 * ex - still));
                    return;
                }

                if (moving.Values.All(v => v <= still))
                {
                    svg.Attribute(rect, start, Affine(moving, 1, -ex));
                    svg.Attribute(rect, size, Affine(moving, -1, still + 2 * ex));
                    return;
                }
            }

            svg.Attribute(rect, start, svg.Track(t => Corners(map.Local(t), c).Min));
            svg.Attribute(rect, size, svg.Track(t => Corners(map.Local(t), c).Size));
        }

        private (double Min, double Size) Corners(double time, int c)
        {
            var a = point1.Evaluate(time)[c];
            var b = point2.Evaluate(time)[c];
            var e = Math.Abs(expand.Evaluate(time)[0]);
            return (Math.Min(a, b) - e, Math.Abs(b - a) + 2 * e);
        }

    }

    /// <summary>
    /// Synfig's import layer: a picture whose top-left corner is drawn at <c>tl</c> and bottom-right at
    /// <c>br</c>, so swapping a pair of coordinates mirrors it. The picture is held, never a filename: a
    /// script hands over pixels, and the <c>.sif</c> writer puts them in a file beside its own.
    /// </summary>
    public sealed class ImageLayer : MotionLayer
    {
        public ImageLayer(MotionOptions o) : base(o)
        {
            picture = MotionPicture.From(o.Raw("image"), o.Who);
            tl = o.Node("tl", MotionType.Vector, new[] { 0d, 0d });
            br = o.Node("br", MotionType.Vector, new double[] { picture.Width, picture.Height });
            var name = o.Has("interpolation") ? o.Raw("interpolation") as string
                ?? throw new ArgumentException($"{o.Who}'s interpolation is a name: nearest, linear or cubic.") : "linear";
            interpolation = name switch
            {
                "nearest" => 0,
                "linear" => 1,
                "cubic" => 3,
                _ => throw new ArgumentException($"{o.Who} has no interpolation '{name}'. It takes: nearest, linear, cubic.")
            };
        }

        private readonly MotionPicture picture;
        private readonly MotionNode tl, br;
        private readonly int interpolation;

        public override IEnumerable<MotionNode> Nodes() => [Amount, tl, br];

        internal MotionPicture Picture => picture;

        protected override void Draw(SkiaCanvas surface, double time, double amount)
        {
            var a = tl.Evaluate(time);
            var b = br.Evaluate(time);
            var sx = (b[0] - a[0]) / picture.Width;
            var sy = (b[1] - a[1]) / picture.Height;
            if (sx == 0 || sy == 0) return;

            var canvas = surface.SkCanvas;
            canvas.Save();
            canvas.Concat(new SKMatrix((float)sx, 0, (float)a[0], 0, (float)sy, (float)a[1], 0, 0, 1));
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(amount * 255)) };
            var sampling = interpolation switch
            {
                0 => new SKSamplingOptions(SKFilterMode.Nearest),
                3 => new SKSamplingOptions(SKCubicResampler.CatmullRom),
                _ => new SKSamplingOptions(SKFilterMode.Linear)
            };
            canvas.DrawImage(picture.Image, 0, 0, sampling, paint);
            canvas.Restore();
        }

        public override XElement ToSif(SifWriter sif) => Element("import", "0.1", sif,
            sif.Param("tl", tl), sif.Param("br", br),
            SifWriter.Param("c", new XElement("integer", new XAttribute("value", interpolation))),
            SifWriter.Param("gamma_adjust", new XElement("real", new XAttribute("value", "1"))),
            SifWriter.Param("filename", new XElement("string", sif.Picture(picture))),
            SifWriter.Param("time_offset", new XElement("time", new XAttribute("value", "0s"))));

        /// <summary>
        /// The picture at its own size, under a translation to <c>tl</c> and a scale to <c>br</c>: exact
        /// wherever one corner holds still, sampled when both move.
        /// </summary>
        public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
        {
            var image = SvgElement("image",
                new XAttribute("width", picture.Width), new XAttribute("height", picture.Height),
                new XAttribute("preserveAspectRatio", "none"),
                interpolation == 0 ? new XAttribute("image-rendering", "pixelated") : null,
                new XAttribute(SvgAnimationWriter.Xlink + "href", picture.DataUri));

            var chain = new[]
            {
                svg.Transform("translate", svg.Track(tl, 0, map), v => $"{SvgAnimationWriter.N(v)} 0", 0),
                svg.Transform("translate", svg.Track(tl, 1, map), v => $"0 {SvgAnimationWriter.N(v)}", 0),
                svg.Transform("scale", Scale(svg, map, 0, picture.Width), v => $"{SvgAnimationWriter.N(v, 6)} 1", 1),
                svg.Transform("scale", Scale(svg, map, 1, picture.Height), v => $"1 {SvgAnimationWriter.N(v, 6)}", 1),
            }.Where(g => g.HasAttributes || g.HasElements).ToArray();

            object inner = image;
            for (var i = chain.Length - 1; i >= 0; i--)
            {
                chain[i].Add(inner);
                inner = chain[i];
            }

            var outer = SvgElement("g", inner);
            Opacity(outer, svg, map);
            return outer;
        }

        private MotionTrack Scale(SvgAnimationWriter svg, MotionTimeMap map, int c, int size)
        {
            var a = svg.Track(tl, c, map);
            var b = svg.Track(br, c, map);
            if (a.IsStatic) return Affine(b, 1d / size, -a.Values[0] / size);
            if (b.IsStatic) return Affine(a, -1d / size, b.Values[0] / size);
            return svg.Track(t => (br.Evaluate(map.Local(t))[c] - tl.Evaluate(map.Local(t))[c]) / size);
        }
    }

    /// <summary>What region and outline share: a spline of points, possibly closed, moved by <c>origin</c>.</summary>
    public abstract class Shape : MotionLayer
    {
        protected Shape(MotionOptions o, string who, bool loopByDefault) : base(o)
        {
            Origin = o.Node("origin", MotionType.Vector, new[] { 0d, 0d });
            Color = o.Node("color", MotionType.Color, "#000000");
            Loop = o.Bool("loop", loopByDefault);
            Points = MotionSplinePoint.ReadAll(o.Raw("points"), who);
        }

        protected MotionNode Origin { get; }

        protected MotionNode Color { get; }

        protected bool Loop { get; }

        protected IReadOnlyList<MotionSplinePoint> Points { get; }

        /// <summary>Whether the shape covers everything but itself.</summary>
        protected virtual bool Inverted => false;

        public override IEnumerable<MotionNode> Nodes() =>
            new[] { Amount, Origin, Color }.Concat(Points.SelectMany(p => p.Nodes()));

        protected XElement[] ShapeParams(SifWriter sif) =>
        [
            sif.Param("color", Color),
            sif.Param("origin", Origin),
            SifWriter.Param("invert", SifWriter.Bool(Inverted)),
            SifWriter.Param("antialias", SifWriter.Bool(true)),
            SifWriter.Param("feather", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("blurtype", new XElement("integer", new XAttribute("value", "1"))),
            SifWriter.Param("winding_style", new XElement("integer", new XAttribute("value", "0"))),
            SifWriter.Param("bline", new XElement("bline", new XAttribute("type", "bline_point"),
                new XAttribute("loop", Loop ? "true" : "false"),
                Points.Select(p => new XElement("entry", p.ToSif(sif)))))
        ];

        /// <summary>The spline at a time, translated by origin, as Synfig's region builds it.</summary>
        protected SKPath Path(double time, out MotionSplinePoint.State[] states)
        {
            var o = Origin.Evaluate(time);
            states = [.. Points.Select(p => p.At(time, o))];
            var path = new SKPath();
            if (states.Length == 0) return path;

            path.MoveTo(states[0].P);
            for (var i = 1; i < states.Length; i++) Segment(path, states[i - 1], states[i]);
            if (Loop)
            {
                Segment(path, states[^1], states[0]);
                path.Close();
            }

            return path;
        }

        protected static void Segment(SKPath path, MotionSplinePoint.State a, MotionSplinePoint.State b)
        {
            if (a.T2 == SKPoint.Empty && b.T1 == SKPoint.Empty) path.LineTo(b.P);
            else path.CubicTo(a.P + Third(a.T2), b.P - Third(b.T1), b.P);
        }

        protected static SKPoint Third(SKPoint t) => new(t.X / 3f, t.Y / 3f);

        /// <summary>
        /// The spline as SVG path data with the same commands at every time — a cubic for every segment,
        /// even a straight one — so SMIL can interpolate one frame's <c>d</c> into the next.
        /// </summary>
        protected string PathData(double time)
        {
            var s = States(time);
            if (s.Length == 0) return "";

            var d = new StringBuilder($"M{P(s[0].P)}");
            for (var i = 1; i < s.Length; i++) Cubic(d, s[i - 1], s[i]);
            if (Loop)
            {
                Cubic(d, s[^1], s[0]);
                d.Append('Z');
            }

            return d.ToString();

            static void Cubic(StringBuilder d, MotionSplinePoint.State a, MotionSplinePoint.State b) =>
                d.Append($"C{P(a.P + Third(a.T2))} {P(b.P - Third(b.T1))} {P(b.P)}");
        }

        protected MotionSplinePoint.State[] States(double time)
        {
            var o = Origin.Evaluate(time);
            return [.. Points.Select(p => p.At(time, o))];
        }
    }

    public sealed class RegionLayer(MotionOptions o) : Shape(o, "region", true)
    {
        private readonly bool invert = o.Bool("invert", false);

        protected override bool Inverted => invert;

        protected override void Draw(SkiaCanvas surface, double time, double amount)
        {
            using var path = Path(time, out _);
            if (invert) path.FillType = SKPathFillType.InverseWinding;
            using var paint = Paint(Color.Evaluate(time), amount);
            surface.SkCanvas.DrawPath(path, paint);
        }

        public override XElement ToSif(SifWriter sif) => Element("region", "0.1", sif, ShapeParams(sif));

        public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
        {
            var path = SvgElement("path");
            if (invert) path.SetAttributeValue("fill-rule", "evenodd");
            svg.Attribute(path, "d", t => (invert ? SvgAnimationWriter.Everything : "") + PathData(map.Local(t)));
            Paint(path, "fill", Color, svg, map);
            Opacity(path, svg, map);
            return path;
        }
    }

    public sealed class OutlineLayer : Shape
    {
        public OutlineLayer(MotionOptions o) : base(o, "outline", false)
        {
            width = o.Node("width", MotionType.Real, 1d);
            sharpCusps = o.Bool("sharpCusps", true);
            roundTips = o.Bool("roundTips", true);
            trimStart = o.Node("trimStart", MotionType.Real, 0d);
            trimEnd = o.Node("trimEnd", MotionType.Real, 1d);
            trimOffset = o.Node("trimOffset", MotionType.Real, 0d);
            trimmed = o.Has("trimStart") || o.Has("trimEnd") || o.Has("trimOffset");
        }

        private readonly MotionNode width, trimStart, trimEnd, trimOffset;
        private readonly bool sharpCusps, roundTips, trimmed;

        public override IEnumerable<MotionNode> Nodes() => base.Nodes().Append(width).Append(trimStart).Append(trimEnd).Append(trimOffset);

        /// <summary>
        /// The visible spans in arc length, and the length: one span, or two where the trim runs past the end and wraps to
        /// the start; none when <c>trimEnd</c> is at or before <c>trimStart</c>.
        /// </summary>
        private (double Length, (double A, double B)[] Spans) Trim(SKPath path, double time)
        {
            using var measure = new SKPathMeasure(path, false);
            double length = measure.Length;
            if (!trimmed) return (length, [(0, length)]);
            var s = Math.Clamp(trimStart.Evaluate(time)[0], 0, 1);
            var e = Math.Clamp(trimEnd.Evaluate(time)[0], 0, 1);
            if (e - s <= 1e-9 || length <= 0) return (length, []);
            if (e - s >= 1 - 1e-9) return (length, [(0, length)]);
            var a = (((s + trimOffset.Evaluate(time)[0]) % 1) + 1) % 1 * length;
            var visible = (e - s) * length;
            return a + visible <= length ? (length, [(a, a + visible)]) : (length, [(a, length), (0, a + visible - length)]);
        }

        /// <summary>
        /// A trimmed outline as Synfig can hold it: its advanced outline with one dash, whose gap is the rest of the length so
        /// the pattern repeats once per length — the visible part lands where the trim puts it and wraps past the end as the
        /// trim does. The length changes as the points move, so the dash is sampled at the frame rate and held where still;
        /// a frame showing nothing takes the amount to 0, since a dash of no length still draws its round tips.
        /// </summary>
        private XElement TrimmedToSif(SifWriter sif)
        {
            var fps = sif.Fps;
            var count = (int)Math.Floor(sif.Duration * fps + 1e-9) + 1;
            var times = Enumerable.Range(0, count).Select(f => f / fps).ToArray();
            var (offsets, lengths, phases, amounts) = (new double[count][], new double[count][], new double[count][], new double[count][]);
            for (var f = 0; f < count; f++)
            {
                using var path = Path(times[f], out _);
                var (length, spans) = Trim(path, times[f]);
                var visible = spans.Sum(x => x.B - x.A);
                var gap = Math.Max(0, length - visible);
                var start = spans.Length == 0 ? 0 : spans[0].A;
                var q = start - gap;
                var phase = length > 0 ? q - length * Math.Ceiling(q / length) : 0;
                (offsets[f], lengths[f], phases[f]) = ([gap], [visible], [phase]);
                amounts[f] = [spans.Length == 0 ? 0 : Math.Clamp(Amount.Evaluate(times[f])[0], 0, 1)];
            }

            var tip = new XElement("integer", new XAttribute("value", roundTips ? 1 : 4));
            var element = Element("advanced_outline", "0.2", sif,
                ShapeParams(sif),
                sif.Param("width", width),
                SifWriter.Param("expand", new XElement("real", new XAttribute("value", "0"))),
                SifWriter.Param("start_tip", new XElement(tip)),
                SifWriter.Param("end_tip", new XElement(tip)),
                SifWriter.Param("cusp_type", new XElement("integer", new XAttribute("value", sharpCusps ? 0 : 1))),
                SifWriter.Param("smoothness", new XElement("real", new XAttribute("value", "0"))),
                SifWriter.Param("homogeneous", SifWriter.Bool(true)),
                SifWriter.Param("wplist", new XElement("wplist", new XAttribute("type", "width_point"), new XAttribute("loop", "false"))),
                SifWriter.Param("dash_enabled", SifWriter.Bool(true)),
                SifWriter.Param("dilist", new XElement("dilist", new XAttribute("type", "dash_item"), new XAttribute("loop", "false"),
                    new XElement("entry", new XElement("composite", new XAttribute("type", "dash_item"),
                        sif.Link("offset", MotionTextLayer.Keys(MotionType.Real, times, offsets)),
                        sif.Link("length", MotionTextLayer.Keys(MotionType.Real, times, lengths)),
                        new XElement("side_before", new XElement(tip)),
                        new XElement("side_after", new XElement(tip)))))),
                sif.Param("dash_offset", MotionTextLayer.Keys(MotionType.Real, times, phases)));
            element.Elements("param").First(e => (string?)e.Attribute("name") == "amount")
                .ReplaceWith(sif.Param("amount", MotionTextLayer.Keys(MotionType.Real, times, amounts)));
            return element;
        }

        public override XElement ToSif(SifWriter sif) => trimmed ? TrimmedToSif(sif) : Element("outline", "0.3", sif,
            ShapeParams(sif),
            sif.Param("width", width),
            SifWriter.Param("expand", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("sharp_cusps", SifWriter.Bool(sharpCusps)),
            SifWriter.Param("round_tip[0]", SifWriter.Bool(roundTips)),
            SifWriter.Param("round_tip[1]", SifWriter.Bool(roundTips)),
            SifWriter.Param("homogeneous_width", SifWriter.Bool(true)));

        /// <summary>
        /// A stroke while every point has the same width at every frame; otherwise the tapered outline as
        /// a filled polygon with a fixed number of vertices, so its <c>d</c> interpolates.
        /// </summary>
        public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
        {
            var path = SvgElement("path");
            var even = svg.Times.All(t =>
            {
                var s = States(map.Local(t));
                return s.All(p => Math.Abs(p.Width - s[0].Width) < 1e-9);
            });

            if (trimmed && !even)
                throw new InvalidOperationException($"The outline{(Desc is null ? "" : $" '{Desc}'")} tapers and is trimmed, which SVG cannot draw: "
                    + "SVG trims a stroke by dashing it, and a taper is drawn as a filled shape. Capture the composition as frames, or write a .sif.");

            if (trimmed)
            {
                // The same dash Synfig takes: the visible part, then the rest of the length, starting where the trim starts.
                svg.Attribute(path, "stroke-dasharray", t =>
                {
                    using var p = Path(map.Local(t), out _);
                    var (length, spans) = Trim(p, map.Local(t));
                    var visible = spans.Sum(x => x.B - x.A);
                    return $"{SvgAnimationWriter.N(visible)} {SvgAnimationWriter.N(Math.Max(0, length - visible) + 1e-3)}";
                });
                svg.Attribute(path, "stroke-dashoffset", t =>
                {
                    using var p = Path(map.Local(t), out _);
                    var (_, spans) = Trim(p, map.Local(t));
                    return SvgAnimationWriter.N(spans.Length == 0 ? 0 : -spans[0].A);
                });
                svg.Attribute(path, "visibility", t =>
                {
                    using var p = Path(map.Local(t), out _);
                    return Trim(p, map.Local(t)).Spans.Length == 0 ? "hidden" : "visible";
                });
            }

            if (even)
            {
                svg.Attribute(path, "d", t => PathData(map.Local(t)));
                path.SetAttributeValue("fill", "none");
                Paint(path, "stroke", Color, svg, map);
                svg.Attribute(path, "stroke-width", svg.Track(t =>
                {
                    var local = map.Local(t);
                    var s = States(local);
                    return s.Length == 0 ? 0 : Math.Abs(width.Evaluate(local)[0] * s[0].Width);
                }));
                path.SetAttributeValue("stroke-linecap", roundTips ? "round" : "butt");
                path.SetAttributeValue("stroke-linejoin", sharpCusps ? "miter" : "round");
            }
            else
            {
                svg.Attribute(path, "d", t => TaperedData(map.Local(t)));
                Paint(path, "fill", Color, svg, map);
            }

            Opacity(path, svg, map);
            return path;
        }

        /// <summary>The tapered outline as one polygon: down the left side, round the far tip, back up the right.</summary>
        private string TaperedData(double time)
        {
            const int samples = 32;
            var s = States(time);
            var w = width.Evaluate(time)[0];
            var left = new List<SKPoint>();
            var right = new List<SKPoint>();
            var count = Loop ? s.Length : s.Length - 1;
            SKPoint tan = new(1, 0), first = new(1, 0);

            for (var i = 0; i < count; i++)
            {
                var a = s[i];
                var b = s[(i + 1) % s.Length];
                using var seg = new SKPath();
                seg.MoveTo(a.P);
                seg.CubicTo(a.P + Third(a.T2), b.P - Third(b.T1), b.P);
                using var measure = new SKPathMeasure(seg);
                for (var k = i == 0 ? 0 : 1; k <= samples; k++)
                {
                    if (!measure.GetPositionAndTangent(measure.Length * k / samples, out var pos, out var tn)) pos = a.P;
                    else if (tn.Length > 0) tan = tn;
                    if (i == 0 && k == 0) first = tan;
                    var half = (float)(Math.Abs(w) * (a.Width + (b.Width - a.Width) * k / samples) / 2);
                    var n = new SKPoint(-tan.Y * half, tan.X * half);
                    left.Add(pos + n);
                    right.Add(pos - n);
                }
            }

            if (left.Count == 0) return "";

            var d = new StringBuilder($"M{P(left[0])}");
            foreach (var q in left.Skip(1)) d.Append($"L{P(q)}");
            if (!Loop && roundTips) Arc(d, s[^1].P, tan, (float)(Math.Abs(w) * s[^1].Width / 2), true);
            for (var k = right.Count - 1; k >= 0; k--) d.Append($"L{P(right[k])}");
            if (!Loop && roundTips) Arc(d, s[0].P, first, (float)(Math.Abs(w) * s[0].Width / 2), false);
            d.Append('Z');
            return d.ToString();

            // A semicircle with a fixed vertex count round an end: from the left side to the right.
            static void Arc(StringBuilder d, SKPoint centre, SKPoint direction, float radius, bool forward)
            {
                const int tip = 8;
                var heading = Math.Atan2(direction.Y, direction.X);
                var a0 = forward ? heading + Math.PI / 2 : heading - Math.PI / 2;
                for (var j = 1; j < tip; j++)
                {
                    var a = a0 - Math.PI * j / tip;
                    d.Append($"L{P(new SKPoint(centre.X + (float)(radius * Math.Cos(a)), centre.Y + (float)(radius * Math.Sin(a))))}");
                }
            }
        }

        protected override void Draw(SkiaCanvas surface, double time, double amount)
        {
            var canvas = surface.SkCanvas;
            using var path = Path(time, out var states);
            if (states.Length == 0) return;

            var w = width.Evaluate(time)[0];
            using var paint = Paint(Color.Evaluate(time), amount);
            var (length, spans) = Trim(path, time);
            if (spans.Length == 0) return;
            var whole = spans.Length == 1 && spans[0].A <= 1e-9 && spans[0].B >= length - 1e-9;

            if (states.All(s => Math.Abs(s.Width - states[0].Width) < 1e-9))
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)Math.Abs(w * states[0].Width);
                paint.StrokeCap = roundTips ? SKStrokeCap.Round : SKStrokeCap.Butt;
                paint.StrokeJoin = sharpCusps ? SKStrokeJoin.Miter : SKStrokeJoin.Round;
                if (whole)
                {
                    canvas.DrawPath(path, paint);
                    return;
                }

                // The visible part, in one piece where a looped outline's trim runs on round its seam. Each span is taken into a
                // path of its own: SkiaSharp's GetSegment replaces its destination rather than appending to it, as Skia's does.
                using var measure = new SKPathMeasure(path, false);
                using var part = new SKPath();
                for (var i = 0; i < spans.Length; i++)
                {
                    using var piece = new SKPath();
                    measure.GetSegment((float)spans[i].A, (float)spans[i].B, piece, true);
                    part.AddPath(piece, i > 0 && Loop ? SKPathAddMode.Extend : SKPathAddMode.Append);
                }
                canvas.DrawPath(part, paint);
                return;
            }

            using var shape = whole ? Tapered(states, w) : TaperedSpans(states, w, spans);
            canvas.DrawPath(shape, paint);
        }

        /// <summary>
        /// The tapered outline over the visible spans only: the ribbon sampled along the whole outline as <see cref="Tapered"/>
        /// samples it, kept between each span's ends, with tips at those ends and round joins at the points inside.
        /// </summary>
        private SKPath TaperedSpans(MotionSplinePoint.State[] s, double width, (double A, double B)[] spans)
        {
            const int samples = 32;
            var line = new List<(double D, SKPoint P, SKPoint N, float Half)>();
            var joints = new List<(double D, MotionSplinePoint.State Point)>();
            var count = Loop ? s.Length : s.Length - 1;
            double at = 0;
            for (var i = 0; i < count; i++)
            {
                var (a, b) = (s[i], s[(i + 1) % s.Length]);
                using var seg = new SKPath();
                seg.MoveTo(a.P);
                Segment(seg, a, b);
                using var measure = new SKPathMeasure(seg);
                var length = measure.Length;
                joints.Add((at, a));
                for (var k = i == 0 ? 0 : 1; k <= samples; k++)
                {
                    var d = length * k / samples;
                    if (!measure.GetPositionAndTangent(d, out var pos, out var tan)) continue;
                    var half = (float)(Math.Abs(width) * (a.Width + (b.Width - a.Width) * k / samples) / 2);
                    line.Add((at + d, pos, new SKPoint(-tan.Y, tan.X), half));
                }
                at += length;
            }

            var result = new SKPath();
            foreach (var (from, to) in spans)
            {
                var kept = new List<(SKPoint P, SKPoint N, float Half)> { Sample(from) };
                kept.AddRange(line.Where(x => x.D > from && x.D < to).Select(x => (x.P, x.N, x.Half)));
                kept.Add(Sample(to));

                using var body = new SKPath();
                body.MoveTo(kept[0].P + Scale(kept[0].N, kept[0].Half));
                foreach (var k in kept.Skip(1)) body.LineTo(k.P + Scale(k.N, k.Half));
                for (var k = kept.Count - 1; k >= 0; k--) body.LineTo(kept[k].P - Scale(kept[k].N, kept[k].Half));
                body.Close();
                using var simple = body.Simplify() ?? new SKPath(body);
                Union(result, simple);

                // Tips at the span's ends; round joins at the points it passes.
                if (roundTips) { Disc(result, kept[0].P, kept[0].Half); Disc(result, kept[^1].P, kept[^1].Half); }
                foreach (var (d, point) in joints.Where(j => j.D > from && j.D < to))
                    Disc(result, point.P, (float)(Math.Abs(width) * point.Width / 2));
            }
            return result;

            (SKPoint P, SKPoint N, float Half) Sample(double d)
            {
                var i = line.FindIndex(x => x.D >= d);
                if (i <= 0) return (line[Math.Max(0, i)].P, line[Math.Max(0, i)].N, line[Math.Max(0, i)].Half);
                var (p, q) = (line[i - 1], line[i]);
                var f = (float)((d - p.D) / Math.Max(1e-12, q.D - p.D));
                return (p.P + Scale(q.P - p.P, f), q.N, p.Half + (q.Half - p.Half) * f);
            }

            static SKPoint Scale(SKPoint v, float k) => new(v.X * k, v.Y * k);

            static void Union(SKPath into, SKPath add)
            {
                using var merged = into.Op(add, SKPathOp.Union);
                if (merged is not null) { into.Reset(); into.AddPath(merged); }
            }

            static void Disc(SKPath into, SKPoint c, float r)
            {
                if (r <= 0) return;
                using var disc = new SKPath();
                disc.AddCircle(c.X, c.Y, r);
                Union(into, disc);
            }
        }

        /// <summary>
        /// A stroke whose half-width runs linearly in arc length from one point's width to the next,
        /// as Synfig's homogeneous width does, with round joins and tips.
        /// </summary>
        private SKPath Tapered(MotionSplinePoint.State[] s, double width)
        {
            const int samples = 32;
            var left = new List<SKPoint>();
            var right = new List<SKPoint>();
            var result = new SKPath();
            var count = Loop ? s.Length : s.Length - 1;

            for (var i = 0; i < count; i++)
            {
                var a = s[i];
                var b = s[(i + 1) % s.Length];
                using var seg = new SKPath();
                seg.MoveTo(a.P);
                Segment(seg, a, b);
                using var measure = new SKPathMeasure(seg);
                var length = measure.Length;

                for (var k = (i == 0 ? 0 : 1); k <= samples; k++)
                {
                    var d = length * k / samples;
                    if (!measure.GetPositionAndTangent(d, out var pos, out var tan)) continue;
                    var half = (float)(Math.Abs(width) * (a.Width + (b.Width - a.Width) * k / samples) / 2);
                    var n = new SKPoint(-tan.Y, tan.X);
                    left.Add(pos + new SKPoint(n.X * half, n.Y * half));
                    right.Add(pos - new SKPoint(n.X * half, n.Y * half));
                }

                // Round joins and tips: a disc at every point covers the corner between two segments.
                AddDisc(result, a, width);
            }

            if (!Loop || s.Length == 1) AddDisc(result, s[Loop ? 0 : ^1], width);

            if (left.Count > 1)
            {
                using var body = new SKPath();
                body.MoveTo(left[0]);
                foreach (var p in left.Skip(1)) body.LineTo(p);
                for (var k = right.Count - 1; k >= 0; k--) body.LineTo(right[k]);
                body.Close();
                using var simple = body.Simplify() ?? new SKPath(body);
                using var merged = result.Op(simple, SKPathOp.Union) ?? new SKPath(result);
                return new SKPath(merged);
            }

            return result;

            void AddDisc(SKPath into, MotionSplinePoint.State p, double w)
            {
                if (!roundTips && !Loop && (p.Equals(s[0]) || p.Equals(s[^1]))) return;
                var r = (float)(Math.Abs(w) * p.Width / 2);
                if (r <= 0) return;
                using var disc = new SKPath();
                disc.AddCircle(p.P.X, p.P.Y, r);
                using var merged = into.Op(disc, SKPathOp.Union);
                if (merged is not null) { into.Reset(); into.AddPath(merged); }
            }
        }
    }

    public sealed class DrawnLayer : MotionLayer
    {
        public DrawnLayer(MotionOptions o, Action<CanvasRenderingContext2D, object?, double> draw) : base(o)
        {
            this.draw = draw;
            values = ReadValues(o.Raw("values"));
        }

        private readonly Action<CanvasRenderingContext2D, object?, double> draw;
        private readonly (string Name, MotionNode Node)[] values;

        public override IEnumerable<MotionNode> Nodes() => values.Select(v => v.Node).Prepend(Amount);

        protected override void Draw(SkiaCanvas surface, double time, double amount)
        {
            var canvas = surface.SkCanvas;
            var depth = canvas.Save();
            if (amount < 1)
            {
                using var fade = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(amount * 255)) };
                canvas.SaveLayer(fade);
            }

            try
            {
                var ctx = new CanvasRenderingContext2D(surface, canvas.TotalMatrix);
                var v = values.ToDictionary(e => e.Name, e => (object?)MotionTypes.ToJs(e.Node.Kind, e.Node.Evaluate(time)));
                draw(ctx, v, time);
            }
            finally
            {
                canvas.RestoreToCount(depth);
            }
        }

        public override XElement ToSif(SifWriter sif) => throw new InvalidOperationException(
            $"The drawn layer{(Desc is null ? "" : $" '{Desc}'")} cannot be written as .sif: Synfig cannot run a script. "
            + "Write the composition without it, or capture it as frames.");

        public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map) => throw SvgAnimationWriter.Refuse("drawn layer", Desc);

        /// <summary>Each value's type is read from what it is: a node, a number, a point, or a colour.</summary>
        private static (string, MotionNode)[] ReadValues(object? raw)
        {
            if (raw is null) return [];
            var d = JsInterop.AsDict(raw) ?? throw new ArgumentException("drawn's values is an object: { name: value or node }.");
            return [.. d.Keys.Cast<object>().Select(k => k.ToString()!).Select(name =>
            {
                var value = d[name];
                var who = $"drawn's value '{name}'";
                var kind = value switch
                {
                    MotionNode n => n.Kind,
                    double or int or float or long => MotionType.Real,
                    string => MotionType.Color,
                    IList or IDictionary or IDictionary<string, object?> => MotionType.Vector,
                    _ => throw new ArgumentException($"{who} is a number, a point, a colour or a node, not {value?.GetType().Name ?? "nothing"}.")
                };
                return (name, MotionNodeFactory.Node(value, kind, who));
            })];
        }
    }

    public sealed class GroupLayer : MotionLayer
    {
        public GroupLayer(MotionOptions o, MotionGroup children, MotionSwitchTrack? active = null, MotionPin? pin = null) : base(o)
        {
            this.children = children;
            (this.active, who, this.pin) = (active, o.Who, pin);
            z = o.Node("z", MotionType.Real, 0d);
            rotateX = o.Node("rotateX", MotionType.Angle, 0d);
            rotateY = o.Node("rotateY", MotionType.Angle, 0d);
            Is3D = o.Has("z") || o.Has("rotateX") || o.Has("rotateY");
            keepSize = o.Bool("keepSize", false);
            if (keepSize && !Is3D)
                throw new ArgumentException($"{o.Who}'s keepSize holds a plane at depth to its size at z 0; give the group a z, or drop keepSize.");
            origin = o.Node("origin", MotionType.Vector, new[] { 0d, 0d });
            offset = o.Node("offset", MotionType.Vector, new[] { 0d, 0d });
            angle = o.Node("angle", MotionType.Angle, 0d);
            skewAngle = o.Node("skewAngle", MotionType.Angle, 0d);
            scale = o.Node("scale", MotionType.Vector, new[] { 1d, 1d });
            timeOffset = o.Number("timeOffset", 0);
            timeDilation = o.Number("timeDilation", 1);

            if (o.Has("bone"))
            {
                if (o.Raw("bone") is not MotionBone b)
                    throw new ArgumentException($"{o.Who}'s bone is a bone made by composition.bone(...); a weighted list binds points, not groups.");
                var own = new[] { "origin", "offset", "angle", "skewAngle", "scale", "z", "rotateX", "rotateY" }.Where(o.Has).ToArray();
                if (own.Length > 0)
                    throw new ArgumentException(
                        $"{o.Who} follows bone '{b.Name}', which gives it its transform: drop {string.Join(", ", own)}, or put them on a group inside it.");
                bone = b;
                restInverse = b.RestLink.Invert();
            }
        }

        private readonly MotionBone? bone;
        private readonly MotionAffine restInverse;

        private readonly MotionGroup children;
        private readonly MotionNode origin, offset, angle, skewAngle, scale, z, rotateX, rotateY;
        private readonly bool keepSize;
        private readonly MotionPin? pin;

        /// <summary>A plane the camera sees in perspective: given <c>z</c>, <c>rotateX</c> or <c>rotateY</c>.</summary>
        internal bool Is3D { get; }
        /// <summary>For a switch, which child shows, read at the switch's own time; null for a plain group.</summary>
        private readonly MotionSwitchTrack? active;
        private readonly string who;

        internal MotionGroup Children => children;
        private readonly double timeOffset, timeDilation;

        public override IEnumerable<MotionNode> Nodes() =>
            new[] { Amount, origin, offset, angle, skewAngle, scale, z, rotateX, rotateY }.Concat(pin?.To ?? []).Concat(children.Nodes());

        /// <summary>
        /// Draws the children in the group's frame. They are drawn on a surface of their own whenever that
        /// could differ from drawing them in place — a fade, or a child that blends — because Synfig blends
        /// a child with its siblings only, never with what lies under the group.
        /// </summary>
        protected override void Draw(SkiaCanvas surface, double time, double amount)
        {
            // A switch picks its layer at its own time, as Synfig evaluates a layer's parameters, then draws only that one.
            var only = Shown(time);
            if (active is not null && only is null) return;

            var root = children.Root;
            var parent = root?.Space ?? MotionSpace.Root;
            var (space, childTime) = Enter(parent, root?.Frame, time);
            if (space is not { } inner) return;
            if ((Is3D || pin is not null) && children.HasDeformationDeep)
                throw new InvalidOperationException($"The {(Is3D ? "3D group" : "warp")}{Named} holds a skeleton deformation, whose bones live on a flat "
                    + "page, not seen in perspective. Put the 3D group or warp round the deformed drawing's group instead, or keep it flat.");

            var canvas = surface.SkCanvas;
            canvas.Save();
            if (Is3D)
            {
                if (!parent.Plane.TryInvert(out var back)) { canvas.Restore(); return; }
                canvas.Concat(SKMatrix.Concat(back, inner.Plane));
                // Only what lies in front of the camera's near plane: a half-plane of the layer's own units.
                var clip = NearClip(inner, root!.Frame!);
                if (clip is null) { canvas.Restore(); return; }
                using (clip) if (!clip.IsEmpty) canvas.ClipPath(clip, SKClipOperation.Intersect, true);
            }
            else
            {
                canvas.Concat(Matrix(time));
                if (pin is not null)
                {
                    canvas.Concat(pin.At(time)!.Value);
                    if (pin.Clip) canvas.ClipRect(pin.Rect, SKClipOperation.Intersect, true);
                }
            }

            var isolate = amount < 1 || children.HasBlendedLayersAt(childTime);
            if (isolate)
            {
                using var fade = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(amount * 255)) };
                canvas.SaveLayer(fade);
            }

            root?.PushSpace(inner);
            try
            {
                children.RenderLayers(surface, childTime, only);
            }
            finally
            {
                root?.PopSpace();
            }
            if (isolate) canvas.Restore();
            canvas.Restore();
        }

        private string Named => Desc is null ? "" : $" '{Desc}'";

        /// <summary>
        /// The layer's units in front of the camera's near plane, as a clip in those units: an empty path when all of a large
        /// square round the layer is in front (no clip needed), null when none of it is.
        /// </summary>
        private static SKPath? NearClip(MotionSpace space, MotionCameraFrame camera)
        {
            // Camera depth is affine on the plane: depth(u, v) = A·u + B·v + C.
            var m = camera.View * space.World;
            var (a, b, c) = (m[2, 0], m[2, 1], m[2, 3] - MotionCameraFrame.Near);
            const double R = 1e5;
            var square = new[] { (-R, -R), (R, -R), (R, R), (-R, R) };
            if (square.All(p => a * p.Item1 + b * p.Item2 + c >= 0)) return new SKPath();

            // Sutherland–Hodgman against the one edge.
            var kept = new List<(double X, double Y)>();
            for (var i = 0; i < 4; i++)
            {
                var (p, q) = (square[i], square[(i + 1) % 4]);
                var (dp, dq) = (a * p.Item1 + b * p.Item2 + c, a * q.Item1 + b * q.Item2 + c);
                if (dp >= 0) kept.Add(p);
                if (dp >= 0 != dq >= 0)
                {
                    var t = dp / (dp - dq);
                    kept.Add((p.Item1 + (q.Item1 - p.Item1) * t, p.Item2 + (q.Item2 - p.Item2) * t));
                }
            }
            if (kept.Count < 3) return null;
            var path = new SKPath();
            path.AddPoly([.. kept.Select(k => new SKPoint((float)k.X, (float)k.Y))]);
            return path;
        }

        /// <summary>
        /// One step from the group's stack into its own: the space its layers draw in and the time they see. A 3D group is
        /// a plane through the camera; a pin whose corners make no map gives no space.
        /// </summary>
        internal (MotionSpace? Space, double Time) Enter(MotionSpace parent, MotionCameraFrame? camera, double time)
        {
            var childTime = time * timeDilation + timeOffset;
            if (Is3D)
            {
                if (camera is null)
                    throw new InvalidOperationException($"The group{Named} is 3D (it has z, rotateX or rotateY), and the composition has no camera: add comp.camera().");
                if (parent.Warped)
                    throw new InvalidOperationException($"The 3D group{Named} sits inside a warp, whose corner pin has no place in the camera's world. Put the warp inside the 3D group instead.");
                var world = World(parent, time);
                return (new MotionSpace(world, (camera.Projection * world).Plane(), false), childTime);
            }

            var m = Matrix(time);
            var lifted = parent.World * Mat4.Lift(m);
            if (pin is null) return (new MotionSpace(lifted, SKMatrix.Concat(parent.Plane, m), parent.Warped), childTime);
            return pin.At(time) is { } h
                ? (new MotionSpace(lifted, SKMatrix.Concat(parent.Plane, SKMatrix.Concat(m, h)), true), childTime)
                : (null, childTime);
        }

        /// <summary>Whether <paramref name="child"/> shows at the group's own <paramref name="time"/>: always, but in a switch.</summary>
        internal bool Shows(double time, MotionLayer child) => active is null || ReferenceEquals(Shown(time), child);

        /// <summary>The camera depth of the group's anchor (its origin), how a run of 3D groups is sorted.</summary>
        internal double AnchorDepth(MotionSpace parent, MotionCameraFrame camera, double time)
        {
            var o = origin.Evaluate(time);
            var p = World(parent, time).Apply(o[0], o[1], 0);
            return camera.Depth(p.X, p.Y, p.Z);
        }

        /// <summary>
        /// The plane's world matrix: its own after its parent's. With <c>keepSize</c> it is also scaled about the camera's
        /// target by <c>(distance − targetZ + z) / (distance − targetZ)</c>, all read at time 0, so at the film's start it looks
        /// exactly as it would at z 0 — a poster split into planes at depths is unchanged until the camera moves, and then
        /// each plane moves at its depth. Exact when the camera looks straight on at time 0; a plane inside another 3D plane
        /// counts only its own z.
        /// </summary>
        private Mat4 World(MotionSpace parent, double time)
        {
            var world = parent.World * Local4(time);
            if (!keepSize || children.Root?.CameraTargetAt(0) is not { } rest) return world;
            var near = rest.Distance - rest.Z;
            var k = (near + z.Evaluate(0)[0]) / near;
            return Mat4.Translate(rest.X, rest.Y, 0) * Mat4.Rows(k, 0, 0, 0, 0, k, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)
                * Mat4.Translate(-rest.X, -rest.Y, 0) * world;
        }

        /// <summary>
        /// The plane's world matrix, After Effects' order: <c>translate(offset, z) · rotZ(angle) · rotY · rotX · skew · scale ·
        /// translate(−origin)</c>. With no rotateX, rotateY or z it is the 2D transform exactly.
        /// </summary>
        private Mat4 Local4(double time)
        {
            var o = origin.Evaluate(time);
            var off = offset.Evaluate(time);
            var a = angle.Evaluate(time)[0] * Math.PI / 180;
            var sk = skewAngle.Evaluate(time)[0] * Math.PI / 180;
            var sc = scale.Evaluate(time);
            var (sa, ca) = Math.SinCos(a);
            var rz = Mat4.Rows(ca, -sa, 0, 0, sa, ca, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            var ks = Mat4.Rows(sc[0], -Math.Sin(sk) * sc[1], 0, 0, 0, Math.Cos(sk) * sc[1], 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            return Mat4.Translate(off[0], off[1], z.Evaluate(time)[0]) * rz * Mat4.RotateY(rotateY.Evaluate(time)[0])
                * Mat4.RotateX(rotateX.Evaluate(time)[0]) * ks * Mat4.Translate(-o[0], -o[1], 0);
        }

        /// <summary>Synfig's summary transformation: <c>offset · axes(scale, angle, skew) · translate(−origin)</c>.</summary>
        private SKMatrix Matrix(double time)
        {
            // Bound to a bone: carried from the rest pose by the bone's frame, as Synfig's bone link on a transformation.
            if (bone is not null) return (bone.Link(time) * restInverse).ToSk();

            var o = origin.Evaluate(time);
            var off = offset.Evaluate(time);
            var a = angle.Evaluate(time)[0] * Math.PI / 180;
            var sk = skewAngle.Evaluate(time)[0] * Math.PI / 180;
            var sc = scale.Evaluate(time);

            var ax = (X: sc[0] * Math.Cos(a), Y: sc[0] * Math.Sin(a));
            var ay = (X: sc[1] * Math.Cos(a + sk + Math.PI / 2), Y: sc[1] * Math.Sin(a + sk + Math.PI / 2));
            var axes = new SKMatrix((float)ax.X, (float)ay.X, (float)off[0], (float)ax.Y, (float)ay.Y, (float)off[1], 0, 0, 1);
            return SKMatrix.Concat(axes, SKMatrix.CreateTranslation((float)-o[0], (float)-o[1]));
        }

        /// <summary>
        /// The summary transformation as nested groups, one transform each, so each animates on its own:
        /// <c>translate(offset) rotate(angle) skewX(−skew) scale(sx, sy·cos skew) translate(−origin)</c>.
        /// </summary>
        public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
        {
            if (Is3D || pin is not null)
                throw new InvalidOperationException($"The {(Is3D ? "3D group" : "warp")}{Named} cannot be written as animated SVG: SVG has no "
                    + "perspective. Write a .sif, or capture the composition as frames.");
            var outer = SvgElement("g");
            Opacity(outer, svg, map);
            if (children.HasBlendedLayers) outer.SetAttributeValue("style", "isolation:isolate");

            var chain = bone is null ? NodeChain(svg, map) : BoneChain(svg, map);

            svg.Depth++;
            var childMap = map.Then(timeDilation, timeOffset);
            var content = active is null
                ? children.LayersToSvg(svg, childMap).ToArray()
                : children.SwitchedToSvg(svg, childMap, t => Shown(map.Local(t)), Desc).ToArray();
            svg.Depth--;

            // Innermost first: each transform wraps everything after it in the chain.
            object inner = content;
            for (var i = chain.Length - 1; i >= 0; i--)
            {
                chain[i].Add(inner);
                inner = chain[i];
            }

            outer.Add(inner);
            return outer;
        }

        /// <summary>
        /// A bound group's transform, sampled: the bone's frame times the inverse rest frame, decomposed per
        /// time as Synfig decomposes a matrix, with the angles unwrapped so a turn past ±180° does not spin back.
        /// </summary>
        private XElement[] BoneChain(SvgAnimationWriter svg, MotionTimeMap map)
        {
            var at = new Dictionary<double, (double X, double Y, double Angle, double Skew, double Sx, double Sy)>();
            double lastAngle = 0, lastSkew = 0;
            var first = true;
            foreach (var t in svg.Times)
            {
                var d = (bone!.Link(map.Local(t)) * restInverse).Decompose();
                var angle = first ? d.Angle : Unwrap(d.Angle, lastAngle);
                var skew = first ? d.Skew : Unwrap(d.Skew, lastSkew);
                (lastAngle, lastSkew, first) = (angle, skew, false);
                at[t] = (d.OffsetX, d.OffsetY, angle, skew, d.ScaleX, d.ScaleY * Math.Cos(skew * Math.PI / 180));
            }

            return new[]
            {
                svg.Transform("translate", svg.Track(t => at[t].X), v => $"{SvgAnimationWriter.N(v)} 0", 0),
                svg.Transform("translate", svg.Track(t => at[t].Y), v => $"0 {SvgAnimationWriter.N(v)}", 0),
                svg.Transform("rotate", svg.Track(t => at[t].Angle), SvgAnimationWriter.N, 0),
                svg.Transform("skewX", svg.Track(t => -at[t].Skew), SvgAnimationWriter.N, 0),
                svg.Transform("scale", svg.Track(t => at[t].Sx), v => $"{SvgAnimationWriter.N(v, 6)} 1", 1),
                svg.Transform("scale", svg.Track(t => at[t].Sy), v => $"1 {SvgAnimationWriter.N(v, 6)}", 1),
            }.Where(g => g.HasAttributes || g.HasElements).ToArray();

            static double Unwrap(double v, double previous)
            {
                while (v - previous > 180) v -= 360;
                while (v - previous < -180) v += 360;
                return v;
            }
        }

        private XElement[] NodeChain(SvgAnimationWriter svg, MotionTimeMap map)
        {
            var skewTrack = svg.Track(skewAngle, 0, map);
            var syTrack = skewTrack.IsStatic && Math.Abs(skewTrack.Values[0]) < 1e-12
                ? svg.Track(scale, 1, map)
                : svg.Track(t => scale.Evaluate(map.Local(t))[1] * Math.Cos(skewAngle.Evaluate(map.Local(t))[0] * Math.PI / 180));

            return new[]
            {
                svg.Transform("translate", svg.Track(offset, 0, map), v => $"{SvgAnimationWriter.N(v)} 0", 0),
                svg.Transform("translate", svg.Track(offset, 1, map), v => $"0 {SvgAnimationWriter.N(v)}", 0),
                svg.Transform("rotate", svg.Track(angle, 0, map), SvgAnimationWriter.N, 0),
                svg.Transform("skewX", Negate(skewTrack), SvgAnimationWriter.N, 0),
                svg.Transform("scale", svg.Track(scale, 0, map), v => $"{SvgAnimationWriter.N(v)} 1", 1),
                svg.Transform("scale", syTrack, v => $"1 {SvgAnimationWriter.N(v)}", 1),
                svg.Transform("translate", Negate(svg.Track(origin, 0, map)), v => $"{SvgAnimationWriter.N(v)} 0", 0),
                svg.Transform("translate", Negate(svg.Track(origin, 1, map)), v => $"0 {SvgAnimationWriter.N(v)}", 0),
            }.Where(g => g.HasAttributes || g.HasElements).ToArray();

            // Negating a track's values leaves its splines right: a spline shapes progress, not direction.
            static MotionTrack Negate(MotionTrack t) => t with { Values = [.. t.Values.Select(v => -v)] };
        }

        /// <summary>The child a switch shows at a time, refusing a name no child has; null for a plain group or an empty name.</summary>
        private MotionLayer? Shown(double time) => active is null ? null : children.Named(active.At(time), who);

        /// <summary>
        /// One step from the group's stack into its own: its transform at <paramref name="time"/>, the time its children
        /// see, and whether <paramref name="child"/> shows then — a switch shows one.
        /// </summary>
        /// <summary>
        /// A group as Synfig's group; a switch as Synfig's switch, which is the same paste canvas with <c>layer_name</c> and
        /// <c>layer_depth</c> (−1: by name) added, its children locked as Synfig Studio makes them. Every name it shows is
        /// checked against the children first.
        /// </summary>
        public override XElement ToSif(SifWriter sif)
        {
            foreach (var name in active?.Names ?? []) children.Named(name, who);
            return Element(active is null ? "group" : "switch", active is null ? "0.3" : "0.0", sif, Is3D ? BakedParams(sif) : GroupParams(sif),
                active is null ? null : SifWriter.Param("layer_name", active.ToSif()),
                active is null ? null : SifWriter.Param("layer_depth", new XElement("integer", new XAttribute("value", "-1"))));
        }

        private object[] GroupParams(SifWriter sif) => CanvasParams(sif,
            sif.Param("origin", origin),
            SifWriter.Param("transformation", bone is null
                ? new XElement("composite", new XAttribute("type", "transformation"),
                    sif.Link("offset", offset), sif.Link("angle", angle), sif.Link("skew_angle", skewAngle), sif.Link("scale", scale))
                : BoneTransformation(sif)),
            new XElement("canvas", children.LayersToSif(sif), pin?.ToSif(sif)));

        /// <summary>
        /// A 3D group as Synfig can hold it: no transformation of its own, and a warp on top of its layers carrying the whole
        /// projection — a plane through a perspective camera is a homography — with the warp's corners sampled at the frame
        /// rate and held where they do not move. Refused at a frame where the plane is behind the camera or edge-on.
        /// </summary>
        private object[] BakedParams(SifWriter sif)
        {
            var root = children.Root!;
            var o = origin.Evaluate(0);
            const double half = 50;
            var from = new[] { o[0] - half, o[1] - half, o[0] + half, o[1] + half };
            var corners = new[] { (from[0], from[1]), (from[2], from[1]), (from[2], from[3]), (from[0], from[3]) };
            var times = new List<double>();
            var values = Enumerable.Range(0, 4).Select(_ => new List<double[]>()).ToArray();
            for (var f = 0; f < root.FrameCount; f++)
            {
                var t = f / root.Fps;
                var (parentSpace, parentTime, _, _) = children.Parent!.SpaceAt(t);
                var (space, local) = Enter(parentSpace, root.CameraAt(t), parentTime);
                if (!parentSpace.Plane.TryInvert(out var back))
                    throw new InvalidOperationException($"The 3D group{Named}'s parent has no inverse at {MotionTypes.Format(t)} s, so it cannot be written to .sif.");
                var rel = SKMatrix.Concat(back, space!.Value.Plane);
                times.Add(local);
                for (var k = 0; k < 4; k++)
                {
                    var (x, y) = corners[k];
                    var w = rel.Persp0 * x + rel.Persp1 * y + rel.Persp2;
                    if (w <= 1e-9)
                        throw new InvalidOperationException($"The 3D group{Named} turns away from or behind the camera at {MotionTypes.Format(t)} s, "
                            + "which Synfig's warp cannot draw. Keep it facing the camera, or render the film as video.");
                    var p = rel.MapPoint((float)x, (float)y);
                    values[k].Add([p.X, p.Y]);
                }
            }

            // Keys in the group's own clock, the time the warp inside it is evaluated at.
            var order = Enumerable.Range(0, times.Count).OrderBy(i => times[i]).ToArray();
            var keyTimes = order.Select(i => times[i]).ToArray();
            var to = values.Select(v => MotionTextLayer.Keys(MotionType.Vector, keyTimes, [.. order.Select(i => v[i])])).ToArray();
            var warp = new MotionPin(from, to, false);
            var zero = new MotionConstant(MotionType.Vector, [0, 0]);
            return CanvasParams(sif,
                sif.Param("origin", zero),
                SifWriter.Param("transformation", new XElement("composite", new XAttribute("type", "transformation"),
                    new XElement("offset", SifWriter.Value(MotionType.Vector, [0, 0])),
                    new XElement("angle", SifWriter.Value(MotionType.Angle, [0])),
                    new XElement("skew_angle", SifWriter.Value(MotionType.Angle, [0])),
                    new XElement("scale", SifWriter.Value(MotionType.Vector, [1, 1])))),
                new XElement("canvas", children.LayersToSif(sif), warp.ToSif(sif)));
        }

        private object[] CanvasParams(SifWriter sif, XElement originParam, XElement transformation, XElement canvas) =>
        [
            originParam,
            transformation,
            SifWriter.Param("canvas", canvas),
            SifWriter.Param("time_dilation", new XElement("real", new XAttribute("value", MotionTypes.Format(timeDilation)))),
            SifWriter.Param("time_offset", new XElement("time", new XAttribute("value", MotionTypes.Time(timeOffset)))),
            SifWriter.Param("children_lock", SifWriter.Bool(active is not null)),
            SifWriter.Param("outline_grow", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("z_range", SifWriter.Bool(false)),
            SifWriter.Param("z_range_position", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("z_range_depth", new XElement("real", new XAttribute("value", "0"))),
            SifWriter.Param("z_range_blur", new XElement("real", new XAttribute("value", "0")))
        ];

        /// <summary>A bone link on the transformation, its base value the inverse of the bone's rest frame.</summary>
        private XElement BoneTransformation(SifWriter sif)
        {
            var d = restInverse.Decompose();
            var local = new XElement("composite", new XAttribute("type", "transformation"),
                new XElement("offset", SifWriter.Value(MotionType.Vector, [d.OffsetX, d.OffsetY])),
                new XElement("angle", SifWriter.Value(MotionType.Angle, [d.Angle])),
                new XElement("skew_angle", SifWriter.Value(MotionType.Angle, [d.Skew])),
                new XElement("scale", SifWriter.Value(MotionType.Vector, [d.ScaleX, d.ScaleY])));
            return MotionBinding.Parse(bone, "group").Wrap(sif, "transformation", local);
        }
    }

    /// <summary>
    /// Synfig's skeleton layer: every bone of the composition drawn as a capsule from its origin to its
    /// tip, <c>width</c> and <c>tipwidth</c> its radii — how a rig is seen.
    /// </summary>
    public sealed class SkeletonLayer(MotionOptions o, IReadOnlyList<MotionBone> bones) : MotionLayer(o)
    {
        private readonly MotionNode color = o.Node("color", MotionType.Color, "#4a90e2");

        public override IEnumerable<MotionNode> Nodes() => [Amount, color];

        protected override void Draw(SkiaCanvas surface, double time, double amount)
        {
            using var path = Path(time, null);
            using var paint = Paint(color.Evaluate(time), amount);
            surface.SkCanvas.DrawPath(path, paint);
        }

        private SKPath Path(double time, int? segments)
        {
            var path = new SKPath { FillType = SKPathFillType.Winding };
            foreach (var bone in bones)
            {
                var points = bone.Capsule(time, segments);
                path.AddPoly([.. points], close: true);
            }

            return path;
        }

        /// <summary>
        /// Synfig's skeleton is an editor guide: excluded from rendering, its colour fixed, its only
        /// parameters <c>amount</c>, <c>name</c> and <c>bones</c>. So it is written that way, and draws only here.
        /// </summary>
        public override XElement ToSif(SifWriter sif) => new("layer",
            new XAttribute("type", "skeleton"), new XAttribute("active", "true"),
            new XAttribute("exclude_from_rendering", "true"), new XAttribute("version", "0.1"),
            Desc is null ? null : new XAttribute("desc", Desc),
            sif.Param("amount", Amount),
            SifWriter.Param("name", new XElement("string", "skeleton")),
            SifWriter.Param("bones", new XElement("static_list", new XAttribute("type", "bone_object"),
                bones.Select(b => new XElement("entry",
                    new XElement("bone", new XAttribute("type", "bone_object"), new XAttribute("guid", sif.Bone(b))))))));

        public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
        {
            const int segments = 12;
            var path = SvgElement("path");
            svg.Attribute(path, "d", t =>
            {
                var local = map.Local(t);
                return string.Concat(bones.Select(b =>
                {
                    var points = b.Capsule(local, segments);
                    return "M" + string.Join("L", points.Select(P)) + "Z";
                }));
            });
            Paint(path, "fill", color, svg, map);
            Opacity(path, svg, map);
            return path;
        }
    }
    #endregion
}

/// <summary>
/// One point of a region or outline: <c>{ point, t1?, t2?, width? }</c>.
/// </summary>
/// <remarks>
/// Tangents are Synfig's: the curve leaves a point toward <c>point + t2 / 3</c> and arrives at the next
/// toward <c>next − t1 / 3</c>, so a tangent is three times the distance to its Bézier control point.
/// <c>t2</c> defaults to <c>t1</c>, which keeps the point smooth; both default to zero, a corner.
/// </remarks>
internal sealed class MotionSplinePoint
{
    #region Fields
    private static readonly HashSet<string> Keys = ["point", "t1", "t2", "width", "bone"];
    private readonly MotionNode point, t1, t2, width;
    private readonly MotionBinding? binding;
    #endregion

    #region Constructors
    private MotionSplinePoint(MotionNode point, MotionNode t1, MotionNode t2, MotionNode width, MotionBinding? binding = null, string? who = null)
    {
        (this.point, this.t1, this.t2, this.width, this.binding) = (point, t1, t2, width, binding);
        if (binding is null) return;

        // Fixed in the rest pose and measured once in the bones' frame, as Synfig Studio does when a vertex is linked.
        if (point is not MotionConstant || t1 is not MotionConstant || t2 is not MotionConstant)
            throw new ArgumentException($"{who} follows a bone, so its point and tangents are fixed values in the rest pose, not nodes.");
        var inverse = binding.Rest.Invert();
        var p = point.Evaluate(0);
        var (px, py) = inverse.Apply(p[0], p[1]);
        this.point = new MotionConstant(MotionType.Vector, [px, py]);
        this.t1 = Vector(t1);
        this.t2 = ReferenceEquals(t1, t2) ? this.t1 : Vector(t2);

        MotionNode Vector(MotionNode t)
        {
            var v = t.Evaluate(0);
            var (x, y) = inverse.ApplyVector(v[0], v[1]);
            return new MotionConstant(MotionType.Vector, [x, y]);
        }
    }
    #endregion

    #region Methods
    public static IReadOnlyList<MotionSplinePoint> ReadAll(object? value, string who)
    {
        if (value is not IList list || list.Count == 0)
            throw new ArgumentException($"{who} takes points: an array of {{ point, t1?, t2?, width? }}.");

        return [.. list.Cast<object?>().Select((item, i) =>
        {
            var at = $"{who} point {i}";
            var d = JsInterop.AsDict(item);
            if (d is null) return new MotionSplinePoint(
                MotionNodeFactory.Node(item, MotionType.Vector, at),
                Zero(), Zero(), new MotionConstant(MotionType.Real, [1]));

            foreach (var key in d.Keys.Cast<object>().Select(k => k.ToString()!))
            {
                if (!Keys.Contains(key) && key is not ("x" or "y"))
                    throw new ArgumentException($"{at} has no '{key}'. A point takes: point, t1, t2, width, bone.");
            }

            var p = d.Contains("point") ? d["point"] : item;
            var t1 = d["t1"] is { } a ? MotionNodeFactory.Node(a, MotionType.Vector, $"{at}'s t1") : Zero();
            var t2 = d["t2"] is { } b ? MotionNodeFactory.Node(b, MotionType.Vector, $"{at}'s t2") : t1;
            return new MotionSplinePoint(
                MotionNodeFactory.Node(p, MotionType.Vector, $"{at}'s point"), t1, t2,
                d["width"] is { } w ? MotionNodeFactory.Node(w, MotionType.Real, $"{at}'s width") : new MotionConstant(MotionType.Real, [1]),
                d["bone"] is { } bone ? MotionBinding.Parse(bone, $"{at}'s bone") : null, at);
        })];

        static MotionNode Zero() => new MotionConstant(MotionType.Vector, [0, 0]);
    }

    public State At(double time, double[] origin)
    {
        var p = point.Evaluate(time);
        var a = t1.Evaluate(time);
        var b = t2.Evaluate(time);
        if (binding?.At(time) is { } m)
        {
            // The vertex by the whole frame, the tangents by its linear part, as Synfig carries a linked point.
            var (px, py) = m.Apply(p[0], p[1]);
            var (ax, ay) = m.ApplyVector(a[0], a[1]);
            var (bx, by) = m.ApplyVector(b[0], b[1]);
            (p, a, b) = ([px, py], [ax, ay], [bx, by]);
        }

        return new State(
            new SKPoint((float)(p[0] + origin[0]), (float)(p[1] + origin[1])),
            new SKPoint((float)a[0], (float)a[1]),
            new SKPoint((float)b[0], (float)b[1]),
            width.Evaluate(time)[0]);
    }

    public IEnumerable<MotionNode> Nodes() => [point, t1, t2, width];

    public XElement ToSif(SifWriter sif)
    {
        var composite = new XElement("composite", new XAttribute("type", "bline_point"),
            sif.Link("point", point),
            sif.Link("width", width),
            new XElement("origin", new XElement("real", new XAttribute("value", "0.5"))),
            new XElement("split", SifWriter.Bool(true)),
            sif.Link("t1", t1),
            sif.Link("t2", t2),
            new XElement("split_radius", SifWriter.Bool(true)),
            new XElement("split_angle", SifWriter.Bool(true)));
        return binding is null ? composite : binding.Wrap(sif, "bline_point", composite);
    }
    #endregion

    #region Types
    public readonly record struct State(SKPoint P, SKPoint T1, SKPoint T2, double Width);
    #endregion
}

/// <summary>An options object read strictly: an unknown key is refused by name.</summary>
internal sealed class MotionOptions
{
    #region Constructors
    public MotionOptions(object? options, string who, params string[] allowed)
    {
        this.who = who;
        values = JsInterop.AsDict(options) ?? (options is null
            ? new Hashtable()
            : throw new ArgumentException($"{who} takes an options object."));

        foreach (var key in values.Keys.Cast<object>().Select(k => k.ToString()!))
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
                throw new ArgumentException($"{who} has no '{key}'. It takes: {string.Join(", ", allowed)}.");
        }
    }
    #endregion

    #region Fields
    private readonly IDictionary values;
    private readonly string who;
    #endregion

    #region Properties
    /// <summary>The call the options belong to, for messages.</summary>
    public string Who => who;
    #endregion

    #region Methods
    /// <summary>The given keys that were set, as a new options object.</summary>
    public Hashtable Subset(params string[] keys)
    {
        var subset = new Hashtable();
        foreach (var key in keys.Where(Has)) subset[key] = values[key];
        return subset;
    }

    public bool Has(string key) => values.Contains(key) && values[key] is not null;

    public object? Raw(string key) => values[key];

    /// <remarks>
    /// What a script passes must be finite (<see cref="MotionTypes.Read"/>); a default may be NaN, as a text selector's
    /// <c>end</c> is for "not set", so a fallback that is not finite is taken as it is.
    /// </remarks>
    public MotionNode Node(string key, MotionType kind, object fallback) =>
        !Has(key) && fallback is double d && !double.IsFinite(d)
            ? new MotionConstant(kind, [d])
            : MotionNodeFactory.Node(Has(key) ? values[key] : fallback, kind, $"{who}'s {key}");

    public double Number(string key, double fallback) =>
        Has(key) ? MotionTypes.Read(values[key], MotionType.Real, $"{who}'s {key}")[0] : fallback;

    public bool Bool(string key, bool fallback) => !Has(key) ? fallback : values[key] is bool b
        ? b : throw new ArgumentException($"{who}'s {key} is true or false.");
    #endregion
}

/// <summary>
/// Writes a composition as Synfig's <c>.sif</c>: a node used in more than one place goes into
/// <c>&lt;defs&gt;</c> once and is referenced, so a link stays a link in Synfig.
/// </summary>
internal sealed class SifWriter
{
    #region Constructors
    public SifWriter(MotionComposition composition, string? stem)
    {
        this.composition = composition;
        this.stem = stem;

        var uses = new Dictionary<MotionNode, int>(ReferenceEqualityComparer.Instance);
        foreach (var node in composition.AllNodes()) Count(node);
        for (var i = 0; i < composition.Bones.Count; i++) bones[composition.Bones[i]] = Guid(i + 1);

        // A skeleton deformation pairs each bone with a fixed copy of it at rest; those copies, and their
        // ancestors so that they hang from the same places, go into the bones section too.
        var deformed = composition.AllLayers().OfType<MotionDeformationLayer>().SelectMany(d => d.Bones)
            .SelectMany(b => Ancestry(b)).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var b in composition.Bones.Where(deformed.Contains)) restBones[b] = "B07D" + (restBones.Count + 1).ToString("X28");

        static IEnumerable<MotionBone> Ancestry(MotionBone b)
        {
            for (var x = b; x is not null; x = x.Parent) yield return x;
        }

        // Children before parents, because Synfig refuses a reference to a def it has not read yet.
        var ordered = new List<MotionNode>();
        var seen = new HashSet<MotionNode>(ReferenceEqualityComparer.Instance);
        foreach (var node in composition.AllNodes()) Visit(node);
        foreach (var node in ordered.Where(n => uses[n] > 1)) ids[node] = $"v{ids.Count + 1}";
        exportOrder = [.. ordered.Where(ids.ContainsKey)];

        void Count(MotionNode node)
        {
            uses[node] = uses.GetValueOrDefault(node) + 1;
            if (uses[node] == 1) foreach (var child in node.Children) Count(child);
        }

        void Visit(MotionNode node)
        {
            if (!seen.Add(node)) return;
            foreach (var child in node.Children) Visit(child);
            ordered.Add(node);
        }
    }
    #endregion

    #region Fields
    private readonly MotionComposition composition;
    private readonly string? stem;
    private readonly Dictionary<MotionNode, string> ids = new(ReferenceEqualityComparer.Instance);
    private readonly MotionNode[] exportOrder;
    private readonly Dictionary<string, string> pictures = [];
    private readonly Dictionary<MotionBone, string> bones = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MotionBone, string> restBones = new(ReferenceEqualityComparer.Instance);
    private readonly List<(string Name, byte[] Png)> files = [];
    #endregion

    #region Properties
    /// <summary>The pictures the file refers to, by the names it gives them.</summary>
    public IReadOnlyList<(string Name, byte[] Png)> Pictures => files;

    public double Fps => composition.Fps;

    public double Duration => composition.Duration;
    #endregion

    #region Methods
    /// <summary>The guid a bone is written and referred to under.</summary>
    public string Bone(MotionBone bone) => bones.TryGetValue(bone, out var guid)
        ? guid
        : throw new InvalidOperationException($"Bone '{bone.Name}' belongs to another composition, so this one cannot write it.");

    /// <summary>The guid of a bone's fixed rest copy, which a skeleton deformation pairs it with.</summary>
    public string RestBone(MotionBone bone) => restBones.TryGetValue(bone, out var guid)
        ? guid
        : throw new InvalidOperationException($"Bone '{bone.Name}' has no rest copy in this file.");

    // Synfig's guids are 32 hex digits. These only have to be distinct within the file.
    private static string Guid(int n) => "B07E" + n.ToString("X28");

    /// <summary>
    /// The <c>&lt;bones&gt;</c> section: Synfig's root bone, then every bone parent first, each with its
    /// links. It comes before <c>&lt;defs&gt;</c>, where Synfig expects it and allows references forward.
    /// </summary>
    private XElement? BonesSection()
    {
        if (bones.Count == 0) return null;
        const string root = "B07E" + "FFFFFFFFFFFFFFFFFFFFFFFFFFFF";
        return new XElement("bones",
            new XElement("bone_root", new XAttribute("type", "bone_object"), new XAttribute("guid", root)),
            composition.Bones.Select(b => new XElement("bone", new XAttribute("type", "bone_object"), new XAttribute("guid", bones[b]),
                new XElement("name", new XElement("string", b.Name)),
                new XElement("parent", new XElement("bone_valuenode", new XAttribute("type", "bone_object"),
                    new XAttribute("guid", b.Parent is null ? root : bones[b.Parent]))),
                Link("origin", b.Origin), Link("angle", b.Angle), Link("scalelx", b.Scalelx), Link("width", b.Width),
                Link("scalex", b.Scalex), Link("tipwidth", b.Tipwidth),
                new XElement("bone_depth", new XElement("real", new XAttribute("value", "0"))),
                Link("length", b.Length))),
            composition.Bones.Where(restBones.ContainsKey).Select(b => new XElement("bone",
                new XAttribute("type", "bone_object"), new XAttribute("guid", restBones[b]),
                new XElement("name", new XElement("string", b.Name + " (rest)")),
                new XElement("parent", new XElement("bone_valuenode", new XAttribute("type", "bone_object"),
                    new XAttribute("guid", b.Parent is null ? root : restBones[b.Parent]))),
                Fixed("origin", MotionType.Vector, b.Origin.Evaluate(0)),
                Fixed("angle", MotionType.Angle, [b.RestAngle]),
                Fixed("scalelx", MotionType.Real, [b.RestScalelx]),
                Fixed("width", MotionType.Real, b.Width.Evaluate(0)),
                Fixed("scalex", MotionType.Real, b.Scalex.Evaluate(0)),
                Fixed("tipwidth", MotionType.Real, b.Tipwidth.Evaluate(0)),
                new XElement("bone_depth", new XElement("real", new XAttribute("value", "0"))),
                Fixed("length", MotionType.Real, b.Length.Evaluate(0)))));

        static XElement Fixed(string name, MotionType kind, double[] value) => new(name, Value(kind, value));
    }

    /// <summary>The file name an image layer's picture is written under, beside the <c>.sif</c>.</summary>
    public string Picture(MotionPicture picture)
    {
        if (stem is null)
            throw new InvalidOperationException(
                "This composition has an image layer, whose picture is written beside the .sif: use composition.saveSif(path) instead of toSif().");
        // Keyed on the pixels, so the pieces of one cut-out sheet share one file.
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(picture.Png));
        if (pictures.TryGetValue(key, out var name)) return name;
        name = $"{stem}-image{pictures.Count + 1}.png";
        pictures[key] = name;
        files.Add((name, picture.Png));
        return name;
    }

    public string Write()
    {
        var c = composition;
        var v = c.View;
        var root = new XElement("canvas",
            new XAttribute("version", "1.2"),
            new XAttribute("width", c.Width), new XAttribute("height", c.Height),
            new XAttribute("xres", "2834.645669"), new XAttribute("yres", "2834.645669"),
            new XAttribute("gamma-r", "1"), new XAttribute("gamma-g", "1"), new XAttribute("gamma-b", "1"),
            new XAttribute("view-box", string.Join(" ", v.Select(MotionTypes.Format))),
            new XAttribute("antialias", "1"),
            new XAttribute("fps", MotionTypes.Format(c.Fps)),
            new XAttribute("begin-time", "0s"),
            new XAttribute("end-time", MotionTypes.Time(c.Duration)),
            new XElement("name", "Polson"),
            BonesSection(),
            exportOrder.Length == 0 ? null : new XElement("defs", exportOrder.Select(n =>
            {
                var e = Inline(n);
                e.Add(new XAttribute("id", ids[n]));
                return e;
            })),
            c.LayersToSif(this));

        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings)) new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A layer parameter: the node inline, or a reference to its def.</summary>
    public XElement Param(string name, MotionNode node) => ids.TryGetValue(node, out var id)
        ? new XElement("param", new XAttribute("name", name), new XAttribute("use", id))
        : new XElement("param", new XAttribute("name", name), Inline(node));

    /// <summary>A link inside a linkable node, written as a child element; a shared node becomes an attribute.</summary>
    public object Link(string name, MotionNode node) => ids.TryGetValue(node, out var id)
        ? new XAttribute(name, id)
        : new XElement(name, Inline(node));

    public XElement Linkable(string element, string type, params (string Name, MotionNode Node)[] links) =>
        new(element, new XAttribute("type", type), links.Select(l => Link(l.Name, l.Node)));

    public static XElement Param(string name, XElement value) => new("param", new XAttribute("name", name), value);

    public static XElement Bool(bool value) => new("bool", new XAttribute("value", value ? "true" : "false"));

    public static XElement Value(MotionType kind, double[] v) => kind switch
    {
        MotionType.Real => new XElement("real", new XAttribute("value", MotionTypes.Format(v[0]))),
        MotionType.Angle => new XElement("angle", new XAttribute("value", MotionTypes.Format(v[0]))),
        MotionType.Vector => new XElement("vector",
            new XElement("x", MotionTypes.Format(v[0])), new XElement("y", MotionTypes.Format(v[1]))),
        _ => new XElement("color",
            new XElement("r", MotionTypes.Format(v[0])), new XElement("g", MotionTypes.Format(v[1])),
            new XElement("b", MotionTypes.Format(v[2])), new XElement("a", MotionTypes.Format(v[3])))
    };

    private XElement Inline(MotionNode node) => node.ToSif(this);
    #endregion
}
