namespace Polson.ExtendedMind.SpeechGeneration;

using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Google.GenAI;
using Google.GenAI.Types;
using Polson.ExtendedMind.ImageGeneration;

/// <summary>The seam between <c>Assets.speak</c> and the service, so the path after a generation can be tested offline.</summary>
public interface ISpeechGenerator
{
    /// <summary>The default model.</summary>
    string Model { get; }

    /// <summary>Speaks <paramref name="prompt"/> in a prebuilt voice. Never throws for a remote fault.</summary>
    Task<SpeechGenerationResult> GenerateSpeech(string prompt, string voice, string? model = null, CancellationToken ct = default);
}

/// <summary>
/// Transport to the Gemini text-to-speech models. Not exposed to scripts: they reach it only through
/// <c>Assets.speak</c>, which bounds the line, checks the voice and the direction, charges the budget and caches.
/// </summary>
/// <remarks>
/// <para>
/// Express-mode auth, as <see cref="ImageGenerator"/>. A line goes in as the text of one user turn, with
/// <c>ResponseModalities = ["AUDIO"]</c> and a prebuilt voice; the model reads a direction before a colon
/// (<c>Say warmly: ...</c>) as how to say what follows rather than as words.
/// </para>
/// <para>
/// <b>The two generations answer differently.</b> The 2.5 models return bare 16-bit PCM labelled
/// <c>audio/L16;codec=pcm;rate=24000</c>; <c>gemini-3.8-flash-lite-tts</c> returns a whole WAV (<c>audio/wav</c>)
/// carrying a C2PA provenance chunk after the samples. Both are normalised to a WAV here, and a WAV that came whole
/// is kept whole so the provenance travels with the file. Measured 2026-10-14; see
/// <c>tests/Polson.Tests.ExtendedMind/SpeechProbeLiveTests.cs</c>.
/// </para>
/// </remarks>
public class SpeechGenerator : Runtime, ISpeechGenerator, IDisposable
{
    #region Constructors
    /// <param name="apiKey">Express-mode API key, from <c>ApiKeys:GoogleAgentPlatform</c>.</param>
    /// <param name="model">Default model; overridable per call.</param>
    public SpeechGenerator(string apiKey, string model = DefaultModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.model = model;
        client = new Client(enterprise: true, apiKey: apiKey);
    }
    #endregion

    #region Properties
    /// <summary>
    /// The fastest model measured (2.6–2.8 s a line). On the probe line the aligner placed every word of its Kore reading
    /// at 0.96 or more; its Puck reading left the last word at 0.67, so read the confidences whichever voice it is.
    /// </summary>
    public const string DefaultModel = "gemini-3.8-flash-lite-tts";

    public string Model => model;
    #endregion

