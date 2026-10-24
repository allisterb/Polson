namespace Polson.Tests.Animation;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Polson.Animation;
using Polson.Drawing.Skia;
using SkiaSharp;
using Xunit;

using static MotionCompositionTests;

/// <summary>
/// Video with sound through ffmpeg. Each file is decoded back with ffmpeg and read: a video that encodes and plays can
/// still have the wrong frames or the sound in the wrong place, and only reading it back says which. The tests that
/// encode skip where there is no ffmpeg; the refusals run everywhere.
/// </summary>
public class MotionVideoTests : TestsRuntime, IDisposable
{
    #region Fields
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-video-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public MotionVideoTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static SkiaCanvas Board(int w, int h, string colour)
    {
        var canvas = new SkiaCanvas(w, h);
        var ctx = canvas.GetContext("2d");
        ctx.FillStyle = colour;
        ctx.FillRect(0, 0, w, h);
        return canvas;
    }

    /// <summary>A mono 16 kHz WAV: silence, then a 440 Hz tone, then silence.</summary>
    private string Wav(string name, double silence, double tone, double after = 0)
    {
        const int rate = 16000;
        var samples = Enumerable.Range(0, (int)((silence + tone + after) * rate)).Select(i =>
        {
            var t = i / (double)rate;
            return (short)(t >= silence && t < silence + tone ? Math.Sin(2 * Math.PI * 440 * t) * 16000 : 0);
        }).ToArray();

        var path = Path.Combine(root, name);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write(s);
        return path;
    }

