namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Polson.Drawing.Skia;
using Polson.Drawing.Svg;

using SkiaSharp;

/// <summary>
/// Frame capture and animated encoding — the piece that turns a sequence of drawn states into one
/// moving artifact.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a spike.</b> It exists to prove one path end to end: a script drives its own timeline,
/// captures a frame per step, and saves an animated WebP. The shape of the authoring API above it is
/// still open, so nothing here should be treated as settled surface.
/// </para>
/// <para>
/// The timeline itself is deliberately <i>not</i> here. Snap's tweening decomposes into an easing
/// (a pure function of <c>0..1</c>, and <c>mina</c> already provides all nine) and a setter called
/// with the eased value — so a timeline is a few lines of JavaScript over calls that already exist,
/// and does not need engine support to be tried out.
/// </para>
/// <para>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS
/// camelCase spelling onto them, so a script calling <c>x.doThing()</c> reaches <c>DoThing()</c>.
/// The camelCase form is the one documented in <c>docs/Polson.core.md</c> and the studio manuals.
/// </remarks>
public class MotionToolkit : IDisposable
{
    #region Constructors
    /// <param name="projectRoot">Where output paths resolve, and the boundary they may not leave.</param>
    /// <param name="recorder">
    /// Told about every file this writes, as <c>(type, fullPath, fields)</c>, so the host can put it in
    /// the run record. The type is <c>render</c> for anything a viewer can watch and <c>export</c> for a
    /// file that is not viewable. Optional: without it nothing is recorded.
    /// </param>
    public MotionToolkit(string? projectRoot = null, Action<string, string, IReadOnlyDictionary<string, object?>>? recorder = null)
    {
        this.projectRoot = projectRoot;
        this.recorder = recorder;
    }
    #endregion

    #region Fields
    /// <summary>
    /// A ceiling on retained frames, because every one is an uncompressed bitmap.
    /// </summary>
    /// <remarks>
    /// 1200 frames of 1920×1080 is roughly 10 GB, which a sandbox should refuse rather than discover.
    /// The limit is on pixels rather than on frame count so a small board can hold many more frames
    /// than a large one — the resource that runs out is memory, not frames.
    /// </remarks>
    private const long MaxRetainedPixels = 600_000_000L;

    private readonly string? projectRoot;
    private readonly Action<string, string, IReadOnlyDictionary<string, object?>>? recorder;
    private readonly List<SKBitmap> frames = [];

    /// <summary>
    /// Where each held frame sits in its film, when that is known: set by a composition's capture,
    /// null for a frame added by <see cref="Frame"/>, whose time only the caller knows.
    /// </summary>
    private readonly List<(int Frame, double Seconds)?> times = [];
    private long retainedPixels;
    #endregion

    #region Properties
    /// <summary>How many frames are held.</summary>
    public int Count => frames.Count;
    #endregion

    #region Methods
    /// <summary>
    /// A new seekable score.
    /// </summary>
    /// <remarks>
    /// Independent of the frame buffer above: a timeline decides what the scene looks like at a
    /// time, <see cref="Frame"/> keeps what it looked like. The usual loop seeks and captures.
    /// <para>
    /// <c>options</c> is <c>{ defaults?: { dur?: number, easing?: fn } }</c>. Read as a typed
    /// options object rather than a dictionary, for the reason given on
    /// <see cref="MotionEntryOptions"/>.
    /// </para>
    /// </remarks>
    public MotionTimeline Timeline(MotionTimelineOptions? options = null) =>
        new(options?.Defaults?.Dur ?? 500d, options?.Defaults?.Easing);

    /// <summary>
    /// SPIKE: an animation as a graph of layers and value nodes, Synfig's model drawn by Skia.
    /// </summary>
    /// <remarks>
    /// <c>options</c> is <c>{ width?, height?, fps?, duration?, view? }</c>; duration in seconds, and
    /// <c>view</c> the <c>[left, top, right, bottom]</c> its layers' units map onto, pixels by default.
    /// </remarks>
    public MotionComposition Composition(object? options = null) => LastComposition = new(options, this, projectRoot);