    #region Methods
    public async Task<SpeechGenerationResult> GenerateSpeech(string prompt, string voice, string? model = null, CancellationToken ct = default)
    {
        var useModel = model ?? this.model;
        var config = new GenerateContentConfig
        {
            ResponseModalities = ["AUDIO"],
            SpeechConfig = new SpeechConfig { VoiceConfig = new VoiceConfig { PrebuiltVoiceConfig = new PrebuiltVoiceConfig { VoiceName = voice } } }
        };
        var started = DateTime.UtcNow;

        try
        {
            var response = await client.Models.GenerateContentAsync(
                useModel, [new Content { Role = "user", Parts = [new Part { Text = prompt }] }], config, ct);
            var blob = response.Candidates?.SelectMany(c => c.Content?.Parts ?? []).Select(p => p.InlineData)
                .FirstOrDefault(b => b?.Data is not null);

            if (blob?.Data is not { Length: > 0 } data)
            {
                var finish = response.Candidates?.FirstOrDefault()?.FinishReason?.ToString();
                var reason = finish is not null && (finish.Contains("SAFETY", StringComparison.OrdinalIgnoreCase)
                    || finish.Contains("BLOCK", StringComparison.OrdinalIgnoreCase) || finish.Contains("PROHIBITED", StringComparison.OrdinalIgnoreCase))
                    ? ImageGenerationFailure.SafetyBlocked : ImageGenerationFailure.NoAudioReturned;
                Warn("{Model} returned no audio part (finish={Finish})", useModel, finish ?? "none");
                return SpeechGenerationResult.Failed(reason, $"No audio part in response (finishReason={finish ?? "none"}).", prompt, useModel);
            }

            // Untrusted binary from the network: a WAV is read for what it claims before anything keeps it.
            if (WavFile.Normalise(data, blob.MimeType) is not { } wav)
                return SpeechGenerationResult.Failed(ImageGenerationFailure.NoAudioReturned,
                    $"Unreadable audio payload ({data.Length} bytes, mime={blob.MimeType ?? "unknown"}).", prompt, useModel);

            return new SpeechGenerationResult
            {
                Success = true,
                Wav = wav,
                Model = response.ModelVersion ?? useModel,
                Prompt = prompt,
                GeneratedUtc = started,
                Charged = true,
                TotalTokens = response.UsageMetadata?.TotalTokenCount,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SpeechGenerationResult.Failed(ImageGenerationFailure.Cancelled, "Cancelled by the caller.", prompt, useModel);
        }
        catch (TaskCanceledException ex)
        {
            return SpeechGenerationResult.Failed(ImageGenerationFailure.Timeout, ex.Message, prompt, useModel);
        }
        catch (ClientError ex)
        {
            Error(ex, "{Model} rejected the request ({Status})", useModel, ex.StatusCode);
            return SpeechGenerationResult.Failed(ImageGenerator.Classify(ex.StatusCode, ex.Message), ex.Message, prompt, useModel);
        }
        catch (ServerError ex)
        {
            Error(ex, "{Model} failed server-side ({Status})", useModel, ex.StatusCode);
            return SpeechGenerationResult.Failed(ImageGenerationFailure.ServiceError, ex.Message, prompt, useModel);
        }
        catch (HttpRequestException ex)
        {
            return SpeechGenerationResult.Failed(ImageGenerationFailure.Network, ex.Message, prompt, useModel);
        }
        catch (Exception ex)
        {
            Error(ex, "Unexpected failure speaking with {Model}", useModel);
            return SpeechGenerationResult.Failed(ImageGenerationFailure.Unknown, ex.Message, prompt, useModel);
        }
    }

    /// <summary>Content address of a speech request, and its cache key.</summary>
    public static string HashOf(string model, string voice, string prompt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"speech|{model}|{voice}|{prompt}")))[..32];

    public void Dispose()
    {
        client.Dispose();
        GC.SuppressFinalize(this);
    }
    #endregion

    #region Fields
    readonly string model;
    readonly Client client;
    #endregion
}

/// <summary>One speech generation, or why there is none.</summary>
public sealed record SpeechGenerationResult
{
    public bool Success { get; init; }

    /// <summary>A WAV of 16-bit PCM: as the service sent it when it sent a file, wrapped when it sent bare samples.</summary>
    public byte[] Wav { get; init; } = [];

    public ImageGenerationFailure Failure { get; init; }

    public string? Error { get; init; }

    public string Model { get; init; } = "";

    public string Prompt { get; init; } = "";

    public DateTime GeneratedUtc { get; init; }

    /// <summary>True for a completed generation the service billed; a cache replay is not.</summary>
    public bool Charged { get; init; }

    public bool FromCache { get; init; }

    public int? TotalTokens { get; init; }

    public static SpeechGenerationResult Failed(ImageGenerationFailure failure, string error, string prompt, string model) =>
        new() { Success = false, Failure = failure, Error = error, Prompt = prompt, Model = model };
}

/// <summary>Reading, checking and wrapping the WAVs speech arrives as. Untrusted bytes: anything unexpected is refused, never guessed.</summary>
public static class WavFile
{
    /// <summary>
    /// A WAV of 16-bit PCM from what the service sent, or null when it is neither that nor bare L16 samples. A whole
    /// file is returned unchanged, so a provenance chunk after the samples is kept.
    /// </summary>
    public static byte[]? Normalise(byte[] data, string? mime)
    {
        if (data is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', ..])
            return Read(data) is { Tag: 1, Bits: 16, DataBytes: > 0 } ? data : null;

        // Bare samples are acceptable only when labelled as such; an unlabelled blob is not assumed to be PCM.
        if (mime is null || !mime.StartsWith("audio/L16", StringComparison.OrdinalIgnoreCase) || data.Length < 2) return null;
        var rate = mime.Split(';').Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("rate=", StringComparison.OrdinalIgnoreCase)) is { } r && int.TryParse(r[5..], out var v) && v > 0 ? v : 24000;
        return Wrap(data, rate);
    }

