namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

using Polson.Drawing.Skia;

using static Polson.Drawing.Skia.ConstructiveDrawingToolkit;

/// <summary>
/// A face as a composition layer: a constructed head whose placement, character and expression are channels, each a
/// value or a node, drawn every frame by the feature drawers.
/// </summary>
/// <remarks>
/// <para>
/// The face is a pure function of its numbers, so it is rebuilt for each frame from them rather than deformed: the head
/// from <c>origin</c>, <c>height</c>, <c>yaw</c> and <c>pitch</c>; then the <c>character</c>; then the <c>channels</c>,
/// named in the toolkit's own vocabulary: Action Units (<c>AU12</c>), named expressions (<c>joy</c>), and Hamm's eye
/// wheel settings (<c>upperLid</c>, <c>browTop</c>, ...), any of them prefixed <c>near.</c> or <c>far.</c> for one side.
/// Any frame renders directly, in any order.
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto them,
/// so a script calling <c>x.doThing()</c> reaches <c>DoThing()</c>. The camelCase form is the one documented in
/// <c>docs/Polson.core.md</c> and the studio manuals.
/// </para>
/// </remarks>
public sealed class MotionFace
{
    #region Constructors
    internal MotionFace(MotionFaceLayer layer) => this.layer = layer;
    #endregion

    #region Fields
    private readonly MotionFaceLayer layer;
    #endregion

    #region Properties
    /// <summary>The channels the face was given, in order.</summary>
    public IReadOnlyList<string> Channels => layer.ChannelNames;

    /// <summary>The poses in the face's library, each blended in by a channel of its name.</summary>
    public IReadOnlyList<string> Poses => layer.PoseNames;
    #endregion

    #region Methods
    /// <summary>The head the face draws at <paramref name="time"/> seconds: every landmark, for placing things on it.</summary>
    public Dictionary<string, object?> HeadAt(double time) => layer.HeadAt(time);

    /// <summary>Every channel's value at <paramref name="time"/> seconds.</summary>
    public Dictionary<string, object?> ChannelsAt(double time) =>
        layer.ChannelNames.Zip(layer.ChannelNodes).ToDictionary(c => c.First, c => (object?)c.Second.Evaluate(time)[0]);
    #endregion
}

/// <summary>The layer behind <see cref="MotionFace"/>.</summary>
internal sealed class MotionFaceLayer : MotionLayer
{
    #region Constructors
    public MotionFaceLayer(MotionOptions o) : base(o)
    {
        construction = o.Raw("construction")?.ToString()?.Trim() ?? "loomis";
        if (construction is not ("loomis" or "doubleCircle"))
            throw new ArgumentException($"{o.Who}'s construction is 'loomis' or 'doubleCircle', not '{construction}'.");
        if (construction == "doubleCircle" && (o.Has("yaw") || o.Has("pitch")))
            throw new ArgumentException($"{o.Who}: the double-circle head is a front view and takes no yaw or pitch; use construction 'loomis' to turn it.");

        origin = o.Node("origin", MotionType.Vector, new[] { 0d, 0d });
        height = o.Node("height", MotionType.Real, 300d);
        // Degrees, as the head takes them: an angle node or a real one.
        MotionNode Degrees(string key) => o.Raw(key) is MotionNode { Kind: MotionType.Angle } angle ? angle : o.Node(key, MotionType.Real, 0d);
        yaw = Degrees("yaw");
        pitch = Degrees("pitch");
        character = ReadReals(o.Raw("character"), $"{o.Who}'s character");
        look = JsInterop.AsDict(o.Raw("look")) ?? (o.Has("look") ? throw new ArgumentException($"{o.Who}'s look is an object of drawing options.") : new Hashtable());
        foreach (var key in look.Keys.Cast<object>().Select(k => k.ToString()!))
            if (!ConstructiveDrawingToolkit.FaceLookKeys.Contains(key))
                throw new ArgumentException($"{o.Who}'s look has no '{key}'. It takes: {string.Join(", ", ConstructiveDrawingToolkit.FaceLookKeys)}.");

        // The pose library: each pose a fixed set of channel values, blended in by a channel of its own name.
        if (o.Has("poses"))
        {
            var library = JsInterop.AsDict(o.Raw("poses")) ?? throw new ArgumentException($"{o.Who}'s poses is an object: {{ name: {{ channel: value }} }}.");
            foreach (var poseName in library.Keys.Cast<object>().Select(k => k.ToString()!))
            {
                if (Toolkit.FacePoseNameTaken(poseName))
                    throw new ArgumentException($"{o.Who}'s pose '{poseName}' takes a name a channel already has; name it something else.");
                var entries = new List<FacePoseEntry>();
                foreach (var (name, node) in ReadReals(library[poseName], $"{o.Who}'s pose '{poseName}'"))
                {
                    if (node is not MotionConstant)
                        throw new ArgumentException($"{o.Who}'s pose '{poseName}' holds fixed values; its '{name}' is a node. Key the pose's weight instead.");
                    var (side, bare) = ConstructiveDrawingToolkit.SplitFaceSide(name);
                    var kind = Toolkit.ClassifyFaceChannel(bare, o.Who, name);
                    entries.Add(new FacePoseEntry(side, bare, kind, node.Evaluate(0)[0]));
                }
                poses[poseName] = entries;
            }
        }

        var channels = ReadReals(o.Raw("channels"), $"{o.Who}'s channels");
        foreach (var (name, node) in channels)
        {
            var (side, bare) = ConstructiveDrawingToolkit.SplitFaceSide(name);
            var kind = poses.ContainsKey(bare) ? FaceChannelKind.Pose : Toolkit.ClassifyFaceChannel(bare, o.Who, name);
            parsed.Add((name, side, bare, kind, node));
        }

        // Built once now, so a bad option in the look or the character is refused at the call rather than at the first frame.
        _ = Build(0d, out _);
        using var probe = new SkiaCanvas(4, 4);
        Draw(probe, 0d, 1d);
    }
    #endregion

