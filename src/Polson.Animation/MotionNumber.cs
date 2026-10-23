namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// A number in a composition that counts, as <c>comp.number(...)</c> returns it: a text whose string follows a real node.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read back by the values it holds.</b> Counting from 0 to 974 shows hundreds of strings, and a figure caught
/// mid-count is not a claim. <c>comp.texts()</c> lists each value held on screen for at least half a second as an item of
/// its own, typically the start and the end; <c>Verify.film</c> holds the watcher to those, and does not call a reading of
/// a value the number passed through a stray.
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto them,
/// so a script calling <c>x.doThing()</c> reaches <c>DoThing()</c>. The camelCase form is the one documented in
/// <c>docs/Polson.core.md</c> and the studio manuals.
/// </para>
/// </remarks>
public sealed class MotionNumber : IShownText
{
    #region Types
    /// <summary>A number counting on the frame from <c>From</c> to <c>To</c> film seconds, through values from <c>Min</c> to <c>Max</c>.</summary>
    internal sealed record Count(NumberFormat Format, double Min, double Max, double From, double To)
    {
        /// <summary>
        /// Whether <paramref name="value"/>, read at <paramref name="at"/> with <paramref name="slack"/> seconds either side,
        /// is one this count could have shown, half its last digit beyond the range allowed.
        /// </summary>
        public bool Covers(double value, double? at, double slack)
        {
            var half = 0.5 * Math.Pow(10, -Format.Decimals);
            return value >= Min - half && value <= Max + half && (at is not { } t || t >= From - slack && t <= To + slack);
        }
    }
    #endregion

    #region Constructors
    internal MotionNumber(MotionText text, MotionTextLayer layer) => (this.text, this.layer) = (text, layer);
    #endregion

    #region Methods
    /// <summary>The value at <paramref name="time"/> seconds of the number's own stack.</summary>
    public double ValueAt(double time) => layer.ValueAt(time) ?? 0;

    /// <summary>The string shown at <paramref name="time"/> seconds of the number's own stack: the value as formatted.</summary>
    public string TextAt(double time) => layer.WordsAt(time);

    /// <summary>As <c>text.boundsAt</c>: the ink box of the digits shown at <paramref name="time"/>.</summary>
    public Dictionary<string, object?>? BoundsAt(double time, object? options = null) => text.BoundsAt(time, options);

    /// <summary>As <c>text.settledAt</c>: when the number stops changing and moving, and how long it then holds.</summary>
    public Dictionary<string, object?>? SettledAt(double from = 0, object? options = null) => text.SettledAt(from, options);

    /// <summary>When it counts on the frame — visible, its value changing from one frame to the next — and through what; null if never.</summary>
    internal Count? Counting()
    {
        var root = layer.Root ?? throw new InvalidOperationException("comp.number's composition is gone.");
        var shown = Enumerable.Range(0, root.FrameCount).Select(f => f / root.Fps).Where(text.VisibleOnFrame)
            .Select(t => (Time: t, Value: layer.ValueAt(text.LayerTime(t)) ?? 0)).ToArray();
        var moving = shown.Where((s, i) => i > 0 && s.Value != shown[i - 1].Value || i + 1 < shown.Length && s.Value != shown[i + 1].Value).ToArray();
        return moving.Length == 0 ? null
            : new(layer.Format!, moving.Min(m => m.Value), moving.Max(m => m.Value), moving[0].Time, moving[^1].Time);
    }

    string IShownText.Text => layer.Words;

    string? IShownText.Desc => layer.Desc;

    string IShownText.Kind => "number";

    bool IShownText.Counts => true;

    string IShownText.TextAt(double time) => layer.WordsAt(text.LayerTime(time));

    bool IShownText.VisibleOnFrame(double time) => text.VisibleOnFrame(time);
    #endregion

    #region Fields
    private readonly MotionText text;
    private readonly MotionTextLayer layer;
    #endregion
}

