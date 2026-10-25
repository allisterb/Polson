namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SkiaSharp;

/// <summary>
/// The pictures Qloo keeps for its entities — a brand's logo, a film's poster — fetched for visual research and
/// measured on arrival.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a general image fetcher.</b> The only host is <see cref="Host"/>, over https, and redirects are refused
/// rather than followed, so a URL that starts on Qloo's host cannot end anywhere else. The URL itself never comes
/// from a script: <see cref="TasteToolkit.Images"/> takes it from the answer Qloo gave this session.
/// </para>
/// <para>
/// <b>Bytes from the open web are parsed defensively.</b> The body is read up to <see cref="MaxBytes"/> and no
/// further, the header is read before any pixels so a decompression bomb is refused by its declared size, and a
/// decode failure is a named failure rather than an empty picture.
/// </para>
/// <para>
/// Cached on disk beside the Qloo answers (<see cref="QlooCache.DefaultDirectory"/>), outside every repository for
/// the same reason they are, and keyed by URL: Qloo serves its images with a year's <c>max-age</c>, so a picture
/// does not change under its address.
/// </para>
/// </remarks>
public sealed class QlooImageSource : Runtime, IDisposable
{
    #region Constructors
    /// <param name="directory">The disk cache; <c>images</c> under the Qloo cache by default.</param>
    /// <param name="handler">For tests. Redirects must be off on any handler passed in.</param>
    public QlooImageSource(string? directory = null, HttpMessageHandler? handler = null)
    {
        this.directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
        Directory.CreateDirectory(this.directory);
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PolsonStudio/0.1 (https://github.com/allisterb/Polson)");
    }
    #endregion

    #region Properties
    public const string Host = "images.qloo.com";

    /// <summary>Largest body read. Qloo's posters measure 60–85 KB at 420 px.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>Largest picture decoded, by the size its header declares.</summary>
    public const long MaxPixels = 4096L * 4096L;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static string DefaultDirectory => Path.Combine(QlooCache.DefaultDirectory, "images");

    public string Location => directory;
    #endregion

    #region Methods
    /// <summary>Whether a URL is one this source will fetch: https on <see cref="Host"/>.</summary>
    public static bool Allowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort;

    /// <summary>Whether the disk cache holds the picture at <paramref name="url"/>, so fetching it costs nothing.</summary>
    public bool IsCached(string url) => Allowed(url) && File.Exists(PathFor(url));

    /// <summary>The picture at <paramref name="url"/>, from the disk cache or Qloo. Never throws for a bad picture.</summary>
    public async Task<QlooImageFetch> Fetch(string url, CancellationToken cancellationToken = default)
    {
        if (!Allowed(url))
            return QlooImageFetch.Failed(TasteImageFailure.BlockedHost, $"'{url}' is not an https URL on {Host}.");

        var path = PathFor(url);
        if (File.Exists(path))
        {
            try
            {
                var held = Decode(await File.ReadAllBytesAsync(path, cancellationToken), "the cache");
                if (held.Success) return held with { FromCache = true };
            }
            catch (IOException) { }
            // An unreadable entry is a miss; the fetch below rewrites it.
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(DefaultTimeout);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if ((int)response.StatusCode is >= 300 and < 400)
                return QlooImageFetch.Failed(TasteImageFailure.BlockedHost,
                    $"{Host} redirected to {response.Headers.Location?.ToString() ?? "(nowhere)"}; redirects are not followed.");
            if (!response.IsSuccessStatusCode)
                return QlooImageFetch.Failed(response.StatusCode == HttpStatusCode.NotFound ? TasteImageFailure.NoImage : TasteImageFailure.HttpError,
                    $"HTTP {(int)response.StatusCode} fetching {url}.");
            if (response.Content.Headers.ContentLength > MaxBytes)
                return QlooImageFetch.Failed(TasteImageFailure.TooLarge, $"{response.Content.Headers.ContentLength} bytes is over the {MaxBytes}-byte ceiling.");

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cts.Token)) > 0)
            {
                // Counted as it arrives: a missing or false Content-Length must not let a body past the ceiling.
                if (buffer.Length + read > MaxBytes)
                    return QlooImageFetch.Failed(TasteImageFailure.TooLarge, $"The body ran past the {MaxBytes}-byte ceiling.");
                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            var fetched = Decode(bytes, Host);
            if (fetched.Success) Store(path, bytes);
            return fetched;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return QlooImageFetch.Failed(TasteImageFailure.Cancelled, "Cancelled by the caller.");
        }
        catch (OperationCanceledException)
        {
            return QlooImageFetch.Failed(TasteImageFailure.Timeout, $"{Host} did not answer in {DefaultTimeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            return QlooImageFetch.Failed(TasteImageFailure.Network, ex.Message);
        }
    }

