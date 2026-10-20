namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Polson.Drawing.Skia;
using Polson.Drawing.Svg;

using SkiaSharp;

using HB = HarfBuzzSharp;

using static MotionTextSelectors;

/// <summary>
/// A text in a composition, as <c>comp.text(...)</c> returns it: where its letters are at any time.
/// </summary>
/// <remarks>
/// <para>
/// Asked for by both trailer runs: each built its title-safe check by measuring the type some other way — rebuilding
/// the composition with text only and diffing renders, or laying the line out again with <c>ctx.measureText</c> on the
/// assumption that it would match. Read from the layer itself, the boxes are the letters as drawn, animators and all,
/// with no rendering.
/// </para>
/// <para>
/// Boxes are in the units of the stack the text was added to, and times are that stack's: at the composition's top
/// level, the frame's units and the film's seconds. <c>{ frame: true }</c> carries them out through every group above —
/// transform, clock and switch — into the rendered frame's pixels at the film's time, which is what a title-safe check
/// measures. trailer2's title sat inside a camera group, where the stack's own units say nothing about the frame.
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto them,
/// so a script calling <c>x.doThing()</c> reaches <c>DoThing()</c>. The camelCase form is the one documented in
/// <c>docs/Polson.core.md</c> and the studio manuals.
/// </para>
/// </remarks>
public sealed class MotionText
{
    #region Constructors
    internal MotionText(MotionTextLayer layer, MotionLayerList stack) => (this.layer, this.stack) = (layer, stack);
    #endregion

    #region Fields
    private readonly MotionTextLayer layer;
    private readonly MotionLayerList stack;
    #endregion

    #region Properties
    /// <summary>The words, as given.</summary>
    public string Text => layer.Words;
    #endregion

    #region Methods
    /// <summary>
    /// The ink box of every visible letter at <paramref name="time"/> seconds, animators applied, as
    /// <c>{ x, y, width, height }</c>; null when nothing shows. A blur's spread is not included. <c>options</c>:
    /// <c>{ frame? }</c>, true for the rendered frame's pixels at the film's time.
    /// </summary>
    public Dictionary<string, object?>? BoundsAt(double time, object? options = null)
    {
        var letters = Letters(time, options, "boundsAt");
        if (letters.Count == 0) return null;
        return Box(letters.Skip(1).Aggregate(letters[0].Ink, (b, l) => SKRect.Union(b, l.Ink)));
    }

    /// <summary>
    /// Each visible letter at <paramref name="time"/> seconds: <c>{ text, x, y, width, height, opacity, index, word, line }</c>,
    /// its box as drawn and the character, word and line it counts as. Spaces and fully transparent letters are left out.
    /// <c>options</c> as <see cref="BoundsAt"/>.
    /// </summary>
    public List<Dictionary<string, object?>> LettersAt(double time, object? options = null) =>
        [.. Letters(time, options, "lettersAt").Select(l =>
        {
            var d = Box(l.Ink);
            d["text"] = l.Cluster;
            d["opacity"] = l.Opacity;
            d["index"] = l.Char;
            d["word"] = l.Word;
            d["line"] = l.Line;
            return d;
        })];

    /// <summary>
    /// When the text stops moving: from <paramref name="from"/> seconds on, the first frame from which no visible letter's
    /// box or opacity changes, and how long it then holds — <c>{ at, until, seconds }</c>, <c>until</c> the last frame
    /// before anything changes again (or the film's end); null when it is never visible and unchanged across a frame step.
    /// The layer's <c>amount</c> counts: a text faded out by it is not visible. <c>options</c>:
    /// <c>{ frame? }</c> as <see cref="BoundsAt"/>, true to count a text still only when it is still on screen, as a text in
    /// a moving group is not — and visible only while every group above shows it, their <c>amount</c>s included.
    /// </summary>
    /// <remarks>
    /// The reading-time check: trailer4 found when its tagline came to rest by diffing <c>boundsAt</c> and
    /// <c>lettersAt</c> frame by frame. Read at the composition's frame rate, to a hundredth of a unit and 1/500 of opacity.
    /// </remarks>
    public Dictionary<string, object?>? SettledAt(double from = 0, object? options = null)
    {
        var root = stack.Root ?? throw new InvalidOperationException("text.settledAt needs the text's composition.");
        var fps = root.Fps;
        var first = (int)Math.Ceiling(from * fps - 1e-9);
        var last = (int)Math.Floor(root.Duration * fps + 1e-9);
        var frames = Enumerable.Range(first, Math.Max(0, last - first + 1))
            .Select(f => (Frame: f, Letters: Letters(f / fps, options, "settledAt"))).ToList();

        for (var i = 0; i < frames.Count; i++)
        {
            // Still means unchanged across at least one frame step: the film's last frame alone has nothing to compare with.
            if (frames[i].Letters.Count == 0 || i + 1 >= frames.Count || !Same(frames[i].Letters, frames[i + 1].Letters)) continue;
            var j = i;
            while (j + 1 < frames.Count && Same(frames[i].Letters, frames[j + 1].Letters)) j++;
            return new()
            {
                ["at"] = frames[i].Frame / fps,
                ["until"] = frames[j].Frame / fps,
                ["seconds"] = (frames[j].Frame - frames[i].Frame) / fps,
            };
        }
        return null;

        static bool Same(List<(string Cluster, SKRect Ink, double Opacity, int Char, int Word, int Line)> a,
            List<(string Cluster, SKRect Ink, double Opacity, int Char, int Word, int Line)> b) =>
            a.Count == b.Count && a.Zip(b).All(p => p.First.Char == p.Second.Char
                && Math.Abs(p.First.Ink.Left - p.Second.Ink.Left) < 0.01 && Math.Abs(p.First.Ink.Top - p.Second.Ink.Top) < 0.01
                && Math.Abs(p.First.Ink.Right - p.Second.Ink.Right) < 0.01 && Math.Abs(p.First.Ink.Bottom - p.Second.Ink.Bottom) < 0.01
                && Math.Abs(p.First.Opacity - p.Second.Opacity) < 0.002);
    }

    /// <summary>
    /// The letters at a time in the stack's own terms, or carried out to the frame: each group above, outermost first,
    /// gives its transform and its children's clock, and a switch not showing the way down hides them.
    /// </summary>
    private List<(string Cluster, SKRect Ink, double Opacity, int Char, int Word, int Line)> Letters(double time, object? options, string who)
    {
        if (!new MotionOptions(options, $"text.{who}", "frame").Bool("frame", false)) return layer.Letters(stack.TimeFor(layer, time));

        var (space, local, own, visible) = stack.SpaceAt(time);
        if (!visible || stack is MotionGroup g && !g.Layer.Shows(own, layer)) return [];
        // A group above fades its text as surely as the text's own amount does: a scene cut away by its group's amount
        // going to 0 took its name with it, and productad1's reading-time check still read the name as held to the end.
        var above = stack.AmountAbove(time);
        if (above <= 0) return [];
        var matrix = SKMatrix.Concat(stack.Root?.ViewMatrix() ?? SKMatrix.Identity, space.Plane);
        return [.. layer.Letters(stack.TimeFor(layer, local))
            .Select(l => l with { Ink = matrix.MapRect(l.Ink), Opacity = l.Opacity * above })];
    }

