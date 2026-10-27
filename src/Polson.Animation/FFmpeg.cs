namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;

/// <summary>
/// Video with sound, by way of <c>ffmpeg</c>: Skia frames in over a pipe, an MP4 or WebM out, joined or made into an
/// animated WebP. Also decodes a recording that is not 16-bit PCM WAV into one, for the aligner.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate program, not a library we link.</b> The usual builds are GPL (they carry x264), and running one as its
/// own process is aggregation, as potrace and Blender are; a native fault in a decoder is an exit code, not the MCP
/// server going down. See the ledger row in <c>reference/README.md</c>.
/// </para>
/// <para>
/// <b>A script never writes an argument.</b> ffmpeg opens URLs, follows playlists and reads files a crafted input
/// names, so every argument is built here from typed settings: the frames arrive raw over stdin, each recording is
/// read with the demuxer its extension and first bytes agree on (never a probed one), only the <c>file</c> and
/// <c>pipe</c> protocols are allowed, and every path is spelled <c>file:</c> so none can be read as a protocol.
/// </para>
/// <para>
/// Found in <c>bin/ffmpeg/</c> (put there by <c>tools/bootstrap.py --only ffmpeg</c> on Windows), then on the PATH,
/// which is where Debian's package lands; the host sets <c>Tools:FFmpeg</c> to override. The engine reads no
/// environment variables.
/// </para>
/// </remarks>
public static class FFmpeg
{
    #region Fields
    static string? _override;
    static string? _resolved;
    static bool _looked;

    /// <summary>How long ffmpeg may take to finish once the last frame is in.</summary>
    const int FinishMs = 300_000;
    #endregion

    #region Properties
    /// <summary>An explicit path to the binary, or null to discover one.</summary>
    public static string? ExecutableOverride
    {
        get => _override;
        set { _override = value; _looked = false; _resolved = null; }
    }

    /// <summary>The binary that will be used, or null when none was found.</summary>
    public static string? Executable
    {
        get
        {
            if (_looked) return _resolved;
            _looked = true;
            return _resolved = Discover();
        }
    }

    /// <summary>Whether video can be written here.</summary>
    public static bool Available => Executable is not null;

    /// <summary>How to get it, for a refusal to end with.</summary>
    internal const string Install =
        "run py tools/bootstrap.py --only ffmpeg (Windows), apt-get install ffmpeg (Debian), or set Tools:FFmpeg";
    #endregion

