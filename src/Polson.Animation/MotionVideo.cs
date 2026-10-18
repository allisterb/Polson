namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polson.Drawing.Skia;
using SkiaSharp;

/// <summary>
/// What <c>Motion.saveVideo</c> and <c>comp.saveVideo</c> share: reading their options, naming the recordings to mix
/// in, and saying what was written, including what of the sound was not heard. Also what a finished video becomes:
/// joined with others (<c>Motion.joinVideos</c>) or made a looping WebP (<c>Motion.videoToWebp</c>).
/// </summary>
internal static class MotionVideo
{
    #region Methods
    /// <summary>
    /// Encodes <paramref name="frames"/> into the project at <paramref name="filePath"/>. <paramref name="filmStart"/> is
    /// the composition time of the first frame, which a recording's start is measured against.
    /// </summary>
    public static Dictionary<string, object?> Save(string filePath, IEnumerable<SKBitmap> frames, int count, int width,
        int height, double fps, double filmStart, MotionOptions o, string? projectRoot, MotionToolkit? motion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var who = o.Who;
        if (fps <= 0 || fps > 240) throw new ArgumentException($"{who}: fps {fps} is not between 0 and 240.");

        var full = ProjectPath.Resolve(projectRoot, filePath, nameof(filePath), "Write to");
        var format = Format(full, who);
        var quality = o.Number("quality", 80);
        if (quality is < 1 or > 100) throw new ArgumentException($"{who}'s quality is from 1 to 100, not {quality}.");
        var background = SkiaColorParser.Parse(o.Raw("background")?.ToString() ?? "#ffffff");
        var tracks = Tracks(o.Raw("audio"), filmStart, projectRoot, who);

        if (!FFmpeg.Available)
            throw new InvalidOperationException($"{who} needs the 'ffmpeg' program and none was found: {FFmpeg.Install}.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var encoded = FFmpeg.Encode(frames, count, width, height, fps, full, format, (int)Math.Round(quality),
            background.WithAlpha(255), tracks, who);

        var seconds = encoded.Frames / fps;
        var warnings = new List<object?>();
        if (encoded.Width != width || encoded.Height != height)
            warnings.Add($"The frames are {width}x{height}; video needs an even size, so it was padded to {encoded.Width}x{encoded.Height} with the background.");
        var audio = Report(tracks, seconds, filmStart, fps, warnings);

        motion?.Record("render", full, new()
        {
            ["motion"] = "video",
            ["format"] = format == FFmpeg.VideoFormat.Mp4 ? "mp4" : "webm",
            ["bytes"] = encoded.Bytes,
            ["frames"] = encoded.Frames,
            ["durationMs"] = seconds * 1000,
            ["width"] = encoded.Width,
            ["height"] = encoded.Height,
            ["audio"] = tracks.Select(t => (object?)t.Given).ToList()
        });

        return new Dictionary<string, object?>
        {
            ["path"] = filePath,
            ["format"] = format == FFmpeg.VideoFormat.Mp4 ? "mp4" : "webm",
            ["frames"] = encoded.Frames,
            ["fps"] = fps,
            ["start"] = filmStart,
            ["durationMs"] = seconds * 1000,
            ["width"] = encoded.Width,
            ["height"] = encoded.Height,
            ["bytes"] = encoded.Bytes,
            ["audio"] = audio,
            ["warnings"] = warnings
        };
    }