    private static Dictionary<string, object?> Box(SKRect r) => new()
    {
        ["x"] = (double)r.Left,
        ["y"] = (double)r.Top,
        ["width"] = (double)r.Width,
        ["height"] = (double)r.Height,
    };
    #endregion
}

/// <summary>
/// Synfig's text layer, with After Effects' text animators on top.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layer is ported from Synfig</b> (<c>modules/lyr_freetype/lyr_freetype.cpp</c>, GPL-2-or-later): one string,
/// split into lines, each glyph's unhinted outline placed by its advance × <c>compress</c>, lines stepped by the font's
/// height × <c>vcompress</c>, the block aligned on <c>origin</c> by <c>orient</c> — x by each line's width, y by the
/// height from the first line's tallest glyph to the last baseline — and the whole filled as one shape, scaled from font
/// units by <c>size / unitsPerEm</c>. Synfig's own <c>size</c> is half the em, and y up; ours is the em, upright in the
/// composition's view, and both are converted on the way to <c>.sif</c>. Shaping is HarfBuzz's, as Synfig's is.
/// </para>
/// <para>
/// <b>The animators are After Effects'</b>, by way of EffectCraft's selector maths (<see cref="MotionTextSelectors"/>,
/// MIT): each animator's properties are offsets that a letter, word or line takes in proportion to how far the animator's
/// selectors pick it. Synfig has no animators, so a layer with them is written to <c>.sif</c> as a group per letter, each
/// a one-glyph text layer, with what the animators do sampled at the frame rate.
/// </para>
/// <para><b>A family that is not installed is refused</b>, where Synfig falls back to sans silently.</para>
/// </remarks>
internal sealed class MotionTextLayer : MotionLayer
{
    #region Constructors
    public MotionTextLayer(MotionOptions o, bool yDown, MotionComposition? root) : base(o)
    {
        (this.yDown, this.root, who) = (yDown, root, o.Who);
        text = (o.Raw("text") as string ?? o.Raw("text")?.ToString() ?? "").Replace("\r\n", "\n");
        if (text.Trim().Length == 0) throw new ArgumentException($"{o.Who} needs some text: {{ text: 'BE COOL', family: 'Arial' }}.");

        family = (o.Raw("family") as string)?.Trim() is { Length: > 0 } f
            ? f : throw new ArgumentException($"{o.Who} needs a family, an installed font: {{ family: 'Arial' }}. Skia.Font.families() lists them.");
        weight = Weight(o.Raw("weight"), o.Who);
        style = o.Raw("style")?.ToString()?.Trim().ToLowerInvariant() switch
        {
            null or "normal" => 0,
            "oblique" => 1,
            "italic" => 2,
            var s => throw new ArgumentException($"{o.Who}'s style is 'normal', 'oblique' or 'italic'; '{s}' is not."),
        };
        kerning = o.Bool("kerning", true);
        invert = o.Bool("invert", false);

        size = o.Node("size", MotionType.Real, 48d);
        origin = o.Node("origin", MotionType.Vector, new[] { 0d, 0d });
        orient = o.Node("orient", MotionType.Vector, new[] { 0.5, 0.5 });
        color = o.Node("color", MotionType.Color, "#000000");
        compress = o.Node("compress", MotionType.Real, 1d);
        vcompress = o.Node("vcompress", MotionType.Real, 1d);

        animators = o.Has("animators") ? Animator.ReadAll(o.Raw("animators"), o.Who) : [];
        if (invert && animators.Length > 0)
            throw new ArgumentException($"{o.Who} cannot invert and animate at once: an inverted text is one shape, and animators move its letters apart.");

        shape = TextShape.Build(text, Typeface(family, weight, style, o.Who), kerning);
    }

    /// <summary>A one-glyph layer for the export of an animated text, its nodes given rather than read.</summary>
    private MotionTextLayer(MotionTextLayer of, string cluster, MotionNode size, MotionNode origin, MotionNode color)
        : base(new MotionOptions(null, of.who))
    {
        (yDown, root, who, text, family, weight, style, kerning, invert) =
            (of.yDown, of.root, of.who, cluster, of.family, of.weight, of.style, false, false);
        (this.size, this.origin, this.color) = (size, origin, color);
        orient = MotionNodeFactory.Node(new[] { 0d, 0d }, MotionType.Vector, who);
        compress = vcompress = MotionNodeFactory.Node(1d, MotionType.Real, who);
        animators = [];
        shape = TextShape.Build(text, of.shape.Typeface, false);
    }
    #endregion

    #region Fields
    private readonly bool yDown, kerning, invert;
    private readonly MotionComposition? root;
    private readonly string who, text, family;
    private readonly int weight, style;
    private readonly MotionNode size, origin, orient, color, compress, vcompress;
    private readonly Animator[] animators;
    private readonly TextShape shape;