    #region Methods
    /// <summary>The first line of <c>ffmpeg -version</c>, or null when there is no binary.</summary>
    public static string? Version()
    {
        if (Executable is not { } exe) return null;
        try
        {
            var info = Info(exe, "-hide_banner", "-version");
            using var process = Process.Start(info);
            if (process is null) return null;
            var line = process.StandardOutput.ReadLine();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return line?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One frame of <paramref name="video"/> at <paramref name="seconds"/>, written as the PNG <paramref name="png"/>; null when
    /// it was written, else why not. What <c>Verify.film</c> shows the agent where the read-back read something the film
    /// never set, so it looks at the frame before calling it a misread or a fault.
    /// </summary>
    public static string? Frame(string video, double seconds, string png)
    {
        if (Executable is not { } exe) return $"no 'ffmpeg' was found: {Install}.";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png))!);
            var info = Info(exe, "-hide_banner", "-loglevel", "error", "-y", "-ss",
                Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", png);
            using var process = Process.Start(info);
            if (process is null) return "ffmpeg did not start.";
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000)) { process.Kill(true); return "ffmpeg took more than 30 s for one frame."; }
            return process.ExitCode == 0 && File.Exists(png) ? null : $"ffmpeg failed: {error.Result.Trim()}";
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// A silent copy of <paramref name="video"/>, <paramref name="seconds"/> long, at most <paramref name="maxHeight"/> tall and
    /// encoded to fit <paramref name="maxBytes"/>, written as <paramref name="output"/>; null when written, else why not. What
    /// <c>Verify.film</c> sends when the film itself is over the watcher's limit: a grainy 1080 master ran 65 MB (becool2).
    /// </summary>
    /// <remarks>
    /// The bitrate is a share of what fits, capped by VBV; an encode that still comes out too big is tried again at a smaller
    /// share. Read with the <c>file</c> protocol only, and moved into place only when it fits.
    /// </remarks>
    public static string? WatchCopy(string video, double seconds, string output, int maxHeight, long maxBytes)
    {
        if (Executable is not { } exe) return $"no 'ffmpeg' was found: {Install}.";
        var partial = output + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            for (var share = 0.85; share > 0.3; share *= 0.7)
            {
                var kbps = Math.Max(1, (long)(maxBytes * 8 * share / Math.Max(1, seconds) / 1000)).ToString(CultureInfo.InvariantCulture);
                var info = Info(exe, "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-protocol_whitelist", "file",
                    "-i", "file:" + video, "-an", "-vf", $"scale=-2:'min(ih,{maxHeight})':flags=lanczos",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-maxrate", kbps + "k", "-bufsize", kbps + "k",
                    "-pix_fmt", "yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
                    "-movflags", "+faststart", "-f", "mp4", "file:" + partial);
                using var process = Process.Start(info);
                if (process is null) return "ffmpeg did not start.";
                process.StandardInput.Close();
                var error = process.StandardError.ReadToEndAsync();
                process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(FinishMs)) { Kill(process); return $"ffmpeg took more than {FinishMs / 1000} s for the copy."; }
                if (process.ExitCode != 0) return $"ffmpeg failed: {Trim(error.Result)}";
                if (new FileInfo(partial).Length > maxBytes) continue;
                MoveIntoPlace(partial, output, "Verify.film");
                return null;
            }
            return $"no copy {maxHeight} px tall came in under {maxBytes / (1024 * 1024.0):0.#} MB; the film is too long to watch in one read.";
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>
    /// Encodes <paramref name="frames"/>, each <paramref name="width"/>×<paramref name="height"/>, at
    /// <paramref name="fps"/>, mixing <paramref name="tracks"/> in at their offsets, into <paramref name="output"/>.
    /// </summary>
    /// <remarks>
    /// The frames are drawn onto <paramref name="background"/> (video has no alpha) and padded by a pixel on the right or
    /// bottom to an even size, which 4:2:0 chroma needs. They are not disposed here, and each is finished with before the
    /// next is asked for, so an iterator may render one at a time and dispose it as it moves on. Written beside the
    /// output and moved into place only when ffmpeg succeeds, so a failed encode never leaves a file that looks done.
    /// </remarks>
    internal static Encoded Encode(IEnumerable<SKBitmap> frames, int frameCount, int width, int height, double fps,
        string output, VideoFormat format, int quality, SKColor background, IReadOnlyList<AudioTrack> tracks, string who)
    {
        if (Executable is not { } exe)
            throw new InvalidOperationException($"{who} needs the 'ffmpeg' program and none was found: {Install}.");
        if (frameCount < 1) throw new InvalidOperationException($"{who} has no frames to encode.");

        var w = width + (width & 1);
        var h = height + (height & 1);
        // Its own name per encode: a host that gave up waiting and retried used to meet the first encode, still running,
        // holding the one shared `.part`, and was refused "Access to the path is denied".
        var partial = $"{output}.{Guid.NewGuid():N}.part";
        var info = Info(exe, [.. Arguments(w, h, fps, frameCount, partial, format, quality, tracks)]);

        using var flat = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(flat);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{who}: ffmpeg would not start.");
        // Read at once, so a chatty stderr cannot fill its pipe and stall the encoder.
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();

        // A write that fails means ffmpeg has stopped reading, which it does when it has failed: its stderr says why.
        var stdin = process.StandardInput.BaseStream;
        var count = 0;
        var broken = false;
        try
        {
            foreach (var frame in frames)
            {
                // The script's limit, checked here because Jint cannot stop native work: past it, the encode stops and
                // keeps nothing, which is what the timeout's message then truthfully says (`ScriptDeadline`).
                ScriptDeadline.ThrowIfPassed(who);
                if (frame.Width != width || frame.Height != height)
                    throw new InvalidOperationException(
                        $"{who}: every frame must be {width}x{height}; frame {count} is {frame.Width}x{frame.Height}.");
                canvas.Clear(background);
                using (var image = SKImage.FromPixels(frame.PeekPixels()))
                    canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default, null);
                canvas.Flush();
                try { stdin.Write(flat.GetPixelSpan()); }
                catch (IOException) { broken = true; break; }
                count++;
            }
        }
        catch
        {
            Close(stdin);
            Kill(process);
            File.Delete(partial);
            throw;
        }
        broken |= !Close(stdin);

        if (!process.WaitForExit(FinishMs))
        {
            Kill(process);
            File.Delete(partial);
            throw new TimeoutException($"{who}: ffmpeg did not finish within {FinishMs / 1000} s of the last frame.");
        }
        if (broken || process.ExitCode != 0 || count != frameCount)
        {
            File.Delete(partial);
            throw new InvalidOperationException(broken || process.ExitCode != 0
                ? $"{who}: ffmpeg failed (exit {process.ExitCode}). {Trim(stderr.Result)}"
                : $"{who}: {count} frames arrived where {frameCount} were expected.");
        }

        MoveIntoPlace(partial, output, who);
        return new(count, w, h, new FileInfo(output).Length);
    }

    /// <summary>
    /// What a finished video is, read by ffmpeg from the file's own headers: its codec, size, frame rate, length and
    /// whether it has sound. Only a file that begins like its container is opened, and with that container's demuxer.
    /// </summary>
    internal static VideoInfo Probe(string fullPath, string given, VideoFormat format, string who)
    {
        if (Executable is not { } exe)
            throw new InvalidOperationException($"{who} needs the 'ffmpeg' program and none was found: {Install}.");
        var head = new byte[12];
        int read;
        using (var f = File.OpenRead(fullPath)) read = f.Read(head, 0, head.Length);
        var begins = format == VideoFormat.Mp4
            ? read >= 8 && Encoding.ASCII.GetString(head, 4, 4) == "ftyp"
            : read >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
        if (!begins)
            throw new ArgumentException($"{who}: '{given}' does not begin like a {(format == VideoFormat.Mp4 ? ".mp4" : ".webm")}, so it was not read.");

        // With no output ffmpeg describes its input on stderr and exits non-zero; the description is what is wanted.
        var info = Info(exe, "-hide_banner", "-nostdin", "-protocol_whitelist", "file",
            "-f", format == VideoFormat.Mp4 ? "mov" : "matroska", "-i", "file:" + fullPath);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{who}: ffmpeg would not start.");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            Kill(process);
            throw new TimeoutException($"{who}: reading '{given}' took longer than 60 s.");
        }
        var text = stderr.Result;
        var video = System.Text.RegularExpressions.Regex.Match(text, @"Stream #0:\d+.*?: Video: (\w+).*?, (\d+)x(\d+).*?, ([\d.]+) fps");
        var duration = System.Text.RegularExpressions.Regex.Match(text, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!video.Success || !duration.Success)
            throw new ArgumentException($"{who}: '{given}' could not be read as a video. {Trim(text)}");
        double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        return new(video.Groups[1].Value, int.Parse(video.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(video.Groups[3].Value, CultureInfo.InvariantCulture), D(video.Groups[4].Value),
            D(duration.Groups[1].Value) * 3600 + D(duration.Groups[2].Value) * 60 + D(duration.Groups[3].Value),
            System.Text.RegularExpressions.Regex.IsMatch(text, @"Stream #0:\d+.*?: Audio: "));
    }

    /// <summary>
    /// Joins <paramref name="parts"/>, end to end and in order, into <paramref name="output"/> without re-encoding them,
    /// and with <paramref name="tracks"/> mixes sound over the whole in place of any the parts had.
    /// </summary>
    /// <remarks>
    /// ffmpeg's concat demuxer reads a list naming the parts; the list is written here, from paths already resolved inside
    /// the project, and only the <c>file</c> protocol is allowed, so nothing a script gives can make it read another file.
    /// The parts must share a codec, size and frame rate — <see cref="Probe"/> checks first — because stream copy joins the
    /// streams as they are. Written beside the output and moved into place only when ffmpeg succeeds.
    /// </remarks>
    internal static long Join(IReadOnlyList<string> parts, string output, VideoFormat format, IReadOnlyList<AudioTrack> tracks,
        double seconds, string who)
    {
        if (Executable is not { } exe)
            throw new InvalidOperationException($"{who} needs the 'ffmpeg' program and none was found: {Install}.");
        string N(double x) => x.ToString("0.######", CultureInfo.InvariantCulture);
        var partial = output + ".part";
        var list = output + ".parts.txt";
        // ffconcat: one file a line, single-quoted; a quote inside a path closes, escapes and reopens.
        File.WriteAllText(list, "ffconcat version 1.0\n" + string.Concat(parts.Select(p =>
            $"file '{p.Replace('\\', '/').Replace("'", "'\\''")}'\n")), new UTF8Encoding(false));

        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-protocol_whitelist", "file", "-f", "concat", "-safe", "0", "-i", "file:" + list };
        foreach (var t in tracks)
        {
            args.AddRange(["-protocol_whitelist", "file"]);
            if (t.Offset < 0) args.AddRange(["-ss", N(-t.Offset)]);
            args.AddRange(["-f", t.Demuxer, "-i", "file:" + t.FullPath]);
        }
        if (tracks.Count == 0) args.AddRange(["-map", "0", "-c", "copy"]);
        else
        {
            var graph = new StringBuilder();
            for (var i = 0; i < tracks.Count; i++)
            {
                var delay = Math.Max(0, (long)Math.Round(tracks[i].Offset * 1000));
                graph.Append($"[{i + 1}:a]aresample=48000,adelay=delays={delay}:all=1[a{i}];");
            }
            graph.Append(string.Concat(tracks.Select((_, i) => $"[a{i}]")));
            if (tracks.Count > 1) graph.Append($"amix=inputs={tracks.Count}:duration=longest:dropout_transition=0:normalize=0,");
            graph.Append("apad[a]");
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "0:v", "-map", "[a]", "-c:v", "copy"]);
            args.AddRange(format == VideoFormat.Mp4 ? ["-c:a", "aac", "-b:a", "192k"] : ["-c:a", "libopus", "-b:a", "128k"]);
            args.AddRange(["-t", N(seconds)]);
        }
        if (format == VideoFormat.Mp4) args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-f", format == VideoFormat.Mp4 ? "mp4" : "webm", "file:" + partial]);

        try
        {
            using var process = Process.Start(Info(exe, [.. args])) ?? throw new InvalidOperationException($"{who}: ffmpeg would not start.");
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(FinishMs))
            {
                Kill(process);
                throw new TimeoutException($"{who}: ffmpeg did not finish joining within {FinishMs / 1000} s.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{who}: ffmpeg failed to join the parts (exit {process.ExitCode}). {Trim(stderr.Result)}");
            MoveIntoPlace(partial, output, who);
            return new FileInfo(output).Length;
        }
        finally
        {
            File.Delete(list);
            File.Delete(partial);
        }
    }

    /// <summary>
    /// Re-encodes the finished video at <paramref name="input"/> as an animated WebP <paramref name="width"/> wide, the
    /// height keeping the aspect, playing <paramref name="loops"/> times (0 is forever) and silent.
    /// </summary>
    /// <remarks>
    /// Read with its own container's demuxer and only the <c>file</c> protocol, as <see cref="Join"/> reads parts. The
    /// frames reach libwebp as RGB decoded with the bt709 matrix they were written with: WebP's own YUV is bt601, so
    /// handing it the video's planes as they are would shift every colour. Written beside the output and moved into place
    /// only when ffmpeg succeeds.
    /// </remarks>
    internal static long ToWebp(string input, VideoFormat format, string output, int width, int quality, int loops, string who)
    {
        if (Executable is not { } exe)
            throw new InvalidOperationException($"{who} needs the 'ffmpeg' program and none was found: {Install}.");
        string N(double x) => x.ToString("0.######", CultureInfo.InvariantCulture);
        var partial = output + ".part";
        var info = Info(exe, "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-protocol_whitelist", "file", "-f", format == VideoFormat.Mp4 ? "mov" : "matroska", "-i", "file:" + input,
            "-an", "-vf", $"scale={width}:-1:flags=lanczos:in_color_matrix=bt709:in_range=tv,format=bgra",
            "-c:v", "libwebp_anim", "-quality", N(quality), "-compression_level", "4", "-loop", N(loops),
            "-f", "webp", "file:" + partial);

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"{who}: ffmpeg would not start.");
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(FinishMs))
            {
                Kill(process);
                throw new TimeoutException($"{who}: ffmpeg did not finish the WebP within {FinishMs / 1000} s.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{who}: ffmpeg failed to write the WebP (exit {process.ExitCode}). {Trim(stderr.Result)}");
            MoveIntoPlace(partial, output, who);
            return new FileInfo(output).Length;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>
    /// Sets <paramref name="cells"/> — finished videos, each at its place and size — over <paramref name="background"/>,
    /// a <paramref name="width"/>×<paramref name="height"/> RGBA frame carrying the labels, into the MP4
    /// <paramref name="output"/>, <paramref name="seconds"/> long. A film shorter than that holds its last frame, dimmed,
    /// as the page's grid does. With <paramref name="sound"/> the sound is that cell's; without it the video is silent.
    /// </summary>
    /// <remarks>
    /// The background arrives raw over stdin, as <see cref="Encode"/>'s frames do, and is held for the whole length by
    /// <c>loop</c>; the videos are read as <see cref="Join"/> reads parts. Each is fitted inside its cell keeping its
    /// shape, so a film of another aspect is letterboxed rather than stretched. Written beside the output and moved into
    /// place only when ffmpeg succeeds.
    /// </remarks>
    internal static long Grid(SKBitmap background, IReadOnlyList<GridCell> cells, double fps, double seconds, int? sound,
        string output, string who)
    {
        if (Executable is not { } exe)
            throw new InvalidOperationException($"{who} needs the 'ffmpeg' program and none was found: {Install}.");
        string N(double x) => x.ToString("0.######", CultureInfo.InvariantCulture);
        var (w, h) = (background.Width, background.Height);
        var partial = $"{output}.{Guid.NewGuid():N}.part";

        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-protocol_whitelist", "pipe", "-f", "rawvideo", "-pix_fmt", "rgba", "-video_size", $"{w}x{h}",
            "-framerate", N(fps), "-i", "pipe:0" };
        foreach (var c in cells)
            args.AddRange(["-protocol_whitelist", "file", "-f", c.Format == VideoFormat.Mp4 ? "mov" : "matroska", "-i", "file:" + c.FullPath]);

        // The labels once, held; then each film fitted to its cell, held on its last frame and dimmed past its end, laid
        // on in turn. bt709 throughout, as Arguments has it.
        var graph = new StringBuilder($"[0:v]loop=loop=-1:size=1:start=0,fps={N(fps)},scale=out_color_matrix=bt709:out_range=tv,format=yuv420p[bg0]");
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            graph.Append($";[{i + 1}:v]fps={N(fps)},scale={c.Width}:{c.Height}:force_original_aspect_ratio=decrease:flags=lanczos")
                .Append(":in_color_matrix=bt709:in_range=tv:out_color_matrix=bt709:out_range=tv,")
                .Append($"pad={c.Width}:{c.Height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,format=yuv420p,tpad=stop_mode=clone:stop=-1");
            if (c.Seconds < seconds - 0.05)
                graph.Append($",lutyuv=y='16+(val-16)*0.55':u='128+(val-128)*0.6':v='128+(val-128)*0.6':enable='gte(t,{N(c.Seconds)})'");
            graph.Append($"[v{i}];[bg{i}][v{i}]overlay={c.X}:{c.Y}:format=yuv420[bg{i + 1}]");
        }
        if (sound is { } s) graph.Append($";[{s + 1}:a]aresample=48000,apad[a]");
        args.AddRange(["-filter_complex", graph.ToString(), "-map", $"[bg{cells.Count}]"]);
        if (sound is not null) args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "192k"]);

        var cap = (long)Math.Round(w * (double)h * fps * 80 / 500 / 1000);
        args.AddRange(["-c:v", "libx264", "-preset", "fast", "-tune", "animation", "-crf", "18",
            "-maxrate", $"{cap}k", "-bufsize", $"{cap * 2}k", "-pix_fmt", "yuv420p",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
            "-t", N(seconds), "-movflags", "+faststart", "-f", "mp4", "file:" + partial]);

        using var process = Process.Start(Info(exe, [.. args])) ?? throw new InvalidOperationException($"{who}: ffmpeg would not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        try
        {
            var stdin = process.StandardInput.BaseStream;
            try { stdin.Write(background.GetPixelSpan()); } catch (IOException) { /* ffmpeg stopped reading; its stderr says why */ }
            Close(stdin);
            if (!process.WaitForExit(FinishMs))
            {
                Kill(process);
                throw new TimeoutException($"{who}: ffmpeg did not finish within {FinishMs / 1000} s.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{who}: ffmpeg failed (exit {process.ExitCode}). {Trim(stderr.Result)}");
            MoveIntoPlace(partial, output, who);
            return new FileInfo(output).Length;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>The command line, every piece of it from typed values.</summary>
    static IEnumerable<string> Arguments(int w, int h, double fps, int frameCount, string output, VideoFormat format,
        int quality, IReadOnlyList<AudioTrack> tracks)
    {
        string N(double x) => x.ToString("0.######", CultureInfo.InvariantCulture);

        string[] head = ["-hide_banner", "-nostdin", "-loglevel", "error", "-y"];
        foreach (var a in head) yield return a;

        // The frames: raw RGBA, so nothing about them is guessed.
        string[] video = ["-protocol_whitelist", "pipe", "-f", "rawvideo", "-pix_fmt", "rgba",
            "-video_size", $"{w}x{h}", "-framerate", N(fps), "-i", "pipe:0"];
        foreach (var a in video) yield return a;

        // Each recording with the demuxer chosen for it, starting partway in when it begins before the film does.
        foreach (var t in tracks)
        {
            yield return "-protocol_whitelist"; yield return "file";
            if (t.Offset < 0) { yield return "-ss"; yield return N(-t.Offset); }
            yield return "-f"; yield return t.Demuxer;
            yield return "-i"; yield return "file:" + t.FullPath;
        }

        // bt709 throughout, matrix and tags alike: untagged, a browser guesses 601 for small frames and shifts the colours.
        var graph = new StringBuilder("[0:v]scale=out_color_matrix=bt709:out_range=tv,format=yuv420p[v]");
        if (tracks.Count > 0)
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                var delay = Math.Max(0, (long)Math.Round(tracks[i].Offset * 1000));
                graph.Append($";[{i + 1}:a]aresample=48000,adelay=delays={delay}:all=1[a{i}]");
            }
            graph.Append(';').Append(string.Concat(tracks.Select((_, i) => $"[a{i}]")));
            // normalize=0 sums the lines rather than turning each down by how many there are.
            if (tracks.Count > 1) graph.Append($"amix=inputs={tracks.Count}:duration=longest:dropout_transition=0:normalize=0,");
            graph.Append("apad[a]");
        }
        yield return "-filter_complex"; yield return graph.ToString();
        yield return "-map"; yield return "[v]";
        if (tracks.Count > 0) { yield return "-map"; yield return "[a]"; }

        // A ceiling under the CRF: quality/500 bits a pixel a frame, ~8 Mbps at 1080p24 and 80. Grain redrawn every frame
        // leaves nothing to predict, and unbounded CRF 18 spent 36.6 Mbps keeping it (becool2: 67 MB for 15 s). Capped,
        // ~85% of the grain's energy survives at under a quarter of the size; a film without it runs far below and is unchanged.
        var cap = (long)Math.Round(w * (double)h * fps * quality / 500 / 1000);
        string[] codec = format == VideoFormat.Mp4
            // CRF 18 at the default quality of 80: visually lossless, as WebP's 80 is meant to look.
            ? ["-c:v", "libx264", "-preset", "medium", "-tune", "animation", "-crf", N(Math.Round(51 - quality * 33 / 80d)),
                "-maxrate", $"{cap}k", "-bufsize", $"{cap * 2}k", "-pix_fmt", "yuv420p", "-movflags", "+faststart"]
            // libvpx reads a non-zero -b:v beside -crf as constrained quality: the same ceiling.
            : ["-c:v", "libvpx-vp9", "-crf", N(Math.Round(63 - quality * 0.4)), "-b:v", $"{cap}k", "-row-mt", "1", "-pix_fmt", "yuv420p"];
        foreach (var a in codec) yield return a;
        string[] tags = ["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];
        foreach (var a in tags) yield return a;

        if (tracks.Count > 0)
        {
            string[] audio = format == VideoFormat.Mp4 ? ["-c:a", "aac", "-b:a", "192k"] : ["-c:a", "libopus", "-b:a", "128k"];
            foreach (var a in audio) yield return a;
            // The film sets the length: apad fills a short recording with silence, and this cuts a long one.
            yield return "-t"; yield return N(frameCount / fps);
        }

        yield return "-f"; yield return format == VideoFormat.Mp4 ? "mp4" : "webm";
        yield return "file:" + output;
    }

    /// <summary>
    /// The demuxer a recording is read with: the one its extension names, and only when its first bytes agree. A
    /// probed format is how an <c>.avi</c> that is really a playlist gets ffmpeg to read files it names.
    /// </summary>
    internal static string Demuxer(string fullPath, string given, string who)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var head = new byte[12];
        int read;
        using (var f = File.OpenRead(fullPath)) read = f.Read(head, 0, head.Length);
        bool At(int offset, string magic) =>
            read >= offset + magic.Length && Encoding.ASCII.GetString(head, offset, magic.Length) == magic;

        var (demuxer, ok) = ext switch
        {
            ".wav" => ("wav", At(0, "RIFF") && At(8, "WAVE")),
            ".mp3" => ("mp3", At(0, "ID3") || read >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0),
            ".m4a" => ("mov", At(4, "ftyp")),
            ".ogg" or ".opus" => ("ogg", At(0, "OggS")),
            ".flac" => ("flac", At(0, "fLaC")),
            _ => throw new ArgumentException(
                $"{who}: '{given}' is not a recording it reads. It takes .wav, .mp3, .m4a, .ogg, .opus or .flac.")
        };
        return ok ? demuxer
            : throw new ArgumentException($"{who}: '{given}' is named {ext} but does not begin like one, so it was not read.");
    }

    /// <summary>A WAV's length in seconds from its header, or null for another format or a header it cannot read.</summary>
    internal static double? WavSeconds(string fullPath) =>
        Wav(fullPath) is { ByteRate: > 0 } f ? f.DataBytes / (double)f.ByteRate : null;

    /// <summary>Whether the aligner reads a file as it is: a WAV of 16-bit integer PCM, which is all Python's <c>wave</c> takes.</summary>
    internal static bool IsPcm16Wav(string fullPath) => Wav(fullPath) is { Tag: 1, Bits: 16 };

    /// <summary>
    /// A recording as the aligner reads it: the file itself when it is 16-bit PCM WAV, otherwise decoded once to a 16 kHz
    /// mono one, the aligner's own rate, kept in the temp folder under its content's hash so the same upload is decoded
    /// once however often it is named.
    /// </summary>
    /// <remarks>
    /// Read with the demuxer its extension and first bytes agree on, as a video's recordings are, so an upload is never
    /// probed. The video reads the same source the same way, so the times the aligner finds are the times it plays at:
    /// ffmpeg drops an MP3's encoder delay and honours an M4A's edit list in both.
    /// </remarks>
    internal static string AlignerWav(string fullPath, string given, string who)
    {
        if (IsPcm16Wav(fullPath)) return fullPath;
        var demuxer = Demuxer(fullPath, given, who);
        if (Executable is not { } exe)
            throw new InvalidOperationException(
                $"{who}: '{given}' is not 16-bit PCM WAV, and decoding it for the aligner needs the 'ffmpeg' program: {Install}.");

        string hash;
        using (var f = File.OpenRead(fullPath)) hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(f));
        var dir = Path.Combine(Path.GetTempPath(), "polson-audio");
        var wav = Path.Combine(dir, hash[..32] + ".wav");
        if (File.Exists(wav)) return wav;

        Directory.CreateDirectory(dir);
        var partial = wav + "." + Guid.NewGuid().ToString("N") + ".part";
        var info = Info(exe, "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-protocol_whitelist", "file", "-f", demuxer, "-i", "file:" + fullPath,
            "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", "file:" + partial);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{who}: ffmpeg would not start.");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            Kill(process);
            File.Delete(partial);
            throw new TimeoutException($"{who}: decoding '{given}' took longer than 120 s.");
        }
        if (process.ExitCode != 0 || !IsPcm16Wav(partial))
        {
            File.Delete(partial);
            throw new InvalidOperationException($"{who}: ffmpeg could not decode '{given}' (exit {process.ExitCode}). {Trim(stderr.Result)}");
        }
        // Another caller may have finished the same file first; theirs is the same bytes.
        try { File.Move(partial, wav, overwrite: false); }
        catch (IOException) when (File.Exists(wav)) { File.Delete(partial); }
        return wav;
    }

    /// <summary>
    /// Where speech begins and ends in a 16-bit PCM WAV, in seconds from its start: the first and last 10 ms with sound
    /// in them, a window counting as sound when its level reaches a twentieth of the loudest's. Null when there is none.
    /// </summary>
    /// <remarks>For a line <c>Motion.speech</c> cannot align — one in another language — this is its timing: the recording
    /// says where the line is, so silence a voice leaves before and after is not counted as speaking.</remarks>
    internal static (double Start, double End)? SpeechSpan(string pcm16Wav)
    {
        if (Wav(pcm16Wav) is not { Tag: 1, Bits: 16, ByteRate: > 0 } f || f.Channels < 1) return null;
        var rate = (int)(f.ByteRate / (2 * f.Channels));
        var window = Math.Max(1, rate / 100) * f.Channels;
        var levels = new List<double>();
        using (var r = new BinaryReader(File.OpenRead(pcm16Wav)))
        {
            r.BaseStream.Seek(f.DataStart, SeekOrigin.Begin);
            // A header can claim more than a cut-short file holds.
            var left = Math.Min(f.DataBytes, r.BaseStream.Length - f.DataStart) / 2;
            while (left > 0)
            {
                var n = (int)Math.Min(window, left);
                double sum = 0;
                for (var i = 0; i < n; i++) { double s = r.ReadInt16(); sum += s * s; }
                levels.Add(Math.Sqrt(sum / n));
                left -= n;
            }
        }
        if (levels.Count == 0 || levels.Max() < 1) return null;
        var floor = levels.Max() / 20;
        var first = levels.FindIndex(l => l >= floor);
        var last = levels.FindLastIndex(l => l >= floor);
        var seconds = (double)window / f.Channels / rate;
        return (first * seconds, (last + 1) * seconds);
    }

    /// <summary>A WAV's format tag, bits per sample, bytes per second, data length, channels and where its samples begin,
    /// or null when it is not a WAV it can read.</summary>
    static (int Tag, int Bits, uint ByteRate, long DataBytes, int Channels, long DataStart)? Wav(string fullPath)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(fullPath));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") return null;
            r.ReadUInt32();
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") return null;
            (int Tag, int Bits, uint ByteRate, int Channels)? fmt = null;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                var id = Encoding.ASCII.GetString(r.ReadBytes(4));
                var size = r.ReadUInt32();
                if (id == "fmt " && size >= 16)
                {
                    var tag = r.ReadUInt16();
                    var channels = r.ReadUInt16();
                    r.ReadUInt32();
                    var byteRate = r.ReadUInt32();
                    r.ReadUInt16();
                    fmt = (tag, r.ReadUInt16(), byteRate, channels);
                    r.BaseStream.Seek(size - 16 + (size & 1), SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    // A streamed WAV writes 0 or 0xFFFFFFFF here and means "to the end".
                    var bytes = size is 0 or uint.MaxValue ? r.BaseStream.Length - r.BaseStream.Position : size;
                    return fmt is { } f ? (f.Tag, f.Bits, f.ByteRate, bytes, f.Channels, r.BaseStream.Position) : null;
                }
                else r.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException) { }
        return null;
    }

    static ProcessStartInfo Info(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    /// <summary>Closes ffmpeg's input, which tells it the last frame is in. False when the pipe had already broken.</summary>
    static bool Close(Stream stdin)
    {
        try { stdin.Dispose(); return true; }
        catch (IOException) { return false; }
    }

    static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); process.WaitForExit(10_000); }
        catch (InvalidOperationException) { /* already gone */ }
    }

    /// <summary>
    /// Moves a finished <paramref name="partial"/> over <paramref name="output"/>, waiting a second for a reader that has it
    /// open for a moment. Windows will not replace a file another program holds open: a run page left on a film held
    /// <c>output.mp4</c> for minutes, and nightshift4 (2026-10-25) was refused "Access to the path is denied" five times
    /// with nothing saying why.
    /// </summary>
    internal static void MoveIntoPlace(string partial, string output, string who)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(partial, output, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && File.Exists(output) && File.Exists(partial))
            {
                if (attempt < 10) { System.Threading.Thread.Sleep(100); continue; }
                File.Delete(partial);
                throw new IOException($"{who}: '{Path.GetFileName(output)}' is open in another program — a page or player " +
                    "showing it — and Windows will not replace a file while it is open, so the new one was not kept. Close " +
                    "what is showing it and run this again, or write to another name.", ex);
            }
        }
    }

    static string Trim(string s) => s.Length <= 600 ? s.Trim() : string.Concat("…", s.AsSpan(s.Length - 600).Trim());

    static string? Discover()
    {
        if (!string.IsNullOrWhiteSpace(_override))
            return File.Exists(_override) ? _override : null;

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        // Walks up from the assembly, as the tracer does: a test runs from its own output folder and the CLI from
        // bin/cli, so neither can assume the repository root is the working directory.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "bin", "ffmpeg", name);
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry is not ours to report */ }
        }
        return null;
    }
    #endregion

    #region Types
    /// <summary>The container, from the output's extension: MP4 is H.264 and AAC, WebM is VP9 and Opus.</summary>
    internal enum VideoFormat { Mp4, WebM }

    /// <summary>A recording, where it was given, the demuxer to read it with, and when it starts relative to the first frame.</summary>
    internal sealed record AudioTrack(string Given, string FullPath, string Demuxer, double Offset);

    /// <summary>What was written: frames, the encoded size (padded to even), and bytes.</summary>
    internal sealed record Encoded(int Frames, int Width, int Height, long Bytes);

    /// <summary>A finished video as ffmpeg reads it: codec, size, frames a second, length in seconds, and whether it has sound.</summary>
    internal sealed record VideoInfo(string Codec, int Width, int Height, double Fps, double Seconds, bool HasAudio);

    /// <summary>One film in a <see cref="Grid"/>: where it is, how it is read, its length, and the cell it is fitted into.</summary>
    internal sealed record GridCell(string FullPath, VideoFormat Format, double Seconds, int X, int Y, int Width, int Height);
    #endregion
}
