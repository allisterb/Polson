namespace Polson.Animation;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;

/// <summary>Video with sound, by way of <c>ffmpeg</c>: Skia frames in over a pipe, an MP4 or WebM out.</summary>
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
        var partial = output + ".part";
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

        File.Move(partial, output, overwrite: true);
        return new(count, w, h, new FileInfo(output).Length);
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

        string[] codec = format == VideoFormat.Mp4
            // CRF 18 at the default quality of 80: visually lossless, as WebP's 80 is meant to look.
            ? ["-c:v", "libx264", "-preset", "medium", "-tune", "animation", "-crf", N(Math.Round(51 - quality * 33 / 80d)),
                "-pix_fmt", "yuv420p", "-movflags", "+faststart"]
            : ["-c:v", "libvpx-vp9", "-crf", N(Math.Round(63 - quality * 0.4)), "-b:v", "0", "-row-mt", "1", "-pix_fmt", "yuv420p"];
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
    internal static double? WavSeconds(string fullPath)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(fullPath));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") return null;
            r.ReadUInt32();
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") return null;
            uint byteRate = 0;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                var id = Encoding.ASCII.GetString(r.ReadBytes(4));
                var size = r.ReadUInt32();
                if (id == "fmt " && size >= 16)
                {
                    r.ReadBytes(8);
                    byteRate = r.ReadUInt32();
                    r.BaseStream.Seek(size - 12, SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    // A streamed WAV writes 0 or 0xFFFFFFFF here and means "to the end".
                    var bytes = size is 0 or uint.MaxValue ? r.BaseStream.Length - r.BaseStream.Position : size;
                    return byteRate > 0 ? bytes / (double)byteRate : null;
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
    #endregion
}