    #region Fields
    internal static readonly string[] Options =
        ["origin", "height", "yaw", "pitch", "construction", "character", "poses", "channels", "look", "amount", "blend", "desc"];

    static readonly ConstructiveDrawingToolkit Toolkit = new();

    private readonly string construction;
    private readonly MotionNode origin;
    private readonly MotionNode height;
    private readonly MotionNode yaw;
    private readonly MotionNode pitch;
    private readonly List<(string Name, MotionNode Node)> character;
    private readonly IDictionary look;
    private readonly List<(string Name, string? Side, string Bare, FaceChannelKind Kind, MotionNode Node)> parsed = [];
    private readonly Dictionary<string, List<FacePoseEntry>> poses = [];
    #endregion

    #region Properties
    public IReadOnlyList<string> ChannelNames => [.. parsed.Select(p => p.Name)];

    public IReadOnlyList<MotionNode> ChannelNodes => [.. parsed.Select(p => p.Node)];

    public IReadOnlyList<string> PoseNames => [.. poses.Keys];
    #endregion

    #region Methods
    public Dictionary<string, object?> HeadAt(double time) => Build(time, out _);

    public override IEnumerable<MotionNode> Nodes() =>
        new[] { Amount, origin, height, yaw, pitch }.Concat(character.Select(c => c.Node)).Concat(parsed.Select(p => p.Node));

    public override XElement ToSif(SifWriter sif) => throw new InvalidOperationException(
        $"The face layer{(Desc is null ? "" : $" '{Desc}'")} cannot be written as .sif: Synfig cannot run the feature drawers. "
        + "Write the composition without it, or capture it as frames.");

    public override XElement ToSvg(SvgAnimationWriter svg, MotionTimeMap map) => throw SvgAnimationWriter.Refuse("face layer", Desc);

    protected override void Draw(SkiaCanvas surface, double time, double amount)
    {
        var canvas = surface.SkCanvas;
        var depth = canvas.Save();
        if (amount < 1)
        {
            using var fade = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 255, 255, (byte)Math.Round(amount * 255)) };
            canvas.SaveLayer(fade);
        }

        try
        {
            // The same call a figure's drawing makes, so the two routes cannot drift: the layer only knows the pitch the
            // nose is handed and the height the outline's width is taken from.
            var head = Build(time, out var pitchDeg);
            var ctx = new CanvasRenderingContext2D(surface, canvas.TotalMatrix);
            Toolkit.DrawFace(ctx, head, look, pitchDeg, (float)height.Evaluate(time)[0]);
        }
        finally
        {
            canvas.RestoreToCount(depth);
        }
    }

    /// <summary>The head at a time: built, given its character, then its expression and its eye-wheel settings.</summary>
    private Dictionary<string, object?> Build(double time, out float pitchDeg)
    {
        var o = origin.Evaluate(time);
        var h = (float)height.Evaluate(time)[0];
        pitchDeg = (float)pitch.Evaluate(time)[0];
        var head = construction == "doubleCircle"
            ? Toolkit.CreateDoubleCircleHead((float)o[0], (float)o[1], h)
            : Toolkit.CreateLoomisHead((float)o[0], (float)o[1], h, (float)yaw.Evaluate(time)[0], pitchDeg);

        if (character.Count > 0)
            head = Toolkit.CreateParametricHead(head, character.ToDictionary(c => c.Name, c => (object?)(float)c.Node.Evaluate(time)[0]));

        // The channels at this time, applied as applyFaceChannels applies them: units and expressions summed per side,
        // the eye wheel set by its channels and moved by each pose's weight.
        return Toolkit.ApplyFaceChannelValues(head,
            parsed.Select(c => new FaceChannelValue(c.Side, c.Bare, c.Kind, (float)c.Node.Evaluate(time)[0])), poses);
    }

    /// <summary>An object of named real values or nodes.</summary>
    private static List<(string, MotionNode)> ReadReals(object? raw, string who)
    {
        if (raw is null) return [];
        // In the order written: a script object keeps its keys' order, and a channel list reads best that way.
        IEnumerable<KeyValuePair<string, object?>> entries = raw is IDictionary<string, object?> ordered ? ordered
            : (JsInterop.AsDict(raw) ?? throw new ArgumentException($"{who} is an object: {{ name: number or node }}."))
                .Cast<DictionaryEntry>().Select(e => new KeyValuePair<string, object?>(e.Key.ToString()!, e.Value));
        return [.. entries.Select(e => (e.Key, MotionNodeFactory.Node(e.Value, MotionType.Real, $"{who}' '{e.Key}'")))];
    }
    #endregion

}