    private static readonly Dictionary<string, int> Weights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["thin"] = 100, ["ultralight"] = 200, ["light"] = 300, ["semilight"] = 350, ["book"] = 380, ["normal"] = 400,
        ["medium"] = 500, ["semibold"] = 600, ["bold"] = 700, ["ultrabold"] = 800, ["heavy"] = 900, ["ultraheavy"] = 1000,
    };
    #endregion

    #region Methods
    public override IEnumerable<MotionNode> Nodes() =>
        new[] { Amount, size, origin, orient, color, compress, vcompress }.Concat(animators.SelectMany(a => a.Nodes()));

    protected override void Draw(SkiaCanvas surface, double time, double amount)
    {
        var frame = Frame(time);
        var canvas = surface.SkCanvas;
        var baseColor = color.Evaluate(time);

        if (animators.Length == 0)
        {
            // One shape, as Synfig fills it: overlapping glyphs show no seam.
            using var all = new SKPath { FillType = invert ? SKPathFillType.InverseWinding : SKPathFillType.Winding };
            foreach (var g in frame.Glyphs.Where(g => g.Path is not null)) all.AddPath(g.Path!, g.Matrix);
            using var paint = Paint(baseColor, amount);
            canvas.DrawPath(all, paint);
            return;
        }

        foreach (var g in frame.Glyphs.Where(g => g.Path is not null && g.State.Opacity > 0))
        {
            var c = g.State.FillK > 0 && g.State.Fill is { } f ? Lerp(baseColor, f, g.State.FillK) : baseColor;
            using var paint = Paint(c, amount * Math.Clamp(g.State.Opacity, 0, 1));
            if (g.State.Blur > 1e-9)
            {
                // The blur layer's Gaussian, σ = ⅜ · size, in the glyph's own units under its matrix.
                var sigma = (float)(0.375 * g.State.Blur / Math.Abs(frame.Scale));
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma, respectCTM: true);
            }
            canvas.Save();
            canvas.Concat(g.Animated);
            canvas.Concat(g.Matrix);
            canvas.DrawPath(g.Path!, paint);
            canvas.Restore();
        }
    }

    public override XElement ToSif(SifWriter sif)
    {
        if (animators.Length > 0) return PerGlyph().Layer.ToSif(sif);

        // Synfig's size is half the em, and y up: upright in a y-down view needs its y negative.
        var half = MotionNodeFactory.Node(new[] { 0.5, yDown ? -0.5 : 0.5 }, MotionType.Vector, who);
        var sizeParam = size is MotionConstant
            ? SifWriter.Param("size", SifWriter.Value(MotionType.Vector, [size.Evaluate(0)[0] * 0.5, size.Evaluate(0)[0] * (yDown ? -0.5 : 0.5)]))
            : SifWriter.Param("size", sif.Linkable("scale", "vector", ("link", half), ("scalar", size)));

        return Element("text", "0.5", sif,
            SifWriter.Param("text", new XElement("string", text)),
            sif.Param("color", color),
            SifWriter.Param("family", new XElement("string", SifFamily(shape.Typeface))),
            SifWriter.Param("style", new XElement("integer", new XAttribute("value", style))),
            SifWriter.Param("weight", new XElement("integer", new XAttribute("value", weight))),
            SifWriter.Param("direction", new XElement("integer", new XAttribute("value", 0))),
            sif.Param("compress", compress),
            sif.Param("vcompress", vcompress),
            sizeParam,
            sif.Param("orient", orient),
            sif.Param("origin", SifOrigin()),
            SifWriter.Param("use_kerning", SifWriter.Bool(kerning)),
            SifWriter.Param("grid_fit", SifWriter.Bool(false)),
            SifWriter.Param("invert", SifWriter.Bool(invert)));
    }

    /// <summary>
    /// Outlines, not <c>&lt;text&gt;</c>: the SVG then needs no font and draws what this draws. Without animators the
    /// glyphs are one path under the origin's translation; with them, a path per glyph under its sampled transform.
    /// </summary>
    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map)
    {
        var keyed = new[] { ("size", size), ("compress", compress), ("vcompress", vcompress), ("orient", orient) }
            .Where(p => !svg.Track(p.Item2, 0, map).IsStatic || (p.Item2.Kind == MotionType.Vector && !svg.Track(p.Item2, 1, map).IsStatic))
            .Select(p => p.Item1).ToArray();
        if (keyed.Length > 0)
            throw new InvalidOperationException(
                $"The text{(Desc is null ? "" : $" '{Desc}'")} has its {string.Join(" and ", keyed)} keyed, which changes the letters' "
                + "outlines frame by frame — more than SVG can say without a path per frame. Key its origin, or animate it with "
                + "animators, or capture the composition as frames.");
        if (animators.Any(a => a.Blurs))
            throw new InvalidOperationException(
                $"The text{(Desc is null ? "" : $" '{Desc}'")} blurs its letters with an animator, which SVG cannot say per letter. "
                + "Blur the whole text with a blur layer above it, or capture the composition as frames.");

        if (animators.Length == 0)
        {
            var start = map.Local(svg.Times[0]);
            var o0 = origin.Evaluate(start);
            var frame = Frame(start);
            using var all = new SKPath();
            foreach (var g in frame.Glyphs.Where(g => g.Path is not null))
                all.AddPath(g.Path!, SKMatrix.Concat(SKMatrix.CreateTranslation((float)-o0[0], (float)-o0[1]), g.Matrix));

            var path = SvgElement("path", new XAttribute("d", (invert ? SvgAnimationWriter.Everything : "") + all.ToSvgPathData()),
                invert ? new XAttribute("fill-rule", "evenodd") : null);
            Paint(path, "fill", color, svg, map);
            var outer = SvgElement("g");
            Opacity(outer, svg, map);
            var x = svg.Transform("translate", svg.Track(origin, 0, map), v => $"{SvgAnimationWriter.N(v)} 0", 0);
            var y = svg.Transform("translate", svg.Track(origin, 1, map), v => $"0 {SvgAnimationWriter.N(v)}", 0);
            y.Add(path);
            x.Add(y);
            outer.Add(x);
            return outer;
        }

        // Animated: one frame per sample time, computed once and read by every glyph's tracks.
        var frames = svg.Times.ToDictionary(t => t, t => Frame(map.Local(t)));
        var group = SvgElement("g");
        Opacity(group, svg, map);
        var first = frames[svg.Times[0]];
        for (var i = 0; i < first.Glyphs.Length; i++)
        {
            if (first.Glyphs[i].Path is null) continue;
            var k = i;
            using var placed = new SKPath();
            placed.AddPath(first.Glyphs[k].Path!, first.Glyphs[k].Matrix);
            var glyph = new XElement(SvgAnimationWriter.Svg + "path", new XAttribute("d", placed.ToSvgPathData()));
            svg.Attribute(glyph, "fill", t =>
            {
                var s = frames[t].Glyphs[k].State;
                var c = MotionTypes.ToSkColor(s.FillK > 0 && s.Fill is { } f ? Lerp(color.Evaluate(map.Local(t)), f, s.FillK) : color.Evaluate(map.Local(t)));
                return $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
            });

            var anchor = first.Glyphs[k].Anchor;
            var chain = new[]
            {
                svg.Transform("translate", svg.Track(t => frames[t].Glyphs[k].Anchor.X + frames[t].Glyphs[k].State.Dx), v => $"{SvgAnimationWriter.N(v)} 0", 0),
                svg.Transform("translate", svg.Track(t => frames[t].Glyphs[k].Anchor.Y + frames[t].Glyphs[k].State.Dy), v => $"0 {SvgAnimationWriter.N(v)}", 0),
                svg.Transform("rotate", svg.Track(t => frames[t].Glyphs[k].State.Rotation), SvgAnimationWriter.N, 0),
                svg.Transform("scale", svg.Track(t => frames[t].Glyphs[k].State.Sx), v => $"{SvgAnimationWriter.N(v, 6)} 1", 1),
                svg.Transform("scale", svg.Track(t => frames[t].Glyphs[k].State.Sy), v => $"1 {SvgAnimationWriter.N(v, 6)}", 1),
                new XElement(SvgAnimationWriter.Svg + "g", new XAttribute("transform",
                    $"translate({SvgAnimationWriter.N(-anchor.X)} {SvgAnimationWriter.N(-anchor.Y)})")),
            };
            var opacity = svg.Track(t => Math.Clamp(frames[t].Glyphs[k].State.Opacity, 0, 1));
            var outer = new XElement(SvgAnimationWriter.Svg + "g");
            if (!(opacity.IsStatic && Math.Abs(opacity.Values[0] - 1) < 1e-12)) svg.Attribute(outer, "opacity", opacity);

            object inner = glyph;
            for (var c = chain.Length - 1; c >= 0; c--)
            {
                chain[c].Add(inner);
                inner = chain[c];
            }
            outer.Add(inner);
            group.Add(outer);
        }
        return group;
    }

    /// <summary>
    /// The layout and every glyph's animated state at a time: where each glyph's outline goes (<see cref="Glyph.Matrix"/>,
    /// font units to the composition's) and what the animators add about its anchor (<see cref="Glyph.Animated"/>).
    /// </summary>
    internal TextFrame Frame(double time)
    {
        var em = size.Evaluate(time)[0];
        var o = origin.Evaluate(time);
        var or = orient.Evaluate(time);
        var hc = compress.Evaluate(time)[0];
        var vc = vcompress.Evaluate(time)[0];
        var s = em / shape.UnitsPerEm;
        var sy = yDown ? s : -s;           // font units are y down, as Skia gives them; contour y is up

        var states = Animate(time, em);

        // Synfig's layout in contour units (y up): pens per line, the first line's tallest glyph, the block's height.
        var glyphs = shape.Glyphs;
        var placedX = new double[glyphs.Length];
        var widths = new double[shape.LineCount];
        for (var line = 0; line < shape.LineCount; line++)
        {
            var pen = 0.0;
            foreach (var i in shape.LineGlyphs[line])
            {
                pen += states[i].TrackBefore;
                placedX[i] = pen;
                pen += glyphs[i].Advance * hc + states[i].TrackAfter;
            }
            widths[line] = pen;
        }

        var initialY = shape.FirstLineYMax;
        var height = initialY + (shape.LineCount - 1) * vc * shape.LineHeight;
        var result = new Glyph[glyphs.Length];
        for (var i = 0; i < glyphs.Length; i++)
        {
            var g = glyphs[i];
            var cx = placedX[i] - or[0] * widths[g.Line];
            var cy = -g.Line * shape.LineHeight * vc + or[1] * height - initialY;
            var matrix = new SKMatrix((float)s, 0, (float)(o[0] + cx * s), 0, (float)sy, (float)(o[1] - cy * sy), 0, 0, 1);

            // The anchor: the middle of the glyph's advance, on its baseline.
            var anchor = new SKPoint((float)(o[0] + (cx + g.Advance * hc / 2) * s), (float)(o[1] - cy * sy));
            var st = states[i];
            var animated = SKMatrix.CreateTranslation(anchor.X + (float)st.Dx, anchor.Y + (float)st.Dy)
                .PreConcat(SKMatrix.CreateRotationDegrees((float)st.Rotation))
                .PreConcat(SKMatrix.CreateScale((float)st.Sx, (float)st.Sy))
                .PreConcat(SKMatrix.CreateTranslation(-anchor.X, -anchor.Y));
            result[i] = new Glyph(g.Path, matrix, anchor, animated, st, cx, cy);
        }
        return new TextFrame(result, s, sy, o);
    }

    /// <summary>The text's words, as given.</summary>
    internal string Words => text;

    /// <summary>
    /// Each visible letter as drawn at a time: its ink box after every animator, in the stack's units, with what the
    /// selectors count it as. A letter at zero opacity is left out; a space has no ink and never appears.
    /// </summary>
    internal List<(string Cluster, SKRect Ink, double Opacity, int Char, int Word, int Line)> Letters(double time)
    {
        var frame = Frame(time);
        var letters = new List<(string, SKRect, double, int, int, int)>();
        // The layer's own amount fades every letter: a text faded out by it shows nothing, so it has no visible letters.
        var amount = AmountAt(time);
        for (var i = 0; i < frame.Glyphs.Length; i++)
        {
            var g = frame.Glyphs[i];
            if (g.Path is null) continue;
            var opacity = amount * (animators.Length == 0 ? 1 : Math.Clamp(g.State.Opacity, 0, 1));
            if (opacity <= 0) continue;

            using var placed = new SKPath(g.Path);
            placed.Transform(SKMatrix.Concat(g.Animated, g.Matrix));
            var s = shape.Glyphs[i];
            letters.Add((s.Cluster, placed.TightBounds, opacity, s.Char, s.Word, s.Line));
        }
        return letters;
    }

    /// <summary>Every glyph's accumulated animator state at a time — EffectCraft's <c>char_transforms</c>.</summary>
    private GlyphState[] Animate(double time, double em)
    {
        var states = new GlyphState[shape.Glyphs.Length];
        for (var i = 0; i < states.Length; i++) states[i] = GlyphState.Identity;
        foreach (var animator in animators)
        {
            var selection = animator.Select(shape, time);
            var p = animator.Evaluate(time);
            for (var i = 0; i < states.Length; i++)
            {
                var k3 = selection[i];
                if (k3[0] == 0 && k3[1] == 0 && k3[2] == 0) continue;
                var k = k3[0];
                ref var c = ref states[i];
                if (p.Position is { } pos) { c.Dx += pos[0] * k3[0]; c.Dy += pos[1] * k3[1]; }
                if (p.Scale is { } sc) { c.Sx *= 1 + (sc[0] - 1) * k3[0]; c.Sy *= 1 + (sc[1] - 1) * k3[1]; }
                if (p.Rotation is { } r) c.Rotation += r * k;
                if (p.Opacity is { } op) c.Opacity *= 1 + (op - 1) * k;
                if (p.Fill is { } f) { c.Fill = f; c.FillK = Math.Clamp(c.FillK + k, 0, 1); }
                if (p.Tracking is { } tr)
                {
                    // Thousandths of an em, as After Effects counts tracking; half before the letter, half after.
                    var units = tr * k / 1000 * shape.UnitsPerEm;
                    c.TrackBefore += units / 2;
                    c.TrackAfter += units / 2;
                }
                if (p.Blur is { } b) c.Blur += Math.Abs(b * k);
            }
        }
        return states;
    }

    private static double[] Lerp(double[] a, double[] b, double k) => [.. a.Select((v, i) => v + (b[i] - v) * k)];

    /// <summary>A weight by Synfig's name or as a number from 100 to 1000.</summary>
    private static int Weight(object? value, string who) => value switch
    {
        null => 400,
        string s when Weights.TryGetValue(s.Trim(), out var w) => w,
        string s => throw new ArgumentException($"{who}'s weight '{s}' is not one of {string.Join(", ", Weights.Keys)}, or a number from 100 to 1000."),
        _ when MotionTypes.Read(value, MotionType.Real, $"{who}'s weight")[0] is var n && n is >= 100 and <= 1000 => (int)Math.Round(n),
        _ => throw new ArgumentException($"{who}'s weight is a name ({string.Join(", ", Weights.Keys)}) or a number from 100 to 1000."),
    };

    /// <summary>
    /// The family name Synfig must be given to find this face: the font's own legacy family (<c>name</c> table, name ID 1),
    /// which is what fontconfig matches, rather than the family Windows groups it under. They differ for fonts that ship
    /// each weight as a family of its own: Windows calls Franklin Gothic Heavy a weight of "Franklin Gothic", fontconfig
    /// knows only "Franklin Gothic Heavy" — so trailer1's title, written as "Franklin Gothic", opened in Synfig in a
    /// fallback face. Falls back to the face's family when the table cannot be read.
    /// </summary>
    internal static string SifFamily(SKTypeface face)
    {
        const uint name = 0x6E616D65;   // 'name'
        if (!face.TryGetTableData(name, out var t) || t.Length < 6) return face.FamilyName;

        int U16(int at) => at + 1 < t.Length ? (t[at] << 8) | t[at + 1] : 0;
        var count = U16(2);
        var strings = U16(4);
        string? best = null;
        var bestRank = int.MaxValue;
        for (var r = 0; r < count; r++)
        {
            var rec = 6 + r * 12;
            if (rec + 12 > t.Length) break;
            int platform = U16(rec), encoding = U16(rec + 2), language = U16(rec + 4), id = U16(rec + 6);
            int length = U16(rec + 8), offset = U16(rec + 10);
            if (id != 1 || strings + offset + length > t.Length) continue;

            // Windows Unicode, US English first; then any Windows Unicode; then Mac Roman.
            var rank = platform == 3 && encoding is 1 or 0 ? (language == 0x409 ? 0 : 1) : platform == 1 && encoding == 0 ? 2 : -1;
            if (rank < 0 || rank >= bestRank) continue;
            var span = t.AsSpan(strings + offset, length);
            best = rank < 2 ? System.Text.Encoding.BigEndianUnicode.GetString(span) : System.Text.Encoding.Latin1.GetString(span);
            bestRank = rank;
        }
        return string.IsNullOrWhiteSpace(best) ? face.FamilyName : best.Trim();
    }

    /// <summary>The installed face for a family at a weight and style, or a refusal naming what is installed.</summary>
    private static SKTypeface Typeface(string family, int weight, int style, string who)
    {
        var slant = style switch { 1 => SKFontStyleSlant.Oblique, 2 => SKFontStyleSlant.Italic, _ => SKFontStyleSlant.Upright };
        var face = SKFontManager.Default.MatchFamily(family, new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, slant));
        if (face is not null && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase)) return face;

        face?.Dispose();
        var advice = FontAudit.Suggestion(family)?.Replace("font-weight", "weight").Replace("font-style", "style")
            ?? "Skia.Font.families() lists the installed families; Skia.Font.has(name) checks one.";
        throw new ArgumentException($"{who}'s family '{family}' is not installed here, and a text in a substitute face is not the type "
            + $"that was chosen. {advice}");
    }

    /// <summary>
    /// The animated text as Synfig can hold it: a group per glyph, each a one-glyph text layer placed where the glyph sits,
    /// in a group that turns, scales and moves it about its anchor and fades it, with a blur above it when an animator
    /// blurs — every one of those sampled at the frame rate, and held constant where it never changes.
    /// </summary>
    private MotionGroup PerGlyph()
    {
        var fps = root?.Fps ?? 24;
        var count = root?.FrameCount ?? 1;
        var times = Enumerable.Range(0, count).Select(f => f / fps).ToArray();
        var frames = times.Select(Frame).ToArray();

        var outer = new MotionGroup(new MotionOptions(new Hashtable { ["amount"] = Amount, ["blend"] = Blend.Name, ["desc"] = Desc },
            who, "amount", "blend", "desc"));
        for (var i = 0; i < shape.Glyphs.Length; i++)
        {
            var g = shape.Glyphs[i];
            if (g.Path is null) continue;
            var k = i;

            // The one-glyph layer is laid out by Synfig with orient [0, 0], which puts the glyph's own top (its first line's
            // tallest point, never below zero) on its origin: its baseline sits that far below, so the origin goes that far up.
            var top = Math.Max(0, g.YMax);
            var textOrigin = Sampled(times, frames, MotionType.Vector, f =>
            {
                var at = f.Glyphs[k];
                return [f.Origin[0] + at.ContourX * f.Scale, f.Origin[1] - (at.ContourY + top) * f.ScaleY];
            });
            var em = Sampled(times, frames, MotionType.Real, f => [Math.Abs(f.Scale) * shape.UnitsPerEm]);
            var fill = Sampled(times, frames, MotionType.Color, f =>
            {
                var s = f.Glyphs[k].State;
                var c = color.Evaluate(times[Array.IndexOf(frames, f)]);
                return s.FillK > 0 && s.Fill is { } fc ? Lerp(c, fc, s.FillK) : c;
            });

            var group = new MotionGroup(new MotionOptions(new Hashtable
            {
                ["origin"] = Sampled(times, frames, MotionType.Vector, f => [f.Glyphs[k].Anchor.X, f.Glyphs[k].Anchor.Y]),
                ["offset"] = Sampled(times, frames, MotionType.Vector, f => [f.Glyphs[k].Anchor.X + f.Glyphs[k].State.Dx, f.Glyphs[k].Anchor.Y + f.Glyphs[k].State.Dy]),
                ["angle"] = Sampled(times, frames, MotionType.Angle, f => [f.Glyphs[k].State.Rotation]),
                ["scale"] = Sampled(times, frames, MotionType.Vector, f => [f.Glyphs[k].State.Sx, f.Glyphs[k].State.Sy]),
                ["amount"] = Sampled(times, frames, MotionType.Real, f => [Math.Clamp(f.Glyphs[k].State.Opacity, 0, 1)]),
                ["desc"] = g.Cluster,
            }, who, "origin", "offset", "angle", "scale", "amount", "desc"));
            group.Add(new MotionTextLayer(this, g.Cluster, em, textOrigin, fill));
            if (animators.Any(a => a.Blurs))
                group.Blur(new Hashtable
                {
                    ["type"] = "gaussian",
                    ["size"] = Sampled(times, frames, MotionType.Vector, f => [f.Glyphs[k].State.Blur, f.Glyphs[k].State.Blur]),
                });
            outer.Add(group.Layer);
        }
        return outer;
    }

    /// <summary>
    /// <c>origin</c> as Synfig must be given it to put the text where this does. Synfig's text layer shares the shape
    /// layer's <c>origin</c>, and the shape layer translates the outline by it <i>before</i> the text layer scales and
    /// translates it again (<c>layer_shape.cpp</c> 349, <c>lyr_freetype.cpp</c> 1512): the text lands at
    /// <c>origin·(1 + S) + glyph·S</c>, S the glyph scale per axis — measured, (+4.9, −2.9) px for an H at (100, 60) at an
    /// em of 100. So it is written as <c>origin / (1 + S)</c>: exact when origin and size are constant, sampled per frame
    /// when either is keyed.
    /// </summary>
    private MotionNode SifOrigin()
    {
        double[] At(double t)
        {
            var o = origin.Evaluate(t);
            var s = size.Evaluate(t)[0] / shape.UnitsPerEm;
            return [o[0] / (1 + s), o[1] / (1 + (yDown ? -s : s))];
        }

        if (origin is MotionConstant && size is MotionConstant) return new MotionConstant(MotionType.Vector, At(0));
        var fps = root?.Fps ?? 24;
        var times = Enumerable.Range(0, root?.FrameCount ?? 1).Select(f => f / fps).ToArray();
        return Keys(MotionType.Vector, times, [.. times.Select(At)]);
    }

    /// <summary>A value sampled at every frame: a constant when it never changes, else linear keys where it changes.</summary>
    private static MotionNode Sampled(double[] times, TextFrame[] frames, MotionType kind, Func<TextFrame, double[]> at) =>
        Keys(kind, times, [.. frames.Select(at)]);

    /// <summary>
    /// Samples as keys, linear between them, keeping only the samples that a straight line between the keys kept either
    /// side of them would not reproduce: a hold becomes its two ends, a steady move its ends, and only curving spans keep
    /// a key a frame. Exact at every frame to <see cref="KeyTolerance"/>. trailer1's .sif was 56 MB with a key per letter
    /// per frame, most of them letters holding still.
    /// </summary>
    internal static MotionNode Keys(MotionType kind, double[] times, double[][] values)
    {
        if (values.All(v => v.Zip(values[0]).All(p => Math.Abs(p.First - p.Second) < 1e-9)))
            return new MotionConstant(kind, values[0]);

        var kept = new List<int> { 0 };
        for (var i = 1; i < times.Length - 1; i++)
            if (!Spans(kept[^1], i + 1)) kept.Add(i);
        kept.Add(times.Length - 1);

        return new MotionAnimated(kind, [.. kept.Select(i =>
            new MotionWaypoint(times[i], values[i], MotionEase.Linear, MotionEase.Linear, 0, 0, 0, 0))]);

        // Whether a straight line from sample a to sample b passes through every sample between them.
        bool Spans(int a, int b)
        {
            for (var k = a + 1; k < b; k++)
            {
                var f = (times[k] - times[a]) / (times[b] - times[a]);
                for (var c = 0; c < values[k].Length; c++)
                    if (Math.Abs(values[a][c] + (values[b][c] - values[a][c]) * f - values[k][c]) > KeyTolerance) return false;
            }
            return true;
        }
    }

    /// <summary>How far a dropped sample may sit from the line through its neighbours: far below a pixel, a degree or a colour step.</summary>
    private const double KeyTolerance = 1e-4;
    #endregion

    #region Types
    /// <summary>The text shaped once: glyphs, their outlines in font units (y down), advances and selector indices.</summary>
    internal sealed class TextShape
    {
        public required SKTypeface Typeface { get; init; }
        public required int UnitsPerEm { get; init; }
        /// <summary>FreeType's <c>face->height</c>: <c>hhea</c> ascender − descender + line gap.</summary>
        public required double LineHeight { get; init; }
        public required ShapedGlyph[] Glyphs { get; init; }
        public required int[][] LineGlyphs { get; init; }
        public required double FirstLineYMax { get; init; }
        public required int Chars { get; init; }
        public required int CharsNoSpace { get; init; }
        public required int Words { get; init; }
        public int LineCount => LineGlyphs.Length;

        public static TextShape Build(string text, SKTypeface typeface, bool kerning)
        {
            var upem = typeface.UnitsPerEm;
            using var font = new SKFont(typeface, upem) { Hinting = SKFontHinting.None, Subpixel = true, LinearMetrics = true };

            byte[] bytes;
            int index;
            using (var stream = typeface.OpenStream(out index))
            {
                bytes = new byte[stream.Length];
                stream.Read(bytes, bytes.Length);
            }
            // In memory HarfBuzz owns, not a managed array: shaping crashed the test host with an access violation in
            // hb_shape_full whenever enough had run before it to bring on a collection, and never alone — the signature of
            // native code reading bytes the GC has moved. HB.Blob.FromStream hands HarfBuzz a managed buffer.
            var native = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, native, bytes.Length);
            using var blob = new HB.Blob(native, bytes.Length, HB.MemoryMode.ReadOnly, () => System.Runtime.InteropServices.Marshal.FreeHGlobal(native));
            using var face = new HB.Face(blob, index);
            using var hb = new HB.Font(face);
            hb.SetScale(upem, upem);
            HB.Feature[] features = kerning ? [] : [HB.Feature.Parse("-kern")];

            var glyphs = new List<ShapedGlyph>();
            var lineGlyphs = new List<int[]>();
            int chars = 0, noSpace = 0, words = 0;
            var lines = text.Split('\n');
            for (var line = 0; line < lines.Length; line++)
            {
                var s = lines[line];
                var starts = StringInfo.ParseCombiningCharacters(s);
                var space = starts.Select((st, gi) => char.IsWhiteSpace(s, st)).ToArray();

                // Selector indices per grapheme: characters, characters without spaces, words. A line break ends a word.
                var charIndex = new int[starts.Length];
                var noSpaceIndex = new int[starts.Length];
                var wordIndex = new int[starts.Length];
                var inWord = false;
                for (var gi = 0; gi < starts.Length; gi++)
                {
                    charIndex[gi] = chars++;
                    noSpaceIndex[gi] = noSpace;
                    if (!space[gi])
                    {
                        noSpace++;
                        if (!inWord) { words++; inWord = true; }
                    }
                    else inWord = false;
                    wordIndex[gi] = Math.Max(0, words - 1);
                }

                var mine = new List<int>();
                if (s.Length > 0)
                {
                    using var buffer = new HB.Buffer();
                    buffer.AddUtf16(s);
                    buffer.GuessSegmentProperties();
                    hb.Shape(buffer, features);
                    var infos = buffer.GlyphInfos;
                    var positions = buffer.GlyphPositions;
                    var clusters = infos.Select(x => (int)x.Cluster).Distinct().Order().ToArray();
                    for (var k = 0; k < infos.Length; k++)
                    {
                        var cluster = (int)infos[k].Cluster;
                        var next = clusters.FirstOrDefault(c => c > cluster, s.Length);
                        var gi = Math.Max(0, Array.FindLastIndex(starts, st => st <= cluster));
                        var path = font.GetGlyphPath((ushort)infos[k].Codepoint);
                        if (path is { IsEmpty: true }) { path.Dispose(); path = null; }
                        glyphs.Add(new ShapedGlyph(path, positions[k].XAdvance, path is null ? 0 : -path.Bounds.Top,
                            s[cluster..next], line, charIndex[gi], noSpaceIndex[gi], wordIndex[gi], space[gi]));
                        mine.Add(glyphs.Count - 1);
                    }
                }
                lineGlyphs.Add([.. mine]);
            }

            var firstLine = lineGlyphs[0].Select(i => glyphs[i].YMax).DefaultIfEmpty(0).Max();
            return new TextShape
            {
                Typeface = typeface,
                UnitsPerEm = upem,
                LineHeight = HheaHeight(typeface, font),
                Glyphs = [.. glyphs],
                LineGlyphs = [.. lineGlyphs],
                FirstLineYMax = Math.Max(0, firstLine),
                Chars = chars,
                CharsNoSpace = noSpace,
                Words = words,
            };
        }

        /// <summary>The selector unit a glyph belongs to, and how many there are, for what a selector counts.</summary>
        public (int Index, int Count) Unit(BasedOn based, int glyph)
        {
            var g = Glyphs[glyph];
            return based switch
            {
                BasedOn.CharactersExcludingSpaces => (Math.Min(g.CharNoSpace, Math.Max(0, CharsNoSpace - 1)), CharsNoSpace),
                BasedOn.Words => (g.Word, Words),
                BasedOn.Lines => (g.Line, LineCount),
                _ => (g.Char, Chars),
            };
        }

        public int Count(BasedOn based) => based switch
        {
            BasedOn.CharactersExcludingSpaces => CharsNoSpace,
            BasedOn.Words => Words,
            BasedOn.Lines => LineCount,
            _ => Chars,
        };

        private static double HheaHeight(SKTypeface typeface, SKFont font)
        {
            const uint hhea = 0x68686561;   // 'hhea'
            if (typeface.TryGetTableData(hhea, out var t) && t.Length >= 10)
            {
                int I16(int at) => (short)((t[at] << 8) | t[at + 1]);
                var h = I16(4) - I16(6) + I16(8);
                if (h > 0) return h;
            }
            var m = font.Metrics;
            return m.Descent - m.Ascent + m.Leading;
        }
    }

    /// <summary>One shaped glyph: its outline in font units (y down; null for a space), advance, top, and indices.</summary>
    internal sealed record ShapedGlyph(SKPath? Path, double Advance, double YMax, string Cluster,
        int Line, int Char, int CharNoSpace, int Word, bool IsSpace);

    /// <summary>What the animators add to one glyph: offset, scale, turn, opacity, colour, tracking, blur.</summary>
    internal record struct GlyphState(double Dx, double Dy, double Sx, double Sy, double Rotation, double Opacity,
        double[]? Fill, double FillK, double TrackBefore, double TrackAfter, double Blur)
    {
        public static GlyphState Identity => new(0, 0, 1, 1, 0, 1, null, 0, 0, 0, 0);
    }

    /// <summary>A glyph at a time: its outline, where it goes, its anchor, what the animators do about it.</summary>
    internal readonly record struct Glyph(SKPath? Path, SKMatrix Matrix, SKPoint Anchor, SKMatrix Animated, GlyphState State,
        double ContourX, double ContourY);

    /// <summary>The whole text at a time. <c>Scale</c> is units per font unit; <c>ScaleY</c> carries the view's y sense.</summary>
    internal readonly record struct TextFrame(Glyph[] Glyphs, double Scale, double ScaleY, double[] Origin);

    /// <summary>
    /// One animator: properties a fully selected glyph takes, and selectors saying how fully each glyph is selected.
    /// <c>{ properties: { position?, scale?, rotation?, opacity?, fillColor?, tracking?, blur? }, selectors?: [...] }</c>,
    /// or <c>selector</c> for one.
    /// </summary>
    private sealed class Animator
    {
        private MotionNode? position, scale, rotation, opacity, fill, tracking, blur;
        private Selector[] selectors = [];

        public bool Blurs => blur is not null;

        public static Animator[] ReadAll(object? value, string who)
        {
            if (value is not IList list) throw new ArgumentException($"{who}'s animators is a list: [{{ properties: {{ ... }}, selector: {{ ... }} }}].");
            return [.. list.Cast<object?>().Select((a, i) => Read(a, $"{who}'s animators[{i}]"))];
        }

        private static Animator Read(object? value, string who)
        {
            var o = new MotionOptions(value, who, "properties", "selector", "selectors");
            var p = new MotionOptions(o.Raw("properties") ?? throw new ArgumentException($"{who} needs properties: {{ opacity: 0 }}, {{ position: [0, 40] }}, ..."),
                $"{who}'s properties", "position", "scale", "rotation", "opacity", "fillColor", "tracking", "blur");
            var a = new Animator
            {
                position = p.Has("position") ? p.Node("position", MotionType.Vector, 0) : null,
                scale = p.Raw("scale") switch
                {
                    null => null,
                    IConvertible n and not string => MotionNodeFactory.Node(new[] { Convert.ToDouble(n), Convert.ToDouble(n) }, MotionType.Vector, $"{who}'s scale"),
                    MotionNode { Kind: MotionType.Real } r => new MotionComposite(r, r),
                    _ => p.Node("scale", MotionType.Vector, 0),
                },
                rotation = p.Has("rotation") ? p.Node("rotation", MotionType.Angle, 0) : null,
                opacity = p.Has("opacity") ? p.Node("opacity", MotionType.Real, 0) : null,
                fill = p.Has("fillColor") ? p.Node("fillColor", MotionType.Color, 0) : null,
                tracking = p.Has("tracking") ? p.Node("tracking", MotionType.Real, 0) : null,
                blur = p.Has("blur") ? p.Node("blur", MotionType.Real, 0) : null,
            };
            if (o.Has("selector") && o.Has("selectors")) throw new ArgumentException($"{who} takes selector or selectors, not both.");
            var raw = o.Has("selectors")
                ? o.Raw("selectors") as IList ?? throw new ArgumentException($"{who}'s selectors is a list.")
                : o.Has("selector") ? new[] { o.Raw("selector") } : [];
            a.selectors = [.. raw.Cast<object?>().Select((s, i) => Selector.Read(s, $"{who}'s selectors[{i}]"))];
            return a;
        }

        public IEnumerable<MotionNode> Nodes() =>
            new[] { position, scale, rotation, opacity, fill, tracking, blur }.OfType<MotionNode>().Concat(selectors.SelectMany(s => s.Nodes()));

        public (double[]? Position, double[]? Scale, double? Rotation, double? Opacity, double[]? Fill, double? Tracking, double? Blur) Evaluate(double t) =>
            (position?.Evaluate(t), scale?.Evaluate(t), rotation?.Evaluate(t)[0], opacity?.Evaluate(t)[0], fill?.Evaluate(t),
             tracking?.Evaluate(t)[0], blur?.Evaluate(t)[0]);

        /// <summary>Each glyph's selection per dimension — EffectCraft's <c>animator_selection</c>. No selector selects all.</summary>
        public double[][] Select(TextShape shape, double t)
        {
            var n = shape.Glyphs.Length;
            if (selectors.Length == 0) return [.. Enumerable.Range(0, n).Select(_ => new[] { 1d, 1d, 1d })];

            var acc = new double[n][];
            for (var si = 0; si < selectors.Length; si++)
            {
                var sel = selectors[si];
                var count = shape.Count(sel.Based);
                var values = sel.Prepare(t, count);
                for (var gi = 0; gi < n; gi++)
                {
                    var (i, units) = shape.Unit(sel.Based, gi);
                    var prev = si == 0 ? [Initial(sel.Mode), Initial(sel.Mode), Initial(sel.Mode)] : acc[gi];
                    var v = values(i, units);
                    acc[gi] = [Combine(sel.Mode, prev[0], v[0]), Combine(sel.Mode, prev[1], v[1]), Combine(sel.Mode, prev[2], v[2])];
                }
            }
            return acc;
        }
    }

    /// <summary>A range or wiggly selector, its parameters in After Effects' units (percent, degrees).</summary>
    private sealed class Selector
    {
        private bool wiggly, index, randomize, lockDimensions;
        private ulong seed;
        private MotionTextSelectors.Shape shape;
        private MotionNode start = null!, end = null!, offset = null!, amount = null!, smoothness = null!, easeHigh = null!, easeLow = null!;
        private MotionNode max = null!, min = null!, wiggles = null!, correlation = null!, temporal = null!, spatial = null!;

        public Mode Mode { get; private set; }
        public BasedOn Based { get; private set; }

        public static Selector Read(object? value, string who)
        {
            var o = new MotionOptions(value, who, "type", "start", "end", "offset", "units", "amount", "shape", "smoothness",
                "easeHigh", "easeLow", "randomize", "seed", "mode", "basedOn",
                "maxAmount", "minAmount", "wigglesPerSecond", "correlation", "temporalPhase", "spatialPhase", "lockDimensions");
            var s = new Selector
            {
                wiggly = o.Raw("type")?.ToString()?.Trim().ToLowerInvariant() switch
                {
                    null or "range" => false,
                    "wiggly" => true,
                    var x => throw new ArgumentException($"{who}'s type is 'range' or 'wiggly'; '{x}' is not."),
                },
                Mode = Pick<Mode>(o.Raw("mode"), Mode.Add, who, "mode"),
                Based = Pick<BasedOn>(o.Raw("basedOn"), BasedOn.Characters, who, "basedOn"),
                shape = Pick<MotionTextSelectors.Shape>(o.Raw("shape"), MotionTextSelectors.Shape.Square, who, "shape"),
                index = o.Raw("units")?.ToString()?.Trim().ToLowerInvariant() switch
                {
                    null or "percent" => false,
                    "index" => true,
                    var x => throw new ArgumentException($"{who}'s units is 'percent' or 'index'; '{x}' is not."),
                },
                randomize = o.Bool("randomize", false),
                lockDimensions = o.Bool("lockDimensions", false),
                seed = (ulong)Math.Abs((long)o.Number("seed", 0)),
            };
            s.start = o.Node("start", MotionType.Real, 0d);
            s.end = o.Node("end", MotionType.Real, double.NaN);
            s.offset = o.Node("offset", MotionType.Real, 0d);
            s.amount = o.Node("amount", MotionType.Real, 100d);
            s.smoothness = o.Node("smoothness", MotionType.Real, 100d);
            s.easeHigh = o.Node("easeHigh", MotionType.Real, 0d);
            s.easeLow = o.Node("easeLow", MotionType.Real, 0d);
            s.max = o.Node("maxAmount", MotionType.Real, 100d);
            s.min = o.Node("minAmount", MotionType.Real, -100d);
            s.wiggles = o.Node("wigglesPerSecond", MotionType.Real, 2d);
            s.correlation = o.Node("correlation", MotionType.Real, 50d);
            s.temporal = o.Node("temporalPhase", MotionType.Real, 0d);
            s.spatial = o.Node("spatialPhase", MotionType.Real, 0d);
            return s;
        }

        public IEnumerable<MotionNode> Nodes() => wiggly
            ? [max, min, wiggles, correlation, temporal, spatial]
            : [start, end, offset, amount, smoothness, easeHigh, easeLow];

        /// <summary>The selector at a time, as a function of a unit's index and the unit count — EffectCraft's <c>range_params</c>.</summary>
        public Func<int, int, double[]> Prepare(double t, int count)
        {
            if (wiggly)
            {
                var w = new Wiggly(max.Evaluate(t)[0] / 100, min.Evaluate(t)[0] / 100, wiggles.Evaluate(t)[0],
                    correlation.Evaluate(t)[0] / 100, temporal.Evaluate(t)[0], spatial.Evaluate(t)[0], lockDimensions, seed);
                return (i, _) => [WigglyValue(w, t, i, 0), WigglyValue(w, t, i, 1), WigglyValue(w, t, i, 2)];
            }

            var div = index ? Math.Max(count, 1) : 100.0;
            var e = end.Evaluate(t)[0];
            var r = new MotionTextSelectors.Range(
                Math.Clamp(start.Evaluate(t)[0], 0, div) / div,
                Math.Clamp(double.IsNaN(e) ? (index ? count : 100) : e, 0, div) / div,
                Math.Clamp(offset.Evaluate(t)[0], -div, div) / div,
                amount.Evaluate(t)[0] / 100, shape, smoothness.Evaluate(t)[0] / 100,
                easeHigh.Evaluate(t)[0] / 100, easeLow.Evaluate(t)[0] / 100);
            var order = randomize ? RandomOrder(count, seed) : null;
            return (i, n) =>
            {
                var at = order is not null && i < order.Length ? order[i] : i;
                var v = RangeValue(r, at, n);
                return [v, v, v];
            };
        }

        private static T Pick<T>(object? value, T fallback, string who, string name) where T : struct, Enum =>
            value is null ? fallback
            : Enum.TryParse<T>(value.ToString(), ignoreCase: true, out var v) && Enum.IsDefined(v) && !int.TryParse(value.ToString(), out _) ? v
            : throw new ArgumentException($"{who}'s {name} is one of {string.Join(", ", Enum.GetNames<T>().Select(n => $"'{char.ToLowerInvariant(n[0])}{n[1..]}'"))}; '{value}' is not.");
    }
    #endregion
}
