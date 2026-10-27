namespace Polson.Tests.Animation;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Polson.Animation;
using Polson.Drawing.Skia;
using Xunit;

using static MotionCompositionTests;

/// <summary>
/// The audiences' films side by side in one MP4 (<c>polson compare</c>). Read back with ffmpeg: each film in its cell, a
/// short one held on its last frame and dimmed, and the sound only where it was asked for. The encodes skip where there
/// is no ffmpeg; the refusals run everywhere.
/// </summary>
public class ComparisonTests : TestsRuntime, IDisposable
{
    #region Fields
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-compare-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public ComparisonTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A 48×32 film of one colour, <paramref name="frames"/> long at 12 fps.</summary>
    private string Film(string name, string colour, int frames)
    {
        using var motion = new MotionToolkit(root);
        for (var i = 0; i < frames; i++)
        {
            using var board = new SkiaCanvas(48, 32);
            var ctx = board.GetContext("2d");
            ctx.FillStyle = colour;
            ctx.FillRect(0, 0, 48, 32);
            motion.Frame(board);
        }
        motion.SaveVideo(name, Opts(("fps", 12d)));
        return Path.Combine(root, name);
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

    private static (byte R, byte G, byte B) Pixel(byte[] pixels, int frame, int w, int h, int x, int y)
    {
        var at = ((frame * h + y) * w + x) * 4;
        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    private static void Near((byte R, byte G, byte B) actual, int r, int g, int b) =>
        Assert.True(Math.Abs(actual.R - r) <= 14 && Math.Abs(actual.G - g) <= 14 && Math.Abs(actual.B - b) <= 14,
            $"expected about ({r}, {g}, {b}), got {actual}");
    #endregion

    #region Tests
    /// <summary>
    /// Two films sit side by side in cells of the first's shape, labelled below, as long as the longer; the shorter holds its
    /// last frame, dimmed, as the page's grid dims a film past its end.
    /// </summary>
    [Fact]
    public void TestFilmsSitSideBySideAndAShortOneHoldsDimmed()
    {
        if (!FFmpeg.Available) return;
        var red = Film("red.mp4", "#ff0000", 12);
        var blue = Film("blue.mp4", "#0000ff", 6);
        var output = Path.Combine(root, "out", "comparison.mp4");

        Comparison.Write([new(red, "Audience A · Dubai", "in Arabic"), new(blue, "Audience B · 東京", "")], output, width: 640);

        // 640 wide: 8 px gutters, two 308-px cells of the films' 3:2 (206 tall), a 22-px label band under them.
        var info = FFmpeg.Probe(output, "comparison.mp4", FFmpeg.VideoFormat.Mp4, "test");
        Assert.Equal((640, 244), (info.Width, info.Height));
        Assert.InRange(info.Seconds, 0.95, 1.05);
        Assert.False(info.HasAudio);

        var pixels = Decode(output, "-vf", "scale=in_color_matrix=bt709:in_range=tv,format=rgba", "-f", "rawvideo");
        Assert.Equal(12, pixels.Length / (640 * 244 * 4));
        Near(Pixel(pixels, 1, 640, 244, 162, 111), 255, 0, 0);
        Near(Pixel(pixels, 10, 640, 244, 162, 111), 255, 0, 0);
        Near(Pixel(pixels, 2, 640, 244, 478, 111), 0, 0, 255);
        var held = Pixel(pixels, 10, 640, 244, 478, 111);
        Assert.True(held.B is > 60 and < 200 && held.B > held.R + 40, $"the short film past its end should be dimmed blue, got {held}");
        Near(Pixel(pixels, 5, 640, 244, 3, 3), 0x12, 0x13, 0x17);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.part"));
    }

    /// <summary>The sound is the chosen film's, padded to the longest; a film with none cannot be chosen.</summary>
    [Fact]
    public void TestTheSoundIsTheChosenFilmsAlone()
    {
        if (!FFmpeg.Available) return;
        const int rate = 16000;
        var samples = Enumerable.Range(0, (int)(0.3 * rate)).Select(i => (short)(Math.Sin(2 * Math.PI * 440 * i / (double)rate) * 16000)).ToArray();
        using (var w = new BinaryWriter(File.Create(Path.Combine(root, "line.wav"))))
        {
            w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
            w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(samples.Length * 2);
            foreach (var s in samples) w.Write(s);
        }
        var comp = new MotionToolkit(root).Composition(Opts(("width", 48d), ("height", 32d), ("fps", 12d), ("duration", 0.5)));
        comp.Fill(Opts(("color", "#0000ff")));
        comp.SaveVideo("talk.mp4", Opts(("audio", Opts(("path", "line.wav"), ("start", 0.1)))));
        var silent = Film("red.mp4", "#ff0000", 12);
        var talk = Path.Combine(root, "talk.mp4");
        var output = Path.Combine(root, "comparison.mp4");

        Comparison.Write([new(silent, "A", ""), new(talk, "B", "")], output, width: 640, sound: 1);

        var bytes = Decode(output, "-map", "0:a", "-ac", "1", "-ar", "16000", "-f", "s16le");
        var heard = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, heard, 0, heard.Length * 2);
        Assert.InRange(Array.FindIndex(heard, s => Math.Abs((int)s) > 3200) / 16000d, 0.08, 0.14);
        Assert.InRange(heard.Length / 16000d, 0.95, 1.05);

        var refused = Assert.Throws<ArgumentException>(() => Comparison.Write([new(silent, "A", ""), new(talk, "B", "")], output, 640, sound: 0));
        Assert.Contains("has no sound", refused.Message);
    }

    /// <summary>What it will not do, said before anything is read or run.</summary>
    [Fact]
    public void TestItRefusesWhatItCannotSet()
    {
        var a = Path.Combine(root, "a.mp4");
        var b = Path.Combine(root, "b.mp4");
        var output = Path.Combine(root, "c.mp4");

        Assert.Contains("2 to 6", Assert.Throws<ArgumentException>(() => Comparison.Write([new(a, "A", "")], output)).Message);
        Assert.Contains("writes an .mp4", Assert.Throws<ArgumentException>(() =>
            Comparison.Write([new(a, "A", ""), new(b, "B", "")], Path.Combine(root, "c.webm"))).Message);
        Assert.Contains("no film 3", Assert.Throws<ArgumentException>(() =>
            Comparison.Write([new(a, "A", ""), new(b, "B", "")], output, sound: 2)).Message);
        Assert.Throws<FileNotFoundException>(() => Comparison.Write([new(a, "A", ""), new(b, "B", "")], output));
    }
    #endregion
}
