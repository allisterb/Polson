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
/// in, and saying what was written, including what of the sound was not heard.
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
        var format = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".mp4" => FFmpeg.VideoFormat.Mp4,
            ".webm" => FFmpeg.VideoFormat.WebM,
            var ext => throw new ArgumentException(
                $"{who} writes .mp4 (H.264 and AAC) or .webm (VP9 and Opus), not '{ext}'. For an animated WebP use Motion.save.")
        };
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
        var audio = tracks.Select(t =>
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
        }).ToList();

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
