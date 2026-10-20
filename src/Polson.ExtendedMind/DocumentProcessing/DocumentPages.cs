namespace Polson.ExtendedMind.DocumentProcessing;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A PDF as text, one string per page: its own text layer where it has one, OCR where a page is a scan.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why text and not page images.</b> Token Factory takes at most 8 images a request, so a 93-page 10-K cannot be
/// sent as pictures; as text it is about 85k tokens, well inside Nemotron's context. On that 10-K, ten questions
/// read from the text layer scored 10/10; the same questions from eight page images at 100 dpi scored 9/10, with
/// 102,718 read as 102,714. Tesseract at 300 dpi read every one of the income statement's 48 figures.
/// </para>
/// <para>
/// <c>mutool</c> (MuPDF) and <c>tesseract</c> are run as processes: found in <c>bin/</c> on Windows, on the PATH in the
/// container (Debian <c>mupdf-tools</c>, <c>tesseract-ocr</c>), or named by <c>Tools:Mutool</c> / <c>Tools:Tesseract</c>.
/// </para>
/// <para>
/// <b>OCR is slow, about two seconds a page, and a whole script has 60 s under ADK.</b> So at most
/// <c>maxOcrPages</c> are read here; a document with more scanned pages is refused with
/// <see cref="TooManyScannedPages"/>, and the caller sends it whole to a model that reads scans itself.
/// </para>
/// </remarks>
public class DocumentPages : Runtime
{
    #region Constructors
    /// <param name="directory">Where each document's pages are kept, by content hash.</param>
    /// <param name="mutool">Path to <c>mutool</c>; found if null.</param>
    /// <param name="tesseract">Path to <c>tesseract</c>; found if null. Without it, a scanned page is refused.</param>
    public DocumentPages(string directory, string? mutool = null, string? tesseract = null)
    {
        this.directory = directory;
        Mutool = mutool ?? Find("mutool", "");
        Tesseract = tesseract ?? Find("tesseract", "tesseract");
    }
    #endregion

    #region Properties
    /// <summary>Fewer visible characters than this and a page is taken to be a scan.</summary>
    public const int ScannedBelowCharacters = 16;

    public string? Mutool { get; }

    public string? Tesseract { get; }

    /// <summary>Whether a PDF can be read as text at all.</summary>
    public bool IsAvailable => Mutool is not null;
    #endregion

    #region Methods
    /// <summary>The document's pages as text, from the cache when this file was read before.</summary>
    /// <param name="pdf">The file's bytes.</param>
    /// <param name="hash">Its content hash, which names the cache entry.</param>
    /// <param name="maxOcrPages">Most scanned pages to OCR in this call.</param>
    /// <exception cref="InvalidOperationException">No <c>mutool</c>, or it failed.</exception>
    /// <exception cref="TooManyScannedPages">More scanned pages than <paramref name="maxOcrPages"/>, or no Tesseract for one.</exception>
    public async Task<PagedText> Read(byte[] pdf, string hash, int maxOcrPages, CancellationToken ct = default)
    {
        if (Mutool is null) throw new InvalidOperationException("mutool was not found, so a PDF cannot be read as text.");

        var dir = Path.Combine(directory, hash);
        var manifest = Path.Combine(dir, "pages.json");
        if (File.Exists(manifest))
        {
            try
            {
                if (JsonSerializer.Deserialize<PagedText>(await File.ReadAllTextAsync(manifest, ct)) is { Pages.Count: > 0 } cached)
                    return cached;
            }
            catch (JsonException ex)
            {
                Warn("Re-reading document pages for {0}: {1}", hash, ex.Message);
            }
        }

        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.pdf");
        await File.WriteAllBytesAsync(source, pdf, ct);
        try
        {
            await Run(Mutool, ["draw", "-q", "-F", "txt", "-o", Path.Combine(dir, "page%04d.txt"), source], TextTimeout, ct);
            var files = Directory.GetFiles(dir, "page*.txt").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("mutool produced no pages.");

            var pages = files.Select(File.ReadAllText).ToList();
            var scanned = Enumerable.Range(0, pages.Count)
                .Where(i => pages[i].Count(c => !char.IsWhiteSpace(c)) < ScannedBelowCharacters).ToList();

            if (scanned.Count > 0 && Tesseract is null)
                throw new TooManyScannedPages(scanned.Count, $"{scanned.Count} page(s) are scans and tesseract was not found.");
            if (scanned.Count > maxOcrPages)
                throw new TooManyScannedPages(scanned.Count,
                    $"{scanned.Count} of {pages.Count} pages are scans; OCR here reads at most {maxOcrPages} inside a script's time.");

            foreach (var i in scanned)
            {
                var image = Path.Combine(dir, $"ocr{i + 1:0000}.png");
                await Run(Mutool, ["draw", "-q", "-r", "300", "-c", "gray", "-o", image, source, (i + 1).ToString()], TextTimeout, ct);
                var stem = Path.Combine(dir, $"ocr{i + 1:0000}");
                await Run(Tesseract!, [image, stem], OcrTimeout, ct);
                pages[i] = File.Exists(stem + ".txt") ? await File.ReadAllTextAsync(stem + ".txt", ct) : "";
                File.Delete(image);
            }

            var read = new PagedText { Pages = pages, OcrPages = [.. scanned.Select(i => i + 1)] };
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(read), ct);
            return read;
        }
        finally
        {
            // The text is what is kept; the file itself is already in the project, and two copies of a client's
            // document are one more than needed.
            File.Delete(source);
        }
    }
    #endregion

    #region Methods (private)
    private static async Task Run(string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} did not start.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(exe)} took longer than {timeout.TotalSeconds:0} s.");
        }

        if (process.ExitCode != 0)
        {
            var said = (await stderr).Trim();
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({process.ExitCode}): {said[..Math.Min(said.Length, 300)]}");
        }
    }

    /// <summary><c>bin/[sub/]name</c> walking up from the assembly, as ffmpeg is found, then the PATH.</summary>
    private static string? Find(string name, string sub)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "bin", sub, file);
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), file);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry is not ours to report */ }
        }
        return null;
    }
    #endregion

    #region Fields
    private static readonly TimeSpan TextTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(20);
    readonly string directory;
    #endregion
}

/// <summary>A document's pages as text, and which were read by OCR.</summary>
public sealed record PagedText
{
    public required IReadOnlyList<string> Pages { get; init; }

    /// <summary>Pages, counted from 1, whose text came from OCR rather than the text layer.</summary>
    public IReadOnlyList<int> OcrPages { get; init; } = [];
}

/// <summary>Too many scanned pages to OCR inside one script; the document goes whole to a model that reads scans.</summary>
public sealed class TooManyScannedPages(int scanned, string message) : Exception(message)
{
    public int Scanned { get; } = scanned;
}