/// <summary>
/// How a counting number is written: <c>{ decimals?, prefix?, suffix?, grouping? }</c>, as Synfig's <c>realstring</c>
/// writes it (<c>%.Nf</c>), with a <c>join</c> around it for the prefix and suffix.
/// </summary>
/// <remarks>
/// A negative value that rounds to zero shows as 0, where Synfig's <c>printf</c> writes <c>-0</c>: on those frames alone
/// the two differ.
/// </remarks>
internal sealed record NumberFormat(int Decimals, string Prefix, string Suffix, bool Grouping)
{
    public static NumberFormat Read(object? options, string who)
    {
        var o = new MotionOptions(options, $"{who}'s format", "decimals", "prefix", "suffix", "grouping");
        var d = o.Number("decimals", 0);
        if (d is < 0 or > 6 || d != Math.Floor(d)) throw new ArgumentException($"{who}'s format.decimals is a whole number from 0 to 6.");
        return new((int)d, o.Raw("prefix")?.ToString() ?? "", o.Raw("suffix")?.ToString() ?? "", o.Bool("grouping", false));
    }

    /// <remarks>
    /// The binary value rounded, as <c>printf</c> rounds it, not scaled first: <c>Math.Round</c> multiplies by ten, and the
    /// double 231.6499… became 231.65 and showed <c>231.7</c> where Synfig showed <c>231.6</c> (2026-10-23). An exact half
    /// — only a short binary fraction can be one, 0.125 or 2.5 — goes to even, as <c>printf</c> takes it.
    /// </remarks>
    public string Format(double value)
    {
        var spec = (Grouping ? "N" : "F") + Decimals;
        var a = Math.Abs(value);
        var beyond = a.ToString("F" + (Decimals + 25), CultureInfo.InvariantCulture)[^25..];
        var digits = beyond[0] == '5' && beyond.Skip(1).All(c => c == '0')
            ? Math.Round((decimal)a, Decimals, MidpointRounding.ToEven).ToString(spec, CultureInfo.InvariantCulture)
            : a.ToString(spec, CultureInfo.InvariantCulture);
        var sign = value < 0 && digits.Any(c => c is >= '1' and <= '9') ? "-" : "";   // −0 shows as 0
        return Prefix + sign + digits + Suffix;
    }

    /// <summary>The text parameter's value: Synfig's <c>realstring</c> on the node, joined with the prefix and suffix.</summary>
    public XElement ToSif(SifWriter sif, MotionNode value, string? desc)
    {
        if (Grouping)
            throw new InvalidOperationException(
                $"The number{(desc is null ? "" : $" '{desc}'")} groups its digits, which Synfig's realstring cannot: export it with "
                + "grouping off, or capture the composition as frames.");
        var real = new XElement("realstring", new XAttribute("type", "string"),
            sif.Link("real", value),
            new XElement("width", new XElement("integer", new XAttribute("value", 0))),
            new XElement("precision", new XElement("integer", new XAttribute("value", Decimals))),
            new XElement("zero_pad", SifWriter.Bool(false)));
        if (Prefix.Length == 0 && Suffix.Length == 0) return real;
        return new XElement("join", new XAttribute("type", "string"),
            new XElement("strings", new XElement("dynamic_list", new XAttribute("type", "string"), new XElement("entry", real))),
            new XElement("before", new XElement("string", Prefix)),
            new XElement("separator", new XElement("string", "")),
            new XElement("after", new XElement("string", Suffix)));
    }
}

public abstract partial class MotionLayerList
{
    /// <summary>
    /// A number that counts: <c>{ value, format?, family, weight?, style?, size?, origin?, orient?, color?, compress?, kerning?, amount?, blend?, desc? }</c>.
    /// <c>value</c> is a number or a real node, and the string shown follows it, written by <c>format</c>
    /// (<c>{ decimals?, prefix?, suffix?, grouping? }</c>). The rest are <c>comp.text</c>'s. Returns the number.
    /// </summary>
    public MotionNumber Number(object? options)
    {
        var o = new MotionOptions(options, "number", "value", "format", "family", "weight", "style", "size", "origin", "orient", "color",
            "compress", "kerning", "amount", "blend", "desc");
        if (!o.Has("value"))
            throw new ArgumentException("comp.number needs a value: a number, or a real node such as Motion.nodes.animated('real', [{ time: 0, value: 0 }, { time: 2, value: 974 }]).");
        var layer = new MotionTextLayer(o, Root?.YDown ?? true, Root);
        layers.Add(layer);
        var number = new MotionNumber(new MotionText(layer, this), layer);
        Root?.TextHandles.Add(number);
        return number;
    }
}