    /// <summary>
    /// Joins videos <c>Motion.saveVideo</c> or <c>comp.saveVideo</c> wrote, end to end, into one at
    /// <paramref name="filePath"/> without re-encoding them: a film too long to encode inside one script is encoded a span
    /// at a time (<c>comp.saveVideo(part, { from, to })</c>) and joined here. <c>options</c>: <c>{ audio?, start? }</c> —
    /// sound for the whole film, as <c>saveVideo</c> takes it, measured from <c>start</c> (the composition time the
    /// joined film begins at, 0), in place of any sound the parts carry.
    /// </summary>
    public static Dictionary<string, object?> Join(object? parts, string filePath, MotionOptions o, string? projectRoot, MotionToolkit? motion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var who = o.Who;
        var full = ProjectPath.Resolve(projectRoot, filePath, nameof(filePath), "Write to");
        var format = Format(full, who);
        var given = parts is string || parts is not IEnumerable list
            ? throw new ArgumentException($"{who} takes the parts as a list of paths, in order: ['part1.mp4', 'part2.mp4'].")
            : list.Cast<object?>().Select(p => p as string is { Length: > 0 } s ? s
                : throw new ArgumentException($"{who}: every part is a path, not {p?.GetType().Name ?? "nothing"}.")).ToList();
        if (given.Count == 0) throw new ArgumentException($"{who} needs at least one part.");

        var probed = given.Select(g =>
        {
            var path = ProjectPath.Resolve(projectRoot, g, "parts", "Read");
            if (!File.Exists(path)) throw new ArgumentException($"{who}: there is no part at '{g}'.");
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{who}: '{g}' is a part and the output too; write the joined film to a new path.");
            if (Format(path, who) != format)
                throw new ArgumentException($"{who}: '{g}' is not the output's container; join .mp4 parts into an .mp4, .webm into a .webm.");
            if (!FFmpeg.Available) throw new InvalidOperationException($"{who} needs the 'ffmpeg' program and none was found: {FFmpeg.Install}.");
            return (Given: g, Path: path, Info: FFmpeg.Probe(path, g, format, who));
        }).ToList();

        // Stream copy joins the streams as they are, so the parts must match: say which one does not, and how.
        var first = probed[0];
        foreach (var p in probed.Skip(1))
        {
            var (a, b) = (first.Info, p.Info);
            if (a.Codec != b.Codec || a.Width != b.Width || a.Height != b.Height || Math.Abs(a.Fps - b.Fps) > 0.01)
                throw new ArgumentException($"{who}: '{p.Given}' is {b.Codec} {b.Width}x{b.Height} at {b.Fps} fps where '{first.Given}' is " +
                    $"{a.Codec} {a.Width}x{a.Height} at {a.Fps} fps. Encode every part of one film with the same composition and saveVideo options.");
        }
        var start = o.Number("start", 0);
        var tracks = Tracks(o.Raw("audio"), start, projectRoot, who);
        var warnings = new List<object?>();
        if (tracks.Count == 0 && probed.Select(p => p.Info.HasAudio).Distinct().Count() > 1)
            throw new ArgumentException($"{who}: some parts have sound and some do not, so they cannot be joined as they are. " +
                "Encode the parts silent and give the sound here, as audio, for the whole film.");
        if (tracks.Count > 0 && probed.Any(p => p.Info.HasAudio))
            warnings.Add("The parts' own sound was replaced by the audio given here.");

        var seconds = probed.Sum(p => p.Info.Seconds);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = FFmpeg.Join([.. probed.Select(p => p.Path)], full, format, tracks, seconds, who);
        var audio = Report(tracks, seconds, start, first.Info.Fps, warnings);

        motion?.Record("render", full, new()
        {
            ["motion"] = "video",
            ["format"] = format == FFmpeg.VideoFormat.Mp4 ? "mp4" : "webm",
            ["bytes"] = bytes,
            ["frames"] = (int)Math.Round(seconds * first.Info.Fps),
            ["durationMs"] = seconds * 1000,
            ["width"] = first.Info.Width,
            ["height"] = first.Info.Height,
            ["parts"] = given.Cast<object?>().ToList(),
            ["audio"] = tracks.Select(t => (object?)t.Given).ToList()
        });

        return new Dictionary<string, object?>
        {
            ["path"] = filePath,
            ["format"] = format == FFmpeg.VideoFormat.Mp4 ? "mp4" : "webm",
            ["parts"] = given.Count,
            ["frames"] = (int)Math.Round(seconds * first.Info.Fps),
            ["fps"] = first.Info.Fps,
            ["start"] = start,
            ["durationMs"] = seconds * 1000,
            ["width"] = first.Info.Width,
            ["height"] = first.Info.Height,
            ["bytes"] = bytes,
            ["audio"] = audio,
            ["warnings"] = warnings
        };
    }