    /// <summary>Runs ffmpeg on a file the test wrote and returns what it put on stdout.</summary>
    private static byte[] Decode(string file, params string[] output)
    {
        var info = new ProcessStartInfo(FFmpeg.Executable!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", file }.Concat(output).Append("pipe:1"))
            info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(bytes);
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
        return bytes.ToArray();
    }

    /// <summary>Re-encodes a file the test wrote, as an upload might arrive: the codec by the output's extension.</summary>
    internal static string Transcode(string source, string output, params string[] codec)
    {
        var info = new ProcessStartInfo(FFmpeg.Executable!) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", source }.Concat(codec).Append(output))
            info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    /// <summary>Every frame as RGBA, read with the bt709 matrix it was written with.</summary>
    private static (byte[] Pixels, int Count) Frames(string file, int w, int h)
    {
        var pixels = Decode(file, "-map", "0:v", "-vf", "scale=in_color_matrix=bt709:in_range=tv,format=rgba", "-f", "rawvideo");
        return (pixels, pixels.Length / (w * h * 4));
    }

    /// <summary>The soundtrack as 16 kHz mono samples.</summary>
    private static short[] Samples(string file)
    {
        var bytes = Decode(file, "-map", "0:a", "-ac", "1", "-ar", "16000", "-f", "s16le");
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>When the sound first rises above a tenth of full scale, in seconds.</summary>
    private static double Onset(short[] samples) => Array.FindIndex(samples, s => Math.Abs((int)s) > 3200) / 16000d;

    private static (byte R, byte G, byte B) Pixel(byte[] pixels, int frame, int w, int h, int x, int y)
    {
        var at = ((frame * h + y) * w + x) * 4;
        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    private static void Near((byte R, byte G, byte B) actual, int r, int g, int b) =>
        Assert.True(Math.Abs(actual.R - r) <= 12 && Math.Abs(actual.G - g) <= 12 && Math.Abs(actual.B - b) <= 12,
            $"expected about ({r}, {g}, {b}), got {actual}");
    #endregion

    #region Tests
    /// <summary>Every held frame reaches the file, in order and in its colour.</summary>
    [Fact]
    public void TestHeldFramesAreEncodedInOrder()
    {
        if (!FFmpeg.Available) return;
        using var motion = new MotionToolkit(root);
        foreach (var colour in new[] { "#ff0000", "#ff0000", "#ff0000", "#0000ff", "#0000ff", "#0000ff" })
        {
            using var board = Board(48, 32, colour);
            motion.Frame(board);
        }

        var result = motion.SaveVideo("out/move.mp4", Opts(("fps", 12d)));

        Assert.Equal("mp4", result["format"]);
        Assert.Equal(6, result["frames"]);
        Assert.Equal(500d, (double)result["durationMs"]!, 6);
        var (pixels, count) = Frames(Path.Combine(root, "out", "move.mp4"), 48, 32);
        Assert.Equal(6, count);
        Near(Pixel(pixels, 0, 48, 32, 24, 16), 255, 0, 0);
        Near(Pixel(pixels, 5, 48, 32, 24, 16), 0, 0, 255);
        Assert.False(File.Exists(Path.Combine(root, "out", "move.mp4.part")));
    }

    /// <summary>Video has no alpha: a transparent frame shows the background, and an odd size is padded, saying so.</summary>
    [Fact]
    public void TestTransparencyShowsTheBackgroundAndOddSizesArePadded()
    {
        if (!FFmpeg.Available) return;
        using var motion = new MotionToolkit(root);
        using var clear = new SkiaCanvas(41, 31);
        motion.Frame(clear);
        motion.Frame(clear);

        var result = motion.SaveVideo("clear.webm", Opts(("background", "#00ff00")));

        Assert.Equal("webm", result["format"]);
        Assert.Equal(42, result["width"]);
        Assert.Equal(32, result["height"]);
        Assert.Contains((List<object?>)result["warnings"]!, w => w is string s && s.Contains("42x32"));
        var (pixels, count) = Frames(Path.Combine(root, "clear.webm"), 42, 32);
        Assert.Equal(2, count);
        Near(Pixel(pixels, 0, 42, 32, 20, 15), 0, 255, 0);
        Near(Pixel(pixels, 0, 42, 32, 41, 31), 0, 255, 0);
    }

    /// <summary>
    /// A recording lands where it was placed. The tone starts 0.5 s into the file and the file at 0.6 s into the film,
    /// so it is heard at 1.1 s; the film is two seconds and the soundtrack is padded to match.
    /// </summary>
    [Fact]
    public void TestARecordingIsHeardWhereItWasPlaced()
    {
        if (!FFmpeg.Available) return;
        Wav("line.wav", 0.5, 0.5);
        var comp = new MotionToolkit(root).Composition(Opts(("width", 32d), ("height", 32d), ("fps", 25d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#ffffff")));

        var result = comp.SaveVideo("talk.mp4", Opts(("audio", Opts(("path", "line.wav"), ("start", 0.6))), ("to", 1.96)));

        Assert.Equal(50, result["frames"]);
        var samples = Samples(Path.Combine(root, "talk.mp4"));
        Assert.InRange(Onset(samples), 1.08, 1.13);
        Assert.InRange(samples.Length / 16000d, 1.97, 2.03);
        Assert.Empty((List<object?>)result["warnings"]!);
    }

    /// <summary>
    /// Past the script's limit an encode stops and keeps nothing: no film under its name, no partial beside it. Jint stops a
    /// script only between statements, and uberpitch1's timed-out part was written anyway, under the name it reused.
    /// </summary>
    [Fact]
    public void TestAnEncodePastTheScriptsLimitKeepsNothing()
    {
        if (!FFmpeg.Available) return;
        var comp = new MotionToolkit(root).Composition(Opts(("width", 32d), ("height", 32d), ("fps", 25d), ("duration", 1d)));
        comp.Fill(Opts(("color", "#ffffff")));

        using (ScriptDeadline.Begin(TimeSpan.Zero))
            Assert.Throws<TimeoutException>(() => comp.SaveVideo("late.mp4"));

        Assert.False(File.Exists(Path.Combine(root, "late.mp4")));
        Assert.Empty(Directory.GetFiles(root, "*.part"));
        comp.SaveVideo("late.mp4");                                          // outside a script, nothing stops it
        Assert.True(File.Exists(Path.Combine(root, "late.mp4")));
    }

    /// <summary>A sheet of a composition renders only the times asked for, each in a labelled cell 480 wide by default.</summary>
    [Fact]
    public void TestASheetOfACompositionShowsTheTimesNamed()
    {
        var comp = Ramp(new MotionToolkit(root), 960);

        var sheet = comp.Sheet("artifacts/sheet.png", Opts(("times", new object[] { 0d, 1d, 1.96 }), ("cols", 3d)));

        Assert.Equal((3, 3, 1), ((int)sheet["cells"]!, (int)sheet["cols"]!, (int)sheet["rows"]!));
        using var image = SKBitmap.Decode(File.ReadAllBytes(Path.Combine(root, "artifacts", "sheet.png")));
        Assert.Equal((int)sheet["width"]!, image.Width);
        Assert.InRange(image.Width, 3 * 480, 3 * 480 + 80);
        // The ramp runs red to blue: the first cell is red and the last blue, each drawn whole in its own cell.
        var top = 12 + 10 + 120;
        Assert.True(image.GetPixel(12 + 10 + 240, top).Red > 200);
        Assert.True(image.GetPixel(image.Width - 12 - 10 - 240, top).Blue > 200);
        Assert.Throws<ArgumentException>(() => comp.Sheet("artifacts/bad.png", Opts(("times", new object[] { 5d }))));
    }

    /// <summary><c>from</c> moves the sound with the picture: starts are composition times.</summary>
    [Fact]
    public void TestFromMovesTheSoundWithThePicture()
    {
        if (!FFmpeg.Available) return;
        Wav("line.wav", 0.5, 0.5);
        var comp = new MotionToolkit(root).Composition(Opts(("width", 32d), ("height", 32d), ("fps", 25d), ("duration", 3d)));
        comp.Fill(Opts(("color", "#ffffff")));

        var result = comp.SaveVideo("late.mp4", Opts(("audio", Opts(("path", "line.wav"), ("start", 0.6))), ("from", 1d), ("to", 2.96)));

        Assert.Equal(1d, result["start"]);
        Assert.InRange(Onset(Samples(Path.Combine(root, "late.mp4"))), 0.08, 0.13);
    }

    /// <summary>A two-second film whose colour runs red to blue, so every frame is its own.</summary>
    private MotionComposition Ramp(MotionToolkit motion, int size = 32)
    {
        var comp = motion.Composition(Opts(("width", (double)size), ("height", (double)size), ("fps", 25d), ("duration", 2d)));
        comp.Fill(Opts(("color", motion.Nodes.Animated("color", new object[]
            { Opts(("time", 0d), ("value", "#ff0000")), Opts(("time", 2d), ("value", "#0000ff")) }))));
        return comp;
    }

    /// <summary>
    /// A film encoded a span at a time and joined is the film encoded whole: the same frames, in order, at the same length.
    /// This is how a film too long to encode inside one script's time limit is made.
    /// </summary>
    [Fact]
    public void TestPartsJoinIntoTheWholeFilm()
    {
        if (!FFmpeg.Available) return;
        var motion = new MotionToolkit(root);
        var comp = Ramp(motion);
        comp.SaveVideo("whole.mp4", Opts(("to", 1.96)));
        comp.SaveVideo("part1.mp4", Opts(("to", 0.96)));
        comp.SaveVideo("part2.mp4", Opts(("from", 1d), ("to", 1.96)));

        var result = motion.JoinVideos(new List<object?> { "part1.mp4", "part2.mp4" }, "joined.mp4");

        Assert.Equal(2, result["parts"]);
        Assert.Equal(50, result["frames"]);
        Assert.Empty((List<object?>)result["warnings"]!);
        Assert.False(File.Exists(Path.Combine(root, "joined.mp4.parts.txt")), "the list ffmpeg read was left behind");
        var (whole, n) = Frames(Path.Combine(root, "whole.mp4"), 32, 32);
        var (joined, m) = Frames(Path.Combine(root, "joined.mp4"), 32, 32);
        Assert.Equal(50, n);
        Assert.Equal(n, m);
        for (var f = 0; f < n; f++)
        {
            var (a, b) = (Pixel(whole, f, 32, 32, 16, 16), Pixel(joined, f, 32, 32, 16, 16));
            Near(b, a.R, a.G, a.B);
        }
    }

    /// <summary>
    /// Sound given at the join lands on the whole film from its <c>start</c>, across the seam; parts with sound of their own
    /// have it replaced, and the result says so.
    /// </summary>
    [Fact]
    public void TestSoundGivenAtTheJoinPlaysOverTheWholeFilm()
    {
        if (!FFmpeg.Available) return;
        Wav("line.wav", 0.5, 0.8);
        var motion = new MotionToolkit(root);
        var comp = Ramp(motion);
        comp.SaveVideo("a.mp4", Opts(("to", 0.96)));
        comp.SaveVideo("b.mp4", Opts(("from", 1d), ("to", 1.96)));

        var result = motion.JoinVideos(new List<object?> { "a.mp4", "b.mp4" }, "talk.mp4", Opts(("audio", Opts(("path", "line.wav"), ("start", 0.6)))));

        var samples = Samples(Path.Combine(root, "talk.mp4"));
        Assert.InRange(Onset(samples), 1.08, 1.13);                     // the tone crosses the seam at 1 s unbroken
        Assert.InRange(samples.Length / 16000d, 1.97, 2.03);
        Assert.Empty((List<object?>)result["warnings"]!);

        comp.SaveVideo("c.mp4", Opts(("to", 0.96), ("audio", "line.wav")));
        comp.SaveVideo("d.mp4", Opts(("from", 1d), ("to", 1.96), ("audio", Opts(("path", "line.wav"), ("start", 1d)))));
        var replaced = motion.JoinVideos(new List<object?> { "c.mp4", "d.mp4" }, "again.mp4", Opts(("audio", "line.wav")));
        Assert.Contains((List<object?>)replaced["warnings"]!, w => ((string)w!).Contains("replaced"));
    }

    /// <summary>What cannot be joined as it is, is refused by name: parts that differ, some with sound and some without, a
    /// file that is not what its name says, the output among the parts, and something that is not a list.</summary>
    [Fact]
    public void TestPartsThatCannotJoinAreRefusedByName()
    {
        if (!FFmpeg.Available) return;
        Wav("line.wav", 0.1, 0.2);
        var motion = new MotionToolkit(root);
        Ramp(motion).SaveVideo("small.mp4", Opts(("to", 0.96)));
        Ramp(motion, 48).SaveVideo("large.mp4", Opts(("to", 0.96)));
        Ramp(motion).SaveVideo("loud.mp4", Opts(("to", 0.96), ("audio", "line.wav")));
        File.WriteAllText(Path.Combine(root, "fake.mp4"), "not a video at all");

        var size = Assert.Throws<ArgumentException>(() => motion.JoinVideos(new List<object?> { "small.mp4", "large.mp4" }, "x.mp4"));
        Assert.Contains("'large.mp4' is h264 48x48", size.Message);
        var sound = Assert.Throws<ArgumentException>(() => motion.JoinVideos(new List<object?> { "small.mp4", "loud.mp4" }, "x.mp4"));
        Assert.Contains("some parts have sound", sound.Message);
        var fake = Assert.Throws<ArgumentException>(() => motion.JoinVideos(new List<object?> { "small.mp4", "fake.mp4" }, "x.mp4"));
        Assert.Contains("does not begin like", fake.Message);
        var self = Assert.Throws<ArgumentException>(() => motion.JoinVideos(new List<object?> { "small.mp4" }, "small.mp4"));
        Assert.Contains("a new path", self.Message);
        Assert.Throws<ArgumentException>(() => motion.JoinVideos("small.mp4", "x.mp4"));
        Assert.ThrowsAny<Exception>(() => motion.JoinVideos(new List<object?> { "../outside.mp4" }, "x.mp4"));
        Assert.False(File.Exists(Path.Combine(root, "x.mp4")));
    }

    /// <summary>Every frame of an animated WebP, decoded in order onto the one before as a viewer shows it.</summary>
    private static List<SKBitmap> WebpFrames(string file)
    {
        using var codec = SKCodec.Create(new MemoryStream(File.ReadAllBytes(file)))!;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var shown = new List<SKBitmap>();
        using var canvas = new SKBitmap(info);
        for (var i = 0; i < codec.FrameCount; i++)
        {
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, canvas.GetPixels(), new SKCodecOptions(i, i - 1)));
            shown.Add(canvas.Copy());
        }
        return shown;
    }

    /// <summary>The loop count an animated WebP's <c>ANIM</c> chunk holds: 0 is forever.</summary>
    private static int WebpLoops(string file)
    {
        var bytes = File.ReadAllBytes(file);
        for (var at = 12; at + 8 <= bytes.Length; at += 8 + BitConverter.ToInt32(bytes, at + 4) + (BitConverter.ToInt32(bytes, at + 4) & 1))
            if (bytes[at] == 'A' && bytes[at + 1] == 'N' && bytes[at + 2] == 'I' && bytes[at + 3] == 'M') return bytes[at + 12] | bytes[at + 13] << 8;
        throw new InvalidDataException($"{file} has no ANIM chunk.");
    }

    private static void Near(SKColor actual, int r, int g, int b) => Near((actual.Red, actual.Green, actual.Blue), r, g, b);

    /// <summary>
    /// A finished video becomes a looping WebP of the same film: as long, in its colours from first frame to last, looping
    /// forever, and holding fewer frames when some do not change.
    /// </summary>
    [Fact]
    public void TestAVideoBecomesALoopingWebp()
    {
        if (!FFmpeg.Available) return;
        var motion = new MotionToolkit(root);
        var comp = motion.Composition(Opts(("width", 48d), ("height", 32d), ("fps", 25d), ("duration", 2d)));
        comp.Fill(Opts(("color", motion.Nodes.Animated("color", new object[]
            { Opts(("time", 0d), ("value", "#e63946")), Opts(("time", 1d), ("value", "#1d3557")), Opts(("time", 2d), ("value", "#1d3557")) }))));
        comp.SaveVideo("film.mp4", Opts(("to", 1.96)));

        var result = motion.VideoToWebp("film.mp4", "out/film.webp");

        var file = Path.Combine(root, "out", "film.webp");
        Assert.Equal("webp", result["format"]);
        Assert.Equal(50, result["frames"]);
        Assert.Equal(48, result["width"]);
        Assert.Equal(32, result["height"]);
        Assert.Equal(true, result["loop"]);
        Assert.Equal(0, WebpLoops(file));
        Assert.InRange((double)result["durationMs"]!, 1960, 2040);
        Assert.InRange((int)result["storedFrames"]!, 20, 30);              // the blue second holds still
        Assert.Equal(new FileInfo(file).Length, result["bytes"]);
        Assert.Empty((List<object?>)result["warnings"]!);
        Assert.False(File.Exists(file + ".part"));

        var frames = WebpFrames(file);
        try
        {
            Near(frames[0].GetPixel(24, 16), 0xe6, 0x39, 0x46);           // through bt709 video and bt601 WebP, unshifted
            Near(frames[^1].GetPixel(24, 16), 0x1d, 0x35, 0x57);
        }
        finally { frames.ForEach(f => f.Dispose()); }
    }

    /// <summary>
    /// A wide video is made 960 wide unless told, the height keeping the aspect, and a narrower width is honoured. A video
    /// that never changes is a still image, and says so.
    /// </summary>
    [Fact]
    public void TestAWebpIsAtMost960WideUnlessTold()
    {
        if (!FFmpeg.Available) return;
        var motion = new MotionToolkit(root);
        var comp = motion.Composition(Opts(("width", 1600d), ("height", 100d), ("fps", 25d), ("duration", 0.08)));
        comp.Fill(Opts(("color", "#336699")));
        comp.SaveVideo("wide.mp4", Opts(("to", 0.04)));

        var wide = motion.VideoToWebp("wide.mp4", "wide.webp");
        Assert.Equal(960, wide["width"]);
        Assert.Equal(60, wide["height"]);
        Assert.Contains((List<object?>)wide["warnings"]!, w => ((string)w!).Contains("still image"));

        var narrow = motion.VideoToWebp("wide.mp4", "narrow.webp", Opts(("width", 400d)));
        Assert.Equal(400, narrow["width"]);
        Assert.Equal(25, narrow["height"]);
    }

    /// <summary><c>loop</c> is read as <c>Motion.save</c> reads it: false plays once and holds, a number plays that many times.</summary>
    [Fact]
    public void TestAWebpLoopsAsTold()
    {
        if (!FFmpeg.Available) return;
        var motion = new MotionToolkit(root);
        Ramp(motion).SaveVideo("ramp.mp4", Opts(("to", 0.96)));

        Assert.Equal(false, motion.VideoToWebp("ramp.mp4", "once.webp", Opts(("loop", false)))["loop"]);
        Assert.Equal(1, WebpLoops(Path.Combine(root, "once.webp")));
        Assert.Equal(3, motion.VideoToWebp("ramp.mp4", "three.webp", Opts(("loop", 3d)))["loop"]);
        Assert.Equal(3, WebpLoops(Path.Combine(root, "three.webp")));
        Assert.Contains("Motion.videoToWebp: loop", Assert.Throws<ArgumentException>(() => motion.VideoToWebp("ramp.mp4", "x.webp", Opts(("loop", 0.5)))).Message);
    }

    /// <summary>A WebP is silent, and a video with sound says it was left out.</summary>
    [Fact]
    public void TestAVideosSoundIsLeftOutAndSaysSo()
    {
        if (!FFmpeg.Available) return;
        Wav("line.wav", 0.1, 0.2);
        var motion = new MotionToolkit(root);
        Ramp(motion).SaveVideo("loud.mp4", Opts(("to", 0.96), ("audio", "line.wav")));

        var result = motion.VideoToWebp("loud.mp4", "quiet.webp");

        Assert.Contains((List<object?>)result["warnings"]!, w => ((string)w!).Contains("silent"));
    }

    /// <summary>
    /// What cannot be made a WebP is refused by name: wider than the video, a WebP that is not a .webp, a source that is
    /// not a video or not what its name says, a missing one, one outside the project, and an option it does not take.
    /// </summary>
    [Fact]
    public void TestWhatCannotBeAWebpIsRefusedByName()
    {
        if (!FFmpeg.Available) return;
        var motion = new MotionToolkit(root);
        Ramp(motion).SaveVideo("small.mp4", Opts(("to", 0.96)));
        File.WriteAllText(Path.Combine(root, "fake.mp4"), "not a video at all");

        Assert.Contains("the video's own 32", Assert.Throws<ArgumentException>(() => motion.VideoToWebp("small.mp4", "x.webp", Opts(("width", 64d)))).Message);
        Assert.Contains("an animated .webp", Assert.Throws<ArgumentException>(() => motion.VideoToWebp("small.mp4", "x.gif")).Message);
        Assert.Contains("a finished .mp4 or .webm", Assert.Throws<ArgumentException>(() => motion.VideoToWebp("small.gif", "x.webp")).Message);
        Assert.Contains("does not begin like", Assert.Throws<ArgumentException>(() => motion.VideoToWebp("fake.mp4", "x.webp")).Message);
        Assert.Contains("no video at", Assert.Throws<ArgumentException>(() => motion.VideoToWebp("gone.mp4", "x.webp")).Message);
        Assert.Contains("quality", Assert.Throws<ArgumentException>(() => motion.VideoToWebp("small.mp4", "x.webp", Opts(("quality", 0d)))).Message);
        Assert.Contains("has no 'fps'", Assert.Throws<ArgumentException>(() => motion.VideoToWebp("small.mp4", "x.webp", Opts(("fps", 12d)))).Message);
        Assert.ThrowsAny<Exception>(() => motion.VideoToWebp("../outside.mp4", "x.webp"));
        Assert.False(File.Exists(Path.Combine(root, "x.webp")));
    }

    /// <summary>Frames captured from a composition carry their rate and start, so the sound needs neither repeated.</summary>
    [Fact]
    public void TestCapturedFramesSayTheirOwnRateAndStart()
    {
        if (!FFmpeg.Available) return;
        Wav("line.wav", 0.5, 0.5);
        using var motion = new MotionToolkit(root);
        var comp = motion.Composition(Opts(("width", 32d), ("height", 32d), ("fps", 12d), ("duration", 3d)));
        comp.Fill(Opts(("color", "#ffffff")));
        comp.Capture(Opts(("from", 1d), ("to", 2.5)));

        var result = motion.SaveVideo("captured.mp4", Opts(("audio", Opts(("path", "line.wav"), ("start", 1.2)))));

        Assert.Equal(12d, result["fps"]);
        Assert.Equal(1d, result["start"]);
        Assert.InRange(Onset(Samples(Path.Combine(root, "captured.mp4"))), 0.68, 0.73);
    }

    /// <summary>Two lines are mixed, each at its own place, neither turned down for the other.</summary>
    [Fact]
    public void TestSeveralRecordingsAreMixed()
    {
        if (!FFmpeg.Available) return;
        Wav("a.wav", 0, 0.3);
        Wav("b.wav", 0, 0.3);
        var comp = new MotionToolkit(root).Composition(Opts(("width", 32d), ("height", 32d), ("fps", 25d), ("duration", 2d)));
        comp.Fill(Opts(("color", "#ffffff")));

        comp.SaveVideo("two.mp4", Opts(("audio", new List<object?> { "a.wav", Opts(("path", "b.wav"), ("start", 1d)) })));

        var samples = Samples(Path.Combine(root, "two.mp4"));
        double Loudest(double from, double to) => samples[(int)(from * 16000)..(int)(to * 16000)].Max(s => Math.Abs((int)s));
        Assert.InRange(Onset(samples), 0, 0.03);
        Assert.True(Loudest(0.5, 0.95) < 500, "silence between the lines");
        Assert.True(Loudest(1.05, 1.25) > 12000, "the second line at full level");
    }

    /// <summary>A recording longer than the film is cut at its end, and the result says by how much.</summary>
    [Fact]
    public void TestARecordingPastTheEndIsCutAndSaysSo()
    {
        if (!FFmpeg.Available) return;
        Wav("long.wav", 0, 2);
        var comp = new MotionToolkit(root).Composition(Opts(("width", 32d), ("height", 32d), ("fps", 10d), ("duration", 0.9)));
        comp.Fill(Opts(("color", "#ffffff")));

        var result = comp.SaveVideo("short.mp4", Opts(("audio", "long.wav")));

        var track = (Dictionary<string, object?>)((List<object?>)result["audio"]!)[0]!;
        Assert.Equal(2d, (double)track["duration"]!, 3);
        Assert.Equal(1d, (double)track["cut"]!, 3);
        Assert.Contains((List<object?>)result["warnings"]!, w => w is string s && s.Contains("past the end"));
        Assert.InRange(Samples(Path.Combine(root, "short.mp4")).Length / 16000d, 0.97, 1.03);
    }

    /// <summary>A frame that fails mid-film leaves no file behind, finished or partial.</summary>
    [Fact]
    public void TestAFailedRenderLeavesNoFile()
    {
        if (!FFmpeg.Available) return;
        var comp = new MotionToolkit(root).Composition(Opts(("width", 32d), ("height", 32d), ("fps", 10d), ("duration", 1d)));
        comp.Drawn((ctx, v, t) => { if (t > 0.5) throw new InvalidOperationException("the drawing failed"); });

        Assert.ThrowsAny<Exception>(() => comp.SaveVideo("broken.mp4"));

        Assert.Empty(Directory.GetFiles(root, "broken.*"));
    }

    /// <summary>
    /// A recording is read with the demuxer its extension names, and only when its first bytes agree. A playlist named
    /// <c>.wav</c> is the shape of the attack where ffmpeg, left to probe, reads files the playlist names.
    /// </summary>
    [Fact]
    public void TestARecordingThatIsNotWhatItsNameSaysIsRefused()
    {
        File.WriteAllText(Path.Combine(root, "line.wav"), "#EXTM3U\n#EXTINF:1,\nfile:///etc/passwd\n");
        File.WriteAllText(Path.Combine(root, "line.avi"), "RIFF");
        var comp = new MotionToolkit(root).Composition(Opts(("width", 32d), ("height", 32d), ("duration", 1d)));

        var named = Assert.Throws<ArgumentException>(() => comp.SaveVideo("x.mp4", Opts(("audio", "line.wav"))));
        Assert.Contains("does not begin like one", named.Message);
        var kind = Assert.Throws<ArgumentException>(() => comp.SaveVideo("x.mp4", Opts(("audio", "line.avi"))));
        Assert.Contains(".wav, .mp3", kind.Message);
        Assert.Empty(Directory.GetFiles(root, "x.*"));
    }

    /// <summary>The container is the extension's, and an extension it cannot write is refused, pointing at the one that can.</summary>
    [Fact]
    public void TestAnUnknownContainerIsRefused()
    {
        var comp = new MotionToolkit(root).Composition(Opts(("width", 32d), ("height", 32d), ("duration", 1d)));
        var error = Assert.Throws<ArgumentException>(() => comp.SaveVideo("x.gif"));
        Assert.Contains("Motion.save", error.Message);
    }

    /// <summary>A line timed by the rules has no recording, and handing it over as audio says so.</summary>
    [Fact]
    public void TestALineWithoutARecordingIsRefusedAsAudio()
    {
        if (!CmuDictionary.Available) return;
        var motion = new MotionToolkit(root);
        var comp = motion.Composition(Opts(("width", 32d), ("height", 32d), ("duration", 1d)));
        var line = motion.Speech("hello there");

        var error = Assert.Throws<ArgumentException>(() => comp.SaveVideo("x.mp4", Opts(("audio", line))));
        Assert.Contains("no recording", error.Message);
    }

    /// <summary>
    /// An upload the aligner cannot read is decoded to the 16 kHz mono 16-bit WAV it can, once: the tone stays where it
    /// was, the length stays the length, and naming the same file again finds the decoded copy. A 16-bit PCM WAV is used
    /// as it is.
    /// </summary>
    [Fact]
    public void TestAnUploadIsDecodedToTheWavTheAlignerReads()
    {
        if (!FFmpeg.Available) return;
        var wav = Wav("tone.wav", 0.5, 0.5, 0.5);
        Assert.Equal(wav, FFmpeg.AlignerWav(wav, "tone.wav", "test"));

        foreach (var (name, codec) in new[] { ("tone.mp3", new[] { "-c:a", "libmp3lame" }), ("tone.m4a", new[] { "-c:a", "aac" }),
                     ("float.wav", new[] { "-c:a", "pcm_f32le" }), ("tone.flac", new[] { "-c:a", "flac" }) })
        {
            var upload = Transcode(wav, Path.Combine(root, name), codec);
            Assert.False(FFmpeg.IsPcm16Wav(upload));
            var decoded = FFmpeg.AlignerWav(upload, name, "test");
            try
            {
                Assert.NotEqual(upload, decoded);
                Assert.True(FFmpeg.IsPcm16Wav(decoded), name);
                Assert.InRange(FFmpeg.WavSeconds(decoded)!.Value, 1.49, 1.53);
                Assert.InRange(Onset(Samples(decoded)), 0.49, 0.52);
                Assert.Equal(decoded, FFmpeg.AlignerWav(upload, name, "test"));
            }
            finally { File.Delete(decoded); }
        }
    }

    /// <summary>An upload is decoded only when it is what its name says, as a video's recordings are.</summary>
    [Fact]
    public void TestAnUploadThatIsNotWhatItsNameSaysIsNotDecoded()
    {
        var fake = Path.Combine(root, "line.mp3");
        File.WriteAllText(fake, "#EXTM3U\n#EXTINF:1,\nfile:///etc/passwd\n");
        Assert.Contains("does not begin like one", Assert.Throws<ArgumentException>(() => FFmpeg.AlignerWav(fake, "line.mp3", "test")).Message);
    }

    /// <summary>Without ffmpeg the call refuses, saying how to get it, rather than writing nothing quietly.</summary>
    [Fact]
    public void TestWithoutFfmpegTheCallSaysHowToGetIt()
    {
        var was = FFmpeg.ExecutableOverride;
        try
        {
            FFmpeg.ExecutableOverride = Path.Combine(root, "no-such-ffmpeg.exe");
            Assert.False(FFmpeg.Available);
            using var motion = new MotionToolkit(root);
            using var board = Board(8, 8, "#ff0000");
            motion.Frame(board);

            var error = Assert.Throws<InvalidOperationException>(() => motion.SaveVideo("x.mp4"));
            Assert.Contains("bootstrap.py --only ffmpeg", error.Message);
            File.WriteAllText(Path.Combine(root, "v.mp4"), "");
            Assert.Contains("bootstrap.py --only ffmpeg", Assert.Throws<InvalidOperationException>(() => motion.VideoToWebp("v.mp4", "v.webp")).Message);
        }
        finally { FFmpeg.ExecutableOverride = was; }
    }

    /// <summary>
    /// An end written to three places counts as the frame it names. productad1 ended each 5 s part at 4.958 — frame 119 of
    /// a 24 fps film is at 4.9583 s — and got 119 frames: 4.958 × 24 = 118.99 was floored. A <c>.98</c> end must still
    /// stop short of the next part's first frame, or the join repeats it.
    /// </summary>
    [Theory]
    [InlineData(4.958, 120)]
    [InlineData(4.98, 120)]
    [InlineData(5.0, 121)]
    [InlineData(4.9, 118)]
    public void TestAnEndAThousandthShortOfAFrameStillIncludesIt(double to, int frames)
    {
        var motion = new MotionToolkit(root);
        var comp = motion.Composition(Opts(("width", 16d), ("height", 16d), ("fps", 24d), ("duration", 6d)));
        Assert.Equal(frames, comp.Capture(Opts(("to", to))));
        if (!FFmpeg.Available) return;
        Assert.Equal(frames, comp.SaveVideo("span.mp4", Opts(("to", to)))["frames"]);
    }
    #endregion
}