    /// <summary>
    /// The composition the script built last: what <c>polson eval script.js --sif</c> writes, so a film's <c>.sif</c> is an
    /// export taken on demand from its script rather than a file every run has to write.
    /// </summary>
    internal MotionComposition? LastComposition { get; private set; }

    /// <summary>SPIKE: the value nodes a composition's parameters can be: constants, keys and formulas.</summary>
    public MotionNodeFactory Nodes { get; } = new();

    /// <summary>
    /// WCAG's contrast ratio between two colours, from 1 to 21: <c>(L₁ + 0.05) / (L₂ + 0.05)</c> on relative luminance,
    /// linearised sRGB weighted 0.2126 / 0.7152 / 0.0722. Text wants 4.5 or more, display-size type 3. A colour with
    /// alpha is laid over the opaque one first, so <c>contrast('#ffffff80', '#000000')</c> is grey on black.
    /// </summary>
    public double Contrast(string a, string b)
    {
        var (x, y) = (Read(a, "first"), Read(b, "second"));
        if (x[3] < 1 && y[3] < 1) throw new ArgumentException("Motion.contrast: at least one of the two colours is opaque, the ground the other sits on.");
        if (x[3] < 1) x = Over(x, y);
        if (y[3] < 1) y = Over(y, x);
        var (l1, l2) = (Luminance(x), Luminance(y));
        return Math.Round((Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05), 2);

        static double[] Read(string c, string which) => MotionTypes.Read(c, MotionType.Color, $"Motion.contrast's {which} colour");
        static double[] Over(double[] top, double[] ground) => [.. Enumerable.Range(0, 3).Select(i => top[i] * top[3] + ground[i] * (1 - top[3])), 1];
        static double Luminance(double[] c) =>
            0.2126 * Linear(c[0]) + 0.7152 * Linear(c[1]) + 0.0722 * Linear(c[2]);
        static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// A grade without a film: <c>{ hue?, brightness?, contrast?, exposure?, saturation?, gamma? }</c>, as
    /// <c>comp.colorCorrect</c> takes them (a <c>desc</c> is allowed and ignored, so the same object serves both), returning
    /// <c>{ apply(color, t?) }</c> — what that grade makes of a colour, to the pixel the render draws.
    /// </summary>
    public MotionGrade Grade(object? options = null) => new(new MotionColorCorrectLayer(
        new MotionOptions(options, "Motion.grade", "hue", "brightness", "contrast", "exposure", "saturation", "gamma", "desc")));

    /// <summary>
    /// A few smooth spline points fitted to many: <c>[{ point, t1, t2 }]</c>, every input point within <c>error</c> of the
    /// curve, ready for <c>comp.region</c> or <c>comp.outline</c>. <c>points</c> are <c>[x, y]</c> or <c>{ x, y }</c>.
    /// <c>options</c>: <c>{ error?, closed?, corner? }</c> — <c>error</c> in units (0.5), <c>closed</c> to fit round to
    /// the start (false), and <c>corner</c> the turn in degrees above which a point stays a sharp corner (60), or false to
    /// fit everything smooth as paper.js does.
    /// </summary>
    /// <remarks>paper.js's <c>Path.simplify</c>, ported; see <see cref="MotionCurveFit"/>.</remarks>
    public List<Dictionary<string, object?>> FitPoints(object? points, object? options = null)
    {
        const string who = "Motion.fitPoints";
        var o = new MotionOptions(options, who, "error", "closed", "corner");
        if (points is not System.Collections.IList list || list.Count < 2)
            throw new ArgumentException($"{who} takes at least two points, [[x, y], ...] or [{{ x, y }}, ...].");
        var input = list.Cast<object?>().Select((p, i) => MotionTypes.Read(p, MotionType.Vector, $"{who}'s point {i}"))
            .Select(p => new MotionCurveFit.V(p[0], p[1])).ToList();
        var error = o.Number("error", 0.5);
        if (!(error > 0)) throw new ArgumentException($"{who}'s error is a distance in units, more than 0.");
        double? corner = o.Raw("corner") switch
        {
            null => 60,
            false => null,
            bool => throw new ArgumentException($"{who}'s corner is an angle in degrees, or false for no corners."),
            _ => o.Number("corner", 60)
        };
        return [.. MotionCurveFit.Fit(input, o.Bool("closed", false), error, corner).Select(k => new Dictionary<string, object?>
        {
            ["point"] = new[] { k.Point.X, k.Point.Y },
            ["t1"] = new[] { -3 * k.HandleIn.X, -3 * k.HandleIn.Y },
            ["t2"] = new[] { 3 * k.HandleOut.X, 3 * k.HandleOut.Y },
        })];
    }

    /// <summary>
    /// SVG path data as spline points: <c>[{ points, loop, hole }]</c>, one entry per subpath, outer before inner. Each point
    /// is <c>{ point, t1, t2 }</c>, ready for <c>comp.region</c> or <c>comp.outline</c>, or to key one point of. <c>options</c>:
    /// <c>{ fillRule? }</c>, <c>'nonzero'</c> or <c>'evenodd'</c>, which decides <c>hole</c>.
    /// </summary>
    /// <remarks><c>comp.region({ path })</c> does this itself, holes and all; this is for when the points are to be changed.</remarks>
    public List<Dictionary<string, object?>> PathPoints(string d, object? options = null)
    {
        var o = new MotionOptions(options, "Motion.pathPoints", "fillRule");
        var rule = o.Has("fillRule") ? o.Raw("fillRule") as string : "nonzero";
        if (rule is not ("nonzero" or "evenodd"))
            throw new ArgumentException("Motion.pathPoints's fillRule is 'nonzero' or 'evenodd', as in SVG.");
        return [.. MotionSvgPath.Read(d, rule == "evenodd", "Motion.pathPoints", fill: false).Select(c => new Dictionary<string, object?>
        {
            ["points"] = c.ToJs(),
            ["loop"] = c.Loop,
            ["hole"] = c.Hole,
        })];
    }

    /// <summary>
    /// A line of dialogue as phonemes and mouth shapes over time, from its text: <c>{ start?, rate?, set?, pronounce?,
    /// lead?, minimum? }</c>. Its <c>mouths</c> key a <c>comp.switch</c> of drawn mouths; its <c>weights()</c> a face's poses.
    /// </summary>
    public MotionSpeech Speech(string text, object? options = null) => new(text, options, projectRoot);

    /// <summary>
    /// Rasterises the current state of a paper, canvas or bitmap and keeps it as the next frame.
    /// </summary>
    /// <remarks>
    /// A <b>copy</b> is taken. A canvas is a live surface and a paper is a live document, so a caller
    /// that captured a reference and then drew the next state would otherwise find every frame
    /// showing the last one — the failure would be silent and the whole sequence would be wrong.
    /// </remarks>
    public int Frame(object? source, int? width = null, int? height = null)
    {
        var bitmap = Rasterise(source, width, height)
            ?? throw new ArgumentException(
                "Motion.frame(...) takes a Snap paper, a canvas, or a bitmap.", nameof(source));

        var pixels = (long)bitmap.Width * bitmap.Height;
        if (retainedPixels + pixels > MaxRetainedPixels)
        {
            bitmap.Dispose();
            throw new InvalidOperationException(
                $"Motion is holding {Count} frames ({retainedPixels / 1_000_000}M pixels) and this one would exceed the "
                + $"{MaxRetainedPixels / 1_000_000}M limit. Save what you have, call Motion.clear(), or draw a smaller board.");
        }

        if (frames.Count > 0 && (frames[0].Width != bitmap.Width || frames[0].Height != bitmap.Height))
        {
            // Both sizes are read *before* the bitmap is released. Composing the message afterwards
            // reads width and height off freed native memory, which does not throw — it takes the
            // whole process down, and an agent loses the run rather than the frame.
            var message =
                $"Every frame must be the same size. Frame 1 is {frames[0].Width}x{frames[0].Height}, this one is "
                + $"{bitmap.Width}x{bitmap.Height}. Pass the same width and height each time, or clear and start again.";
            bitmap.Dispose();
            throw new InvalidOperationException(message);
        }

        frames.Add(bitmap);
        times.Add(null);
        retainedPixels += pixels;
        return frames.Count;
    }

    /// <summary>Keeps a frame and the film frame and time it shows, so a sheet can label it truthfully.</summary>
    internal int FrameAt(object source, int filmFrame, double seconds)
    {
        var count = Frame(source);
        times[^1] = (filmFrame, seconds);
        return count;
    }

    /// <summary>Discards every held frame.</summary>
    public void Clear()
    {
        foreach (var frame in frames) frame.Dispose();
        frames.Clear();
        times.Clear();
        retainedPixels = 0;
    }

    /// <summary>
    /// Encodes the held frames as one animated WebP and writes it, returning what it wrote.
    /// </summary>
    /// <remarks>
    /// <c>fps</c> sets each frame's duration; pass <c>frameMs</c> instead to state the duration
    /// directly. The frames are <b>kept</b> afterwards, so a caller can save the same sequence twice
    /// at different qualities without redrawing it — call <see cref="Clear"/> when done.
    /// </remarks>
    public Dictionary<string, object?> Save(string filePath, object? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (frames.Count == 0)
            throw new InvalidOperationException("Motion.save(...) has no frames. Call Motion.frame(...) first.");

        var opt = JsInterop.AsDict(options);
        var fps = Num(opt, "fps", 25f);
        var frameMs = Num(opt, "frameMs", fps > 0 ? 1000f / fps : 40f);
        var quality = Num(opt, "quality", 80f);
        var lossless = opt != null && opt.Contains("lossless") && Convert.ToBoolean(opt["lossless"]);
        var loops = LoopCount(opt?["loop"]);

        if (frameMs <= 0) throw new ArgumentException("fps must be positive.", nameof(options));

        var full = ProjectPath.Resolve(projectRoot, filePath, nameof(filePath), "Write to");
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var duration = TimeSpan.FromMilliseconds(frameMs);
        var encoderFrames = new SKWebpEncoderFrame[frames.Count];
        for (var i = 0; i < frames.Count; i++) encoderFrames[i] = new SKWebpEncoderFrame(frames[i], duration);

        var encoderOptions = new SKWebpEncoderOptions(
            lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy,
            Math.Clamp(quality, 1f, 100f));

        using var data = SKWebpEncoder.EncodeAnimated(encoderFrames, encoderOptions)
            ?? throw new InvalidOperationException("Encoding the animated WebP produced no data.");

        var bytes = data.ToArray();
        SetWebpLoopCount(bytes, loops);
        File.WriteAllBytes(full, bytes);

        // What the file holds, read back from the file rather than assumed. The WebP encoder merges
        // consecutive pixel-identical frames and sums their durations — correct, and a real size
        // win, but it means the count handed in is not always the count stored. Reporting the
        // submitted number alone would be quietly wrong for any sequence that holds still.
        var stored = frames.Count;
        using (var codec = SKCodec.Create(new MemoryStream(bytes)))
        {
            if (codec != null) stored = codec.FrameCount;
        }

        Record("render", full, new()
        {
            ["motion"] = "animation",
            ["format"] = "webp",
            ["bytes"] = (long)bytes.Length,
            ["frames"] = frames.Count,
            ["storedFrames"] = stored,
            ["durationMs"] = frameMs * frames.Count,
            ["width"] = frames[0].Width,
            ["height"] = frames[0].Height
        });

        return new Dictionary<string, object?>
        {
            ["path"] = filePath,
            ["frames"] = frames.Count,
            ["storedFrames"] = stored,
            ["merged"] = frames.Count - stored,
            ["width"] = frames[0].Width,
            ["height"] = frames[0].Height,
            ["frameMs"] = frameMs,
            ["durationMs"] = frameMs * frames.Count,
            ["loop"] = loops == 0 ? true : loops == 1 ? false : loops,
            ["bytes"] = (long)bytes.Length
        };
    }

    /// <summary>
    /// Encodes the held frames as a video with sound, an MP4 (H.264, AAC) or a WebM (VP9, Opus) by the path's extension,
    /// and writes it: <c>{ fps?, quality?, background?, audio? }</c>.
    /// </summary>
    /// <remarks>
    /// Frames captured from a composition carry their times, so their rate and where they start are read from them and a
    /// recording lands where the composition put it; <c>fps</c> (25 for frames added by hand) overrides the rate.
    /// <c>audio</c> is a path, a line of speech timed by a recording, <c>{ path, start }</c>, or a list of these, mixed.
    /// Video has no alpha, so frames are drawn over <c>background</c>, white by default. Needs <c>ffmpeg</c>.
    /// </remarks>
    public Dictionary<string, object?> SaveVideo(string filePath, object? options = null)
    {
        var o = new MotionOptions(options, "Motion.saveVideo", "fps", "quality", "background", "audio");
        if (frames.Count == 0)
            throw new InvalidOperationException("Motion.saveVideo(...) has no frames. Call Motion.frame(...) or comp.capture() first.");

        // Evenly spaced captured frames say their own rate; anything else is 25 unless told.
        var timed = times.All(t => t is not null) ? times.Select(t => t!.Value.Seconds).ToList() : null;
        var step = timed is { Count: > 1 } ? timed[1] - timed[0] : 0;
        var even = step > 0 && timed!.Zip(timed.Skip(1)).All(p => Math.Abs(p.Second - p.First - step) < 1e-6);
        var fps = o.Has("fps") ? o.Number("fps", 25) : even ? Math.Round(1 / step, 6) : 25;

        return MotionVideo.Save(filePath, frames, frames.Count, frames[0].Width, frames[0].Height, fps,
            timed?[0] ?? 0, o, projectRoot, this);
    }

    /// <summary>
    /// Joins finished videos end to end into one, without re-encoding: <c>parts</c> is a list of paths in order, written
    /// by <c>saveVideo</c> with one composition and the same options (<c>comp.saveVideo(part, { from, to })</c> encodes a
    /// span). <c>options</c>: <c>{ audio?, start? }</c> — sound for the whole film, as <c>saveVideo</c> takes it, from the
    /// composition time <c>start</c> (0), in place of any the parts carry. Returns what <c>saveVideo</c> returns, with
    /// <c>parts</c>.
    /// </summary>
    public Dictionary<string, object?> JoinVideos(object? parts, string filePath, object? options = null) =>
        MotionVideo.Join(parts, filePath, new MotionOptions(options, "Motion.joinVideos", "audio", "start"), projectRoot, this);

    /// <summary>
    /// A looping animated WebP made from a finished video, for a page that shows images and not video:
    /// <c>{ width?, quality?, loop? }</c>. At most 960 wide unless <c>width</c> says otherwise, never wider than the
    /// video, and silent. Returns what was written, read back from the file.
    /// </summary>
    public Dictionary<string, object?> VideoToWebp(string video, string filePath, object? options = null) =>
        MotionVideo.ToWebp(video, filePath, new MotionOptions(options, "Motion.videoToWebp", "width", "quality", "loop"), projectRoot, this);

    /// <summary>
    /// The WebP loop count for a <c>loop</c> option: <c>true</c> (or absent) plays forever, which is 0 in
    /// the file; <c>false</c> plays once and holds the last frame, which is 1; a number plays that many
    /// times.
    /// </summary>
    internal static int LoopCount(object? loop, string who = "Motion.save") => loop switch
    {
        null => 0,
        bool b => b ? 0 : 1,
        _ when double.TryParse(Convert.ToString(loop, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            => n >= 1 && n <= ushort.MaxValue && n == Math.Floor(n)
                ? (int)n
                : throw new ArgumentException($"{who}: loop {n} is not a whole number of plays from 1 to 65535. Use true to loop forever."),
        _ => throw new ArgumentException($"{who}: loop is true (forever), false (play once and hold), or a number of plays.")
    };

    /// <summary>
    /// Writes the loop count into the animated WebP's <c>ANIM</c> chunk. The encoder always writes 0,
    /// forever, and exposes no option for it; the field is two little-endian bytes after the chunk's
    /// four-byte background colour.
    /// </summary>
    private static void SetWebpLoopCount(byte[] webp, int loops)
    {
        for (var at = 12; at + 8 <= webp.Length;)
        {
            var size = BitConverter.ToInt32(webp, at + 4);
            if (webp[at] == 'A' && webp[at + 1] == 'N' && webp[at + 2] == 'I' && webp[at + 3] == 'M' && size >= 6)
            {
                webp[at + 12] = (byte)(loops & 0xff);
                webp[at + 13] = (byte)(loops >> 8);
                return;
            }
            at += 8 + size + (size & 1);
        }

        // No ANIM chunk: every frame was identical, the encoder merged them into one, and wrote a
        // still. There is nothing to loop.
    }

    /// <summary>
    /// Tiles a selection of the held frames into one labelled image — the artifact to *look* at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An agent cannot watch a video.</b> It reads images, so a moving artifact is close to the
    /// worst possible thing to hand it for inspection: it can produce the file and still not perceive
    /// the motion. A contact sheet puts several instants in one image, which is a single read and,
    /// unlike a video, supports comparison — the eye and <c>bitmap.diff</c> both work across cells.
    /// </para>
    /// <para>
    /// Frames are chosen by <c>indices</c>, or evenly spaced when it is omitted. The first and last
    /// held frames are always included in the even spacing, because the ends of a movement are what
    /// a reader checks first — Studio Manual 24's extremes, arriving from a different direction.
    /// </para>
    /// </remarks>
    public Dictionary<string, object?> Sheet(string filePath, object? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (frames.Count == 0)
            throw new InvalidOperationException("Motion.sheet(...) has no frames. Call Motion.frame(...) first.");

        var opt = JsInterop.AsDict(options);
        var picked = SelectIndices(opt);
        var fps = opt != null && opt.Contains("fps") ? Num(opt, "fps", 25f) : (float?)null;
        var (bytes, sheetW, sheetH, cols, rows, format) = Tile([.. picked.Select(i => frames[i])],
            [.. picked.Select(i => Label(i, fps))], opt, Math.Clamp(Num(opt, "scale", 1f), 0.05f, 4f));

        var full = ProjectPath.Resolve(projectRoot, filePath, nameof(filePath), "Write to");
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(full, bytes);

        Record("render", full, new()
        {
            ["motion"] = "sheet",
            ["format"] = format.ToLowerInvariant() is "jpg" ? "jpeg" : format.ToLowerInvariant(),
            ["bytes"] = (long)bytes.Length,
            ["cells"] = picked.Count,
            ["indices"] = picked.ConvertAll(i => (object?)i),
            ["width"] = sheetW,
            ["height"] = sheetH
        });

        return new Dictionary<string, object?>
        {
            ["path"] = filePath,
            ["cells"] = picked.Count,
            ["held"] = frames.Count,
            ["omitted"] = frames.Count - picked.Count,
            ["indices"] = picked.ConvertAll(i => (object?)i),
            ["cols"] = cols,
            ["rows"] = rows,
            ["width"] = sheetW,
            ["height"] = sheetH,
            ["bytes"] = (long)bytes.Length
        };
    }

    /// <summary>
    /// The speed and value graphs of keyed values, written to <paramref name="filePath"/> and measured:
    /// <c>{ path, from, to, fps, curves, width, height, bytes }</c>, each curve <c>{ label, type, keys, peakSpeed, peakAt,
    /// stops, overshoots, holds }</c>. <paramref name="nodes"/> is a node or <c>{ name: node }</c>; <c>options</c>:
    /// <c>{ from?, to?, fps? (24), width? (960), height? (540) }</c>, the range defaulting to the keys'.
    /// </summary>
    /// <remarks>See <see cref="MotionCurves"/>: an agent cannot read a move from stills, and a move is a curve.</remarks>
    public Dictionary<string, object?> Graph(string filePath, object? nodes, object? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        (string, MotionNode)[] curves = nodes switch
        {
            MotionNode one => [("value", one)],
            _ when JsInterop.AsDict(nodes) is { } d => [.. d.Keys.Cast<object>().Select(k => k.ToString()!).Select(name =>
                (name, d[name] as MotionNode ?? throw new ArgumentException(
                    $"Motion.graph's '{name}' is not a node: pass Motion.nodes.animated(...) or another node, as you keyed it.")))],
            _ => throw new ArgumentException("Motion.graph takes a node, or an object of them: Motion.graph(path, { x: node, scale: other })."),
        };
        if (curves.Length is 0 or > 5) throw new ArgumentException($"Motion.graph draws 1 to 5 curves, not {curves.Length}: one graph is read at a glance.");

        var opt = JsInterop.AsDict(options);
        var span = MotionCurves.Span(curves.Select(c => c.Item2));
        var from = opt != null && opt.Contains("from") ? Num(opt, "from", 0) : span?.From;
        var to = opt != null && opt.Contains("to") ? Num(opt, "to", 0) : span?.To;
        if (from is null || to is null)
            throw new ArgumentException("Motion.graph needs from and to for values with no keys of their own: { from: 0, to: 2 }.");
        if (to <= from) throw new ArgumentException($"Motion.graph's to ({to}) must come after its from ({from}).");
        var fps = Num(opt, "fps", 24f);
        var width = (int)Math.Clamp(Num(opt, "width", 960f), 320, 2400);
        var height = (int)Math.Clamp(Num(opt, "height", 540f), 240, 1600);
        if (fps is <= 0 or > 240) throw new ArgumentException($"Motion.graph's fps is between 0 and 240, not {fps}.");

        var (png, reports) = MotionCurves.Plot(curves, from.Value, to.Value, fps, width, height);
        var full = ProjectPath.Resolve(projectRoot, filePath, nameof(filePath), "Write to");
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(full, png);
        Record("render", full, new() { ["motion"] = "graph", ["format"] = "png", ["bytes"] = (long)png.Length, ["width"] = width, ["height"] = height });

        return new()
        {
            ["path"] = filePath,
            ["from"] = from,
            ["to"] = to,
            ["fps"] = (double)fps,
            ["curves"] = reports.Cast<object?>().ToList(),
            ["width"] = width,
            ["height"] = height,
            ["bytes"] = (long)png.Length,
        };
    }

    /// <summary>
    /// Tiles <paramref name="cells"/> into one labelled sheet, each drawn at <paramref name="scale"/>: <c>Motion.sheet</c>'s
    /// layout and <c>comp.sheet</c>'s. <paramref name="opt"/> carries <c>cols, gap, padding, labels, labelHeight,
    /// background, labelColor, fontFamily, format, quality</c>.
    /// </summary>
    internal static (byte[] Bytes, int Width, int Height, int Cols, int Rows, string Format) Tile(
        IReadOnlyList<SKBitmap> cells, IReadOnlyList<string> labelText, IDictionary? opt, float scale)
    {
        var cols = Math.Max(1, (int)Num(opt, "cols", cells.Count <= 6 ? cells.Count : (int)MathF.Ceiling(MathF.Sqrt(cells.Count))));
        var rows = (int)MathF.Ceiling(cells.Count / (float)cols);
        var gap = Num(opt, "gap", 10f);
        var pad = Num(opt, "padding", 12f);
        var labels = opt == null || !opt.Contains("labels") || Convert.ToBoolean(opt["labels"]);
        var labelH = labels ? Num(opt, "labelHeight", 22f) : 0f;

        var cellW = MathF.Max(1f, cells[0].Width * scale);
        var cellH = MathF.Max(1f, cells[0].Height * scale);
        var sheetW = (int)MathF.Ceiling(cols * cellW + (cols + 1) * gap + pad * 2);
        var sheetH = (int)MathF.Ceiling(rows * (cellH + labelH) + (rows + 1) * gap + pad * 2);

        using var sheet = new SKBitmap(sheetW, sheetH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(SkiaColorParser.Parse(opt?["background"]?.ToString() ?? "#e4e0d4"));

        using var font = new SKFont(SKTypeface.FromFamilyName(opt?["fontFamily"]?.ToString() ?? "Georgia"),
            Math.Max(7f, labelH * 0.62f));
        using var ink = new SKPaint { Color = SkiaColorParser.Parse(opt?["labelColor"]?.ToString() ?? "#15151a"), IsAntialias = true };
        using var border = new SKPaint { Color = ink.Color, IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true };

        for (var i = 0; i < cells.Count; i++)
        {
            var frame = cells[i];
            var x = pad + (i % cols) * (cellW + gap) + gap;
            var y = pad + (i / cols) * (cellH + labelH + gap) + gap;

            // Mipmapped, so a cell smaller than its frame averages the pixels it covers. Point-sampled,
            // a 2px line at scale 0.4 falls between samples and leaves the cell, so the ground under a
            // figure simply vanished from a live run's spacing sheet.
            using (var image = SKImage.FromBitmap(frame))
                canvas.DrawImage(image,
                    SKRect.Create(0, 0, frame.Width, frame.Height),
                    SKRect.Create(x, y, cellW, cellH),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.DrawRect(SKRect.Create(x, y, cellW, cellH), border);

            if (!labels) continue;
            TextShaper.DrawText(canvas, labelText[i], x, y + cellH + labelH * 0.72f, SKTextAlign.Left, font, ink);
        }

        var format = opt?["format"]?.ToString() ?? "png";
        return (SkiaImageEncoder.Encode(sheet, format, (int)Num(opt, "quality", 92f)), sheetW, sheetH, cols, rows, format);
    }

    /// <summary>
    /// A cell's label. A captured frame carries its film frame and time, so it says those. A frame added
    /// by hand says only where it is held, unless the caller passed <c>fps</c> to assert the frames are
    /// evenly spaced at that rate: a live run handed the sheet eleven beat renders and an fps, and got
    /// labels at 0.08 s steps that were never in the film.
    /// </summary>
    internal string Label(int index, float? fps) => times[index] is { } at
        ? $"f{at.Frame}  ·  {at.Seconds:0.00}s"
        : fps is > 0f ? $"#{index}  ·  {index / fps.Value:0.00}s" : $"#{index}";

    /// <summary>Which frames the sheet shows: named outright, or spread across what is held.</summary>
    private List<int> SelectIndices(IDictionary? opt)
    {
        var picked = new List<int>();

        if (opt != null && opt.Contains("indices") && opt["indices"] is IEnumerable given and not string)
        {
            foreach (var value in given)
            {
                var i = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if (i < 0 || i >= frames.Count)
                    throw new ArgumentOutOfRangeException(nameof(opt),
                        $"Frame {i} was asked for but only 0..{frames.Count - 1} are held.");
                picked.Add(i);
            }
            if (picked.Count > 0) return picked;
        }

        var count = Math.Clamp((int)Num(opt, "count", 6f), 1, frames.Count);
        if (count == 1) { picked.Add(0); return picked; }

        // Inclusive of both ends: the extremes of a movement are what a reader checks first.
        for (var i = 0; i < count; i++)
            picked.Add((int)MathF.Round(i * (frames.Count - 1) / (float)(count - 1)));

        return picked;
    }

    /// <summary>Tells the host about a file just written. Never throws: a record is not worth a failed script.</summary>
    internal void Record(string type, string fullPath, Dictionary<string, object?> fields)
    {
        if (recorder is null) return;
        try { recorder(type, fullPath, fields); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Motion: recording {fullPath} failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }

    private SKBitmap? Rasterise(object? source, int? width, int? height) => source switch
    {
        SnapPaper paper => SvgRenderPipeline.RenderToBitmap(
            paper.Document,
            width ?? (int)MathF.Ceiling(paper.Width),
            height ?? (int)MathF.Ceiling(paper.Height),
            SKColors.Transparent),
        SkiaCanvas canvas => Copy(canvas.SkBitmap, width, height),
        SkiaBitmapWrapper wrapper => Copy(wrapper.Bitmap, width, height),
        _ => null
    };

    /// <summary>A detached copy, resampled only when a size was actually asked for.</summary>
    private static SKBitmap Copy(SKBitmap source, int? width, int? height)
    {
        var w = width ?? source.Width;
        var h = height ?? source.Height;

        var copy = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(copy);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, SKRect.Create(0, 0, source.Width, source.Height), SKRect.Create(0, 0, w, h));
        return copy;
    }

    private static float Num(IDictionary? options, string key, float fallback) =>
        options != null && options.Contains(key) && options[key] != null
            ? Convert.ToSingle(options[key], CultureInfo.InvariantCulture)
            : fallback;
    #endregion
}