    /// <summary>Decodes and measures a picture's bytes. The header is checked before any pixels are allocated.</summary>
    public static QlooImageFetch Decode(byte[] bytes, string from)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null)
                return QlooImageFetch.Failed(TasteImageFailure.Undecodable, $"{bytes.Length} bytes from {from} are not an image.");
            if ((long)codec.Info.Width * codec.Info.Height is var pixels && (pixels == 0 || pixels > MaxPixels))
                return QlooImageFetch.Failed(TasteImageFailure.TooLarge, $"The image declares {codec.Info.Width} × {codec.Info.Height} px.");

            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null || bitmap.Width == 0)
                return QlooImageFetch.Failed(TasteImageFailure.Undecodable, $"{bytes.Length} bytes from {from} did not decode.");

            return new QlooImageFetch
            {
                Success = true, Bytes = bytes, Width = bitmap.Width, Height = bitmap.Height,
                MimeType = codec.EncodedFormat switch
                {
                    SKEncodedImageFormat.Png => "image/png",
                    SKEncodedImageFormat.Webp => "image/webp",
                    SKEncodedImageFormat.Gif => "image/gif",
                    _ => "image/jpeg",
                },
                Colour = TasteColour.Measure(bitmap),
            };
        }
        catch (Exception ex)
        {
            // SKBitmap.Decode throws on some malformed input rather than returning null (see WikimediaPhotoSource).
            return QlooImageFetch.Failed(TasteImageFailure.Undecodable, $"{bytes.Length} bytes from {from} did not decode: {ex.Message}");
        }
    }

    public void Dispose() => http.Dispose();
    #endregion

    #region Private methods
    private string PathFor(string url) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32] + ".img");

    private void Store(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Warn("Could not cache Qloo image {0}: {1}", path, ex.Message);
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    #endregion

    #region Fields
    private readonly string directory;
    private readonly HttpClient http;
    #endregion
}

/// <summary>Why a Qloo picture was not delivered.</summary>
public enum TasteImageFailure
{
    None,
    NotConfigured,
    UnknownEntity,
    NoImage,
    AllowanceSpent,
    BlockedHost,
    TooLarge,
    Undecodable,
    HttpError,
    Network,
    Timeout,
    Cancelled,
}

/// <summary>One fetch, as the source returns it.</summary>
public sealed record QlooImageFetch
{
    public bool Success { get; init; }
    public TasteImageFailure Failure { get; init; }
    public string? Error { get; init; }
    public byte[] Bytes { get; init; } = [];
    public int Width { get; init; }
    public int Height { get; init; }
    public string MimeType { get; init; } = "image/jpeg";
    public bool FromCache { get; init; }
    public TasteColourReading? Colour { get; init; }

    public static QlooImageFetch Failed(TasteImageFailure failure, string error) => new() { Failure = failure, Error = error };
}

/// <summary>
/// One Qloo entity's picture, measured: what a visual-research script reads.
/// </summary>
/// <remarks>
/// <b>Research, never material.</b> These are other people's logos and posters, with their own rights and often
/// their own faces; a script measures and compares them and does not draw them into a deliverable. What a deliverable
/// shows is what was measured — the name, the year, the colours, a few of Qloo's words — never the picture.
/// <para>
/// Exposed to the JavaScript sandbox. Members are PascalCase here; Jint resolves the JS camelCase spelling onto
/// them, so <c>image.styleDescription</c> reaches <see cref="StyleDescription"/>.
/// </para>
/// </remarks>
public sealed class TasteImage
{
    #region Properties
    public bool Success { get; init; }

    public TasteImageFailure Failure { get; init; }

    public string FailureName => Failure.ToString();

    public string? Error { get; init; }