    /// <summary>
    /// A looping animated WebP at <paramref name="filePath"/> made from the finished video at <paramref name="video"/>, for
    /// a page or a README that shows an image and not a video. <c>options</c>: <c>{ width?, quality?, loop? }</c> — at
    /// most 960 wide unless given and never wider than the video, quality 80, looping forever.
    /// </summary>
    public static Dictionary<string, object?> ToWebp(string video, string filePath, MotionOptions o, string? projectRoot, MotionToolkit? motion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(video);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var who = o.Who;
        var source = ProjectPath.Resolve(projectRoot, video, nameof(video), "Read");
        var full = ProjectPath.Resolve(projectRoot, filePath, nameof(filePath), "Write to");
        if (!string.Equals(Path.GetExtension(full), ".webp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{who} writes an animated .webp, not '{Path.GetExtension(full)}'.");
        if (Path.GetExtension(source).ToLowerInvariant() is not (".mp4" or ".webm"))
            throw new ArgumentException($"{who} reads a finished .mp4 or .webm, as saveVideo or joinVideos wrote it, not '{Path.GetExtension(source)}'.");
        if (!File.Exists(source)) throw new ArgumentException($"{who}: there is no video at '{video}'.");
        if (!FFmpeg.Available) throw new InvalidOperationException($"{who} needs the 'ffmpeg' program and none was found: {FFmpeg.Install}.");

        var format = Format(source, who);
        var info = FFmpeg.Probe(source, video, format, who);
        var quality = o.Number("quality", 80);
        if (quality is < 1 or > 100) throw new ArgumentException($"{who}'s quality is from 1 to 100, not {quality}.");
        var width = o.Number("width", Math.Min(960, info.Width));
        if (width != Math.Floor(width) || width < 16 || width > info.Width)
            throw new ArgumentException($"{who}'s width is a whole number from 16 to the video's own {info.Width}, not {width}: " +
                "a WebP is made at the video's size or smaller.");
        var loops = MotionToolkit.LoopCount(o.Raw("loop"), who);
        var warnings = new List<object?>();
        if (info.HasAudio) warnings.Add("The video's sound was left out: an animated WebP is silent.");

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = FFmpeg.ToWebp(source, format, full, (int)width, (int)Math.Round(quality), loops, who);

        // What the file holds, read back from the file: the encoder merges frames that do not change and sums their
        // durations, so the frames stored are not the frames the video had.
        using var codec = SKCodec.Create(new MemoryStream(File.ReadAllBytes(full)))
            ?? throw new InvalidOperationException($"{who}: the WebP ffmpeg wrote could not be read back.");
        var frames = (int)Math.Round(info.Seconds * info.Fps);
        var durationMs = codec.FrameInfo.Sum(f => (double)f.Duration);
        if (codec.FrameCount <= 1) warnings.Add("Every frame was the same, so the WebP is a still image and does not play or loop.");

        motion?.Record("render", full, new()
        {
            ["motion"] = "animation",
            ["format"] = "webp",
            ["bytes"] = bytes,
            ["frames"] = frames,
            ["storedFrames"] = codec.FrameCount,
            ["durationMs"] = durationMs,
            ["width"] = codec.Info.Width,
            ["height"] = codec.Info.Height,
            ["video"] = video
        });

        return new Dictionary<string, object?>
        {
            ["path"] = filePath,
            ["format"] = "webp",
            ["video"] = video,
            ["frames"] = frames,
            ["storedFrames"] = codec.FrameCount,
            ["fps"] = info.Fps,
            ["durationMs"] = durationMs,
            ["width"] = codec.Info.Width,
            ["height"] = codec.Info.Height,
            ["loop"] = loops == 0 ? true : loops == 1 ? false : loops,
            ["bytes"] = bytes,
            ["warnings"] = warnings
        };
    }

    /// <summary>The container an output or a part is, from its extension.</summary>
    static FFmpeg.VideoFormat Format(string full, string who) => Path.GetExtension(full).ToLowerInvariant() switch
    {
        ".mp4" => FFmpeg.VideoFormat.Mp4,
        ".webm" => FFmpeg.VideoFormat.WebM,
        var ext => throw new ArgumentException(
            $"{who} writes .mp4 (H.264 and AAC) or .webm (VP9 and Opus), not '{ext}'. For an animated WebP use Motion.save.")
    };

    /// <summary>Each recording as it was mixed — where it starts, how long it is, how much was cut — warning of what was not heard.</summary>
    static List<object?> Report(IReadOnlyList<FFmpeg.AudioTrack> tracks, double seconds, double filmStart, double fps, List<object?> warnings) =>
        [.. tracks.Select(t =>
        {
            var length = FFmpeg.WavSeconds(t.FullPath);
            var cut = length is { } l ? Math.Max(0, t.Offset + l - seconds) : (double?)null;
            if (t.Offset >= seconds)
                warnings.Add($"'{t.Given}' starts at {t.Offset + filmStart:0.00} s, after the film ends at {filmStart + seconds:0.00} s, so none of it is heard.");
            else if (cut > 1 / fps)
                warnings.Add($"'{t.Given}' runs {cut:0.00} s past the end of the film and was cut there. Lengthen the film, or start it sooner.");
            return (object?)new Dictionary<string, object?>
            {
                ["path"] = t.Given,
                ["start"] = t.Offset + filmStart,
                ["duration"] = length,
                ["cut"] = cut
            };
        })];

    /// <summary>
    /// The recordings to mix in: a path, which starts at 0; a line of speech timed by a recording, which starts where the
    /// line does; <c>{ path, start }</c>; or a list of these.
    /// </summary>
    static List<FFmpeg.AudioTrack> Tracks(object? audio, double filmStart, string? projectRoot, string who)
    {
        var tracks = new List<FFmpeg.AudioTrack>();
        if (audio is null) return tracks;
        List<object?> items = audio is string or MotionSpeech || JsInterop.AsDict(audio) is not null || audio is not IEnumerable list
            ? [audio] : list.Cast<object?>().ToList();

        foreach (var item in items)
        {
            var (given, start) = item switch
            {
                string path => (path, 0d),
                MotionSpeech { Audio: { } path } line => (path, line.Start),
                MotionSpeech line => throw new ArgumentException(
                    $"{who}: the line \"{line.Text}\" was timed by the studio's rules and has no recording to play. Pass Motion.speech(text, {{ audio }}), or a path."),
                _ when JsInterop.AsDict(item) is { } d => Track(d, who),
                _ => throw new ArgumentException(
                    $"{who}'s audio is a path, a line of speech with a recording, {{ path, start }}, or a list of these.")
            };
            if (given.Length == 0) throw new ArgumentException($"{who}: an audio path is empty.");
            var full = ProjectPath.Resolve(projectRoot, given, "audio", "Read");
            if (!File.Exists(full)) throw new ArgumentException($"{who}: there is no recording at '{given}'.");
            tracks.Add(new(given, full, FFmpeg.Demuxer(full, given, who), start - filmStart));
        }
        return tracks;
    }

    static (string Path, double Start) Track(IDictionary d, string who)
    {
        var o = new MotionOptions(d, $"{who}'s audio", "path", "start");
        return (o.Raw("path") as string ?? throw new ArgumentException($"{who}: {{ path, start }} needs a path."),
            o.Number("start", 0));
    }
    #endregion
}
