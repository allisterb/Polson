namespace Polson.Animation;

using System;
using System.Collections;
using System.Linq;

using Polson.Drawing.Skia;
using SkiaSharp;

/// <summary>A text or an equation the composition shows, as <c>comp.texts()</c> lists it.</summary>
internal interface IShownText
{
    /// <summary>What a reader would transcribe: a text's words, an equation's linear form.</summary>
    string Text { get; }

    string? Desc { get; }

    /// <summary><c>'text'</c> or <c>'equation'</c>.</summary>
    string Kind { get; }

    /// <summary>Whether it is visible on the frame at the film's <paramref name="time"/>, every group above counted.</summary>
    bool VisibleOnFrame(double time);
}

/// <summary>
/// An equation in a composition, as <c>comp.equation(...)</c> returns it: typeset from LaTeX, drawn as filled outlines in
/// a group of its own, and listed by <c>comp.texts()</c> by its linear form.
/// </summary>
/// <remarks>
/// <para>
/// <b>Outlines, not text.</b> CSharpMath typesets the LaTeX with Latin Modern Math and hands back each glyph, fraction bar
/// and radical as an outline; each becomes a region in the equation's group. So the equation is Synfig-exportable, moves,
/// fades and keys as any group does, and needs no font at render time.
/// </para>
/// <para>
/// <b>Read back like a text.</b> The group's visibility is the equation's: its transform, clock, switch and every
/// <c>amount</c> above it. <c>comp.texts()</c> lists it with its linear form, which is what <c>Verify.film</c> holds the
/// watcher's LaTeX against, through the same parser.
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto them,
/// so a script calling <c>x.doThing()</c> reaches <c>DoThing()</c>. The camelCase form is the one documented in
/// <c>docs/Polson.core.md</c> and the studio manuals.
/// </para>
/// </remarks>
public sealed class MotionEquation : IShownText
{
    #region Constructors
    internal MotionEquation(string latex, string linear, MotionGroup group, string? desc, double width, double height, string kind = "equation") =>
        (Latex, Text, this.group, this.desc, Width, Height, this.kind) = (latex, linear, group, desc, width, height, kind);
    #endregion

    #region Properties
    /// <summary>The LaTeX, as given.</summary>
    public string Latex { get; }

    /// <summary>The equation as one line of Unicode, the way a reader would write it: <c>x = (−b ± √(b² − 4ac))/(2a)</c>.</summary>
    public string Text { get; }

    /// <summary>The typeset width, in the composition's units.</summary>
    public double Width { get; }

    /// <summary>The typeset height, ascent to descent, in the composition's units.</summary>
    public double Height { get; }

    string? IShownText.Desc => desc;

    /// <summary><c>'equation'</c>, or <c>'tick'</c> for an axis label <c>comp.axes</c> drew: a ruler, not a claim.</summary>
    string IShownText.Kind => kind;
    #endregion

    #region Methods
    bool IShownText.VisibleOnFrame(double time) => group.SpaceAt(time).Visible && group.AmountAbove(time) > 0;
    #endregion

    #region Fields
    private readonly MotionGroup group;
    private readonly string? desc;
    private readonly string kind;
    #endregion
}

public abstract partial class MotionLayerList
{
    /// <summary>
    /// An equation from LaTeX: <c>{ latex, origin?, size?, orient?, color?, amount?, blend?, desc? }</c>. <c>size</c> is the
    /// em in units (default 48) and fixed; scale a group to grow it. <c>orient</c> aligns the typeset box on <c>origin</c> as
    /// a text's does, <c>[0, 0]</c> its top left and the default <c>[0.5, 0.5]</c> its centre. <c>color</c> fills every
    /// glyph the LaTeX does not colour itself (<c>\color</c>), and keys. Returns the equation, which <c>comp.texts()</c> lists
    /// by its linear form.
    /// </summary>
    public MotionEquation Equation(object? options) => Equation(options, "equation");

    /// <summary>An equation of a given kind: <c>'tick'</c> for an axis label, which <c>Verify.film</c> sources as a scale.</summary>
    internal MotionEquation Equation(object? options, string kind)
    {
        var o = new MotionOptions(options, "equation", "latex", "origin", "size", "orient", "color", "amount", "blend", "desc");
        if (o.Raw("latex") is not string latex)
            throw new ArgumentException("comp.equation's latex is a LaTeX string, such as 'E = mc^2' (backslashes doubled in JavaScript: '\\\\frac{1}{2}').");
        if (o.Node("size", MotionType.Real, 48d) is not MotionConstant sizeNode)
            throw new ArgumentException("comp.equation's size is fixed when it is typeset; to grow or shrink an equation, key the scale of a group around it.");
        if (o.Node("orient", MotionType.Vector, new[] { 0.5, 0.5 }) is not MotionConstant orientNode)
            throw new ArgumentException("comp.equation's orient is fixed when it is typeset; move it with origin.");

        var size = (float)sizeNode.Evaluate(0)[0];
        var (layout, error) = EquationLayout.Parse(latex, size);
        if (layout is null) throw new ArgumentException($"comp.equation: {error}");

        // The typeset box placed so its orient point sits at the group's origin: y-down units put the box above the
        // baseline at negative y, a y-up view at positive.
        var yDown = Root?.YDown ?? true;
        var orient = orientNode.Evaluate(0);
        var height = layout.Ascent + layout.Descent;
        var px = (float)orient[0] * layout.Width;
        var py = yDown ? -layout.Ascent + (float)orient[1] * height : layout.Ascent - (float)orient[1] * height;

        var groupOptions = o.Subset("amount", "blend", "desc");
        if (o.Has("origin")) groupOptions["offset"] = o.Raw("origin");
        var group = Group(groupOptions);
        var color = o.Has("color") ? o.Raw("color") : "#000000";
        foreach (var (path, own) in layout.Outlines(-px, -py, SKColors.Black, yDown))
        {
            using (path)
            {
                if (path.IsEmpty) continue;
                var data = path.ToSvgPathData();
                group.Region(new Hashtable
                {
                    ["path"] = data,
                    ["color"] = own == SKColors.Black ? color : $"#{own.Red:x2}{own.Green:x2}{own.Blue:x2}",
                });
            }
        }

        var equation = new MotionEquation(latex, layout.Linear, group, o.Has("desc") ? o.Raw("desc")?.ToString() : null, layout.Width, height, kind);
        Root?.TextHandles.Add(equation);
        return equation;
    }
}