    /// <summary>The Qloo entity id.</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>The entity's Qloo type, e.g. <c>urn:entity:brand</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Where Qloo keeps the picture.</summary>
    public string? Url { get; init; }

    /// <summary>Qloo's one-line description of the entity's look, when it has one. Qloo's words, not a measurement.</summary>
    public string? StyleDescription { get; init; }

    /// <summary>A film's or show's release year, when Qloo gives one.</summary>
    public int? Year { get; init; }

    /// <summary>The entity's place in the answer that named it, from 1: its rank in a profile's list or an insights answer.</summary>
    public int Rank { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string MimeType { get; init; } = "image/jpeg";

    public bool FromCache { get; init; }

    /// <summary>The dominant colours, each <c>{ color, share, hue, chroma, lightness }</c> in OKLCH.</summary>
    public object?[] Colours { get; init; } = [];

    /// <summary>Share of the pixels with chroma under <see cref="TasteColour.NeutralChroma"/>: white, black and greys.</summary>
    public double Neutral { get; init; }

    /// <summary>Mean OKLCH lightness of the picture, 0 (black) to 1 (white).</summary>
    public double Lightness { get; init; }

    /// <summary>Mean OKLCH chroma over every pixel: 0 for greyscale, rising as the picture is more vivid.</summary>
    public double Chroma { get; init; }

    /// <summary>Qloo's popularity percentile for the entity, 0–1, when the answer carried one.</summary>
    public double? Popularity { get; init; }

    /// <summary>
    /// The picture's leading hue, <c>{ hue, chroma, concentration, color }</c>, or null when it is essentially
    /// neutral. <c>concentration</c> is 1 when every coloured pixel shares one hue and near 0 when they disagree;
    /// <c>color</c> is the picture's own pixels near that hue, averaged with the more saturated counting for more.
    /// </summary>
    public Dictionary<string, object?>? Lead { get; init; }

    /// <summary>Which of this session's Qloo answers named the entity.</summary>
    public string? Answer { get; init; }
    #endregion

    #region Methods
    /// <summary>
    /// The picture as a data URI, for <c>Skia.Image.fromDataUrl(...)</c>. Empty when there is none. Taking it marks
    /// the script's renders as research, which the studio never shows.
    /// </summary>
    public string ToDataUri()
    {
        if (bytes.Length == 0) return string.Empty;
        taken?.Invoke(this);
        return $"data:{MimeType};base64,{Convert.ToBase64String(bytes)}";
    }

    public override string ToString() => Success ? $"{Name} {Width}×{Height}" : $"{Name}: {Failure}";
    #endregion

    #region Fields
    internal byte[] bytes = [];

    /// <summary>Told when a script takes the pixels: the one way a Qloo picture can reach a render.</summary>
    internal Action<TasteImage>? taken;
    #endregion
}

/// <summary>A picture's colour, measured once on arrival. <c>Chroma</c> is the mean over every pixel: muted to vivid.</summary>
public sealed record TasteColourReading(object?[] Colours, double Neutral, double Lightness, Dictionary<string, object?>? Lead, double Chroma = 0);

/// <summary>
/// Colour measurement in OKLCH (Björn Ottosson's Oklab, 2020), where hue angles and chroma are close to how they
/// look: two pictures 30° apart differ by about the same amount anywhere on the wheel, which sRGB's HSL does not.
/// </summary>
public static class TasteColour
{
    #region Properties
    /// <summary>Below this OKLCH chroma a colour reads as neutral. The purest sRGB colours reach about 0.32.</summary>
    public const double NeutralChroma = 0.04;

    /// <summary>A picture whose coloured pixels are fewer than this share has no lead hue.</summary>
    public const double MinChromaticShare = 0.05;
    #endregion

    #region Methods
    /// <summary>Converts sRGB bytes to OKLCH: lightness 0–1, chroma 0–~0.32, hue in degrees 0–360.</summary>
    public static (double L, double C, double H) Oklch(byte r, byte g, byte b)
    {
        static double Linear(byte v) => v / 255d is var c && c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        double lr = Linear(r), lg = Linear(g), lb = Linear(b);
        var l = Math.Cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
        var m = Math.Cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
        var s = Math.Cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);
        var L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        var h = Math.Atan2(B, A) * 180 / Math.PI;
        return (L, Math.Sqrt(A * A + B * B), h < 0 ? h + 360 : h);
    }

