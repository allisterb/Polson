namespace Polson.Tests.Animation;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Polson.Animation;

using SkiaSharp;

using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Synfig's own renderer as the check on ours: finds <c>synfig.exe</c> and renders a composition's <c>.sif</c> with it.
/// The older test classes each carry a private copy of this; new ones use it.
/// </summary>
internal static class SynfigOracle
{
    #region Methods
    /// <summary><c>reference/apps/synfig/bin/synfig.exe</c>, or <c>POLSON_SYNFIG</c>; null when neither is there, and the test skips.</summary>
    public static string? Find()
    {
        var path = Environment.GetEnvironmentVariable("POLSON_SYNFIG");
        if (string.IsNullOrEmpty(path))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "reference"))) dir = dir.Parent;
            path = dir is null ? null : Path.Combine(dir.FullName, "reference", "apps", "synfig", "bin", "synfig.exe");
        }

        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Every frame Synfig renders of <paramref name="comp"/>, in order.</summary>
    public static SKBitmap[] Render(string synfig, MotionComposition comp, string name, ITestOutputHelper output)
    {
        var dir = Path.Combine(Path.GetTempPath(), "polson-synfig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var sif = comp.SaveSif(Path.Combine(dir, name + ".sif"));
            var start = new ProcessStartInfo(synfig)
            {
                WorkingDirectory = Path.GetDirectoryName(synfig)!,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            foreach (var a in new[] { "-q", sif, "-t", "png", "-o", Path.Combine(dir, name + ".png") }) start.ArgumentList.Add(a);

            using var process = Process.Start(start)!;
            var err = process.StandardError.ReadToEndAsync();
            var outp = process.StandardOutput.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "synfig did not finish in two minutes");
            var log = string.Join("\n", (err.Result + outp.Result).Split('\n')
                .Where(l => !l.Contains("info:") && !l.Contains("Unable to find module") && l.Trim().Length > 0));
            if (log.Length > 0) output.WriteLine(log);
            Assert.True(process.ExitCode == 0, $"synfig exited {process.ExitCode}: {log}");
            Assert.DoesNotContain("error", log, StringComparison.OrdinalIgnoreCase);

            var files = Directory.GetFiles(dir, name + ".*.png").Order(StringComparer.Ordinal).ToArray();
            if (files.Length == 0) files = Directory.GetFiles(dir, name + ".png");
            // Through .NET, not SKBitmap.Decode(path): see MotionBlendTests.
            return [.. files.Select(f => SKBitmap.Decode(File.ReadAllBytes(f)))];
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Writes both frames to <c>POLSON_SYNFIG_DUMP</c> when it is set, to look at a failure.</summary>
    public static void Dump(SKBitmap ours, SKBitmap theirs, string name)
    {
        if (Environment.GetEnvironmentVariable("POLSON_SYNFIG_DUMP") is not { Length: > 0 } dump) return;
        Directory.CreateDirectory(dump);
        File.WriteAllBytes(Path.Combine(dump, $"ours-{name}.png"), ours.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        File.WriteAllBytes(Path.Combine(dump, $"theirs-{name}.png"), theirs.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }
    #endregion
}