    /// <summary>16-bit mono PCM samples as a WAV.</summary>
    public static byte[] Wrap(byte[] pcm, int rate)
    {
        var length = pcm.Length & ~1;
        using var ms = new MemoryStream(44 + length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(length); w.Write(pcm, 0, length);
        return ms.ToArray();
    }

    /// <summary>Length in seconds, or 0 when the header cannot be read.</summary>
    public static double Seconds(byte[] wav) => Read(wav) is { ByteRate: > 0 } f ? f.DataBytes / (double)f.ByteRate : 0;

    /// <summary>Samples per second, or 0 when the header cannot be read.</summary>
    public static int SampleRate(byte[] wav) => Read(wav)?.Rate ?? 0;

    /// <summary>The format and data length, walking past any chunk it does not use; null for anything that is not a well-formed WAV.</summary>
    static (int Tag, int Bits, int Rate, uint ByteRate, long DataBytes)? Read(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;
        (int Tag, int Bits, int Rate, uint ByteRate)? fmt = null;
        for (long at = 12; at + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, (int)at, 4);
            var size = BitConverter.ToUInt32(wav, (int)at + 4);
            if (id == "fmt " && size >= 16 && at + 24 <= wav.Length)
                fmt = (BitConverter.ToUInt16(wav, (int)at + 8), BitConverter.ToUInt16(wav, (int)at + 22),
                    (int)BitConverter.ToUInt32(wav, (int)at + 12), BitConverter.ToUInt32(wav, (int)at + 16));
            else if (id == "data")
            {
                // A data chunk claiming more than the file holds is truncated, and is read as what is there.
                var available = wav.Length - (at + 8);
                return fmt is { } f ? (f.Tag, f.Bits, f.Rate, f.ByteRate, Math.Min(size, available)) : null;
            }
            at += 8 + (long)size + (size & 1);
        }
        return null;
    }
}

/// <summary>Spoken lines already bought, by content address: a re-run of a script speaks for nothing.</summary>
public interface ISpeechCache
{
    Task<SpeechGenerationResult?> Get(string hash);

    Task Put(string hash, SpeechGenerationResult speech);
}

/// <summary>File-backed <see cref="ISpeechCache"/>: <c>&lt;hash&gt;.wav</c> beside <c>&lt;hash&gt;.json</c>.</summary>
public class SpeechCache : Runtime, ISpeechCache
{
    #region Constructors
    public SpeechCache(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(CamelDir, "speech");
        Directory.CreateDirectory(this.directory);
    }
    #endregion

    #region Methods
    public async Task<SpeechGenerationResult?> Get(string hash)
    {
        var (wav, meta) = (Path.Combine(directory, hash + ".wav"), Path.Combine(directory, hash + ".json"));
        if (!System.IO.File.Exists(wav) || !System.IO.File.Exists(meta)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<Entry>(await System.IO.File.ReadAllTextAsync(meta));
            var bytes = await System.IO.File.ReadAllBytesAsync(wav);
            // A truncated or corrupted entry misses rather than delivering a broken file.
            if (record is null || WavFile.Normalise(bytes, null) is null)
            {
                Warn("Discarding unreadable speech cache entry {Hash}", hash);
                return null;
            }
            return new SpeechGenerationResult
            {
                Success = true, Wav = bytes, Model = record.Model, Prompt = record.Prompt,
                GeneratedUtc = record.GeneratedUtc, FromCache = true, Charged = false
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Warn("Speech cache read failed for {Hash}: {Message}", hash, ex.Message);
            return null;
        }
    }

    public async Task Put(string hash, SpeechGenerationResult speech)
    {
        await System.IO.File.WriteAllBytesAsync(Path.Combine(directory, hash + ".wav"), speech.Wav);
        await System.IO.File.WriteAllTextAsync(Path.Combine(directory, hash + ".json"),
            JsonSerializer.Serialize(new Entry(speech.Model, speech.Prompt, speech.GeneratedUtc)));
    }
    #endregion

    #region Types
    sealed record Entry(string Model, string Prompt, DateTime GeneratedUtc);
    #endregion

    #region Fields
    readonly string directory;
    #endregion
}