    /// <summary>
    /// The dominant colours, the neutral share, the mean lightness and the lead hue: a chroma-weighted circular
    /// mean over the coloured pixels, so a poster of many hues still has a direction and a measure of how strongly.
    /// </summary>
    public static TasteColourReading Measure(SKBitmap bitmap, int count = 6)
    {
        // Measured at no more than 160 px across: colour shares do not need every pixel of a 420 px poster.
        var scale = Math.Min(1d, 160d / Math.Max(bitmap.Width, bitmap.Height));
        using var small = scale < 1
            ? bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear))
            : null;
        var source = small ?? bitmap;

        const int buckets = 16, size = 256 / buckets;
        var tally = new Dictionary<int, (long N, long R, long G, long B)>();
        long total = 0, neutral = 0;
        double sumL = 0, sumC = 0, x = 0, y = 0, chromaSum = 0;

        for (var j = 0; j < source.Height; j++)
        for (var i = 0; i < source.Width; i++)
        {
            var p = source.GetPixel(i, j);
            if (p.Alpha == 0) continue;
            total++;
            var (L, C, H) = Oklch(p.Red, p.Green, p.Blue);
            sumL += L;
            sumC += C;
            if (C < NeutralChroma) neutral++;
            else
            {
                x += C * Math.Cos(H * Math.PI / 180);
                y += C * Math.Sin(H * Math.PI / 180);
                chromaSum += C;
            }
            var key = (p.Red / size * buckets + p.Green / size) * buckets + p.Blue / size;
            var t = tally.GetValueOrDefault(key);
            tally[key] = (t.N + 1, t.R + p.Red, t.G + p.Green, t.B + p.Blue);
        }
        if (total == 0) return new TasteColourReading([], 1, 0, null, 0);

        var colours = tally.OrderByDescending(e => e.Value.N).Take(count).Select(e =>
        {
            byte r = (byte)(e.Value.R / e.Value.N), g = (byte)(e.Value.G / e.Value.N), b = (byte)(e.Value.B / e.Value.N);
            var (L, C, H) = Oklch(r, g, b);
            return (object?)new Dictionary<string, object?>
            {
                ["color"] = $"#{r:X2}{g:X2}{b:X2}", ["share"] = Math.Round((double)e.Value.N / total, 4),
                ["hue"] = Math.Round(H, 1), ["chroma"] = Math.Round(C, 4), ["lightness"] = Math.Round(L, 4),
            };
        }).ToArray();

        var chromatic = total - neutral;
        Dictionary<string, object?>? lead = null;
        if ((double)chromatic / total >= MinChromaticShare && chromaSum > 0)
        {
            var hue = Math.Atan2(y, x) * 180 / Math.PI;
            hue = hue < 0 ? hue + 360 : hue;

            // The lead's colour is the picture's own pixels near that hue, averaged with the more saturated counting
            // for more. The commonest coloured bucket was tried first and fails twice over: on a dark poster it is a
            // near-black brown that hides the hue, and when no common bucket is coloured there is none at all.
            double w = 0, sr = 0, sg = 0, sb = 0;
            for (var j = 0; j < source.Height; j++)
            for (var i = 0; i < source.Width; i++)
            {
                var p = source.GetPixel(i, j);
                if (p.Alpha == 0) continue;
                var (_, C, H) = Oklch(p.Red, p.Green, p.Blue);
                if (C < NeutralChroma || HueDistance(H, hue) > 30) continue;
                w += C; sr += C * p.Red; sg += C * p.Green; sb += C * p.Blue;
            }
            lead = new()
            {
                ["hue"] = Math.Round(hue, 1),
                ["chroma"] = Math.Round(chromaSum / chromatic, 4),
                ["concentration"] = Math.Round(Math.Sqrt(x * x + y * y) / chromaSum, 3),
                ["color"] = w > 0 ? $"#{(byte)(sr / w):X2}{(byte)(sg / w):X2}{(byte)(sb / w):X2}" : null,
            };
        }
        return new TasteColourReading(colours, Math.Round((double)neutral / total, 4), Math.Round(sumL / total, 4), lead,
            Math.Round(sumC / total, 4));
    }

    /// <summary>The shorter way round the wheel between two hues, 0–180°.</summary>
    public static double HueDistance(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);
    #endregion
}
