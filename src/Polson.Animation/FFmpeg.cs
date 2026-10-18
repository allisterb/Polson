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
/// Video with sound, by way of <c>ffmpeg</c>: Skia frames in over a pipe, an MP4 or WebM out. Also decodes a recording
/// that is not 16-bit PCM WAV into one, for the aligner.
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
            File.Move(partial, output, overwrite: true);
            return new FileInfo(output).Length;
        }
        finally
        {
            File.Delete(list);
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

    /// <summary>A WAV's format tag, bits per sample, bytes per second and data length, or null when it is not a WAV it can read.</summary>
    static (int Tag, int Bits, uint ByteRate, long DataBytes)? Wav(string fullPath)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(fullPath));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") return null;
            r.ReadUInt32();
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") return null;
            (int Tag, int Bits, uint ByteRate)? fmt = null;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                var id = Encoding.ASCII.GetString(r.ReadBytes(4));
                var size = r.ReadUInt32();
                if (id == "fmt " && size >= 16)
                {
                    var tag = r.ReadUInt16();
                    r.ReadBytes(6);
                    var byteRate = r.ReadUInt32();
                    r.ReadUInt16();
                    fmt = (tag, r.ReadUInt16(), byteRate);
                    r.BaseStream.Seek(size - 16 + (size & 1), SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    // A streamed WAV writes 0 or 0xFFFFFFFF here and means "to the end".
                    var bytes = size is 0 or uint.MaxValue ? r.BaseStream.Length - r.BaseStream.Position : size;
                    return fmt is { } f ? (f.Tag, f.Bits, f.ByteRate, bytes) : null;
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

    /// <summary>A finished video as ffmpeg reads it: codec, size, frames a second, length in seconds, and whether it has sound.</summary>
    internal sealed record VideoInfo(string Codec, int Width, int Height, double Fps, double Seconds, bool HasAudio);
    #endregion
}
