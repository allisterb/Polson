namespace Polson.Tests.ExtendedMind;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using global::Polson.Tests;
using Google.GenAI;
using Google.GenAI.Types;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Which Gemini text-to-speech models this project's key reaches, measured rather than read from documentation, before
/// a speech surface is built on any of them.
/// </summary>
/// <remarks>
/// <b>Live and billed: one short sentence per model that answers.</b> Runs only with <c>POLSON_LIVE_SPEECH_PROBE=1</c>
/// and a key in <c>testappsettings.json</c>; a model name that does not resolve fails with a 404 and bills nothing. Each
/// answer is written as a WAV to <c>POLSON_LIVE_OUT</c> (a temp folder otherwise) to be listened to. The assertions only
/// say the probe ran: the result is the table it writes.
/// </remarks>
public class SpeechProbeLiveTests : TestsRuntime
{
    readonly ITestOutputHelper output;
    readonly string? apiKey;

    public SpeechProbeLiveTests(ITestOutputHelper output)
    {
        this.output = output;
        this.apiKey = config["ApiKeys:GoogleAgentPlatform"];
    }

    /// <summary>Every model probed, unless <c>POLSON_LIVE_SPEECH_MODELS</c> names some (comma-separated), so a new one can be tried alone.</summary>
    static string[] Candidates =>
        System.Environment.GetEnvironmentVariable("POLSON_LIVE_SPEECH_MODELS") is { Length: > 0 } named
            ? named.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            :
            [
                "gemini-2.5-flash-tts", "gemini-2.5-pro-tts",
                "gemini-2.5-flash-preview-tts", "gemini-2.5-pro-preview-tts", "gemini-2.5-flash-lite-preview-tts",
                "gemini-3.8-flash-lite-tts"
            ];

    [Fact]
    public async Task Speech_Live_WhichTextToSpeechModelsAnswer()
    {
        if (string.IsNullOrWhiteSpace(this.apiKey)) return;   // no key configured: nothing to exercise
        if (System.Environment.GetEnvironmentVariable("POLSON_LIVE_SPEECH_PROBE") != "1") return;   // opt-in: this one bills

        var dir = System.Environment.GetEnvironmentVariable("POLSON_LIVE_OUT") ?? Path.Combine(Path.GetTempPath(), "polson-speech-probe");
        Directory.CreateDirectory(dir);
        var client = new Client(enterprise: true, apiKey: this.apiKey);
        const string line = "Who knew? Bob moved the moon so far, for fun!";
        var answered = 0;
        var candidates = Candidates;

        foreach (var model in candidates)
        {
            var config = new GenerateContentConfig
            {
                ResponseModalities = ["AUDIO"],
                SpeechConfig = new SpeechConfig { VoiceConfig = new VoiceConfig { PrebuiltVoiceConfig = new PrebuiltVoiceConfig { VoiceName = "Kore" } } }
            };
            var clock = Stopwatch.StartNew();
            try
            {
                var response = await client.Models.GenerateContentAsync(model, [new Content { Role = "user", Parts = [new Part { Text = line }] }], config);
                var blob = response.Candidates?.SelectMany(c => c.Content?.Parts ?? []).Select(p => p.InlineData).FirstOrDefault(b => b?.Data is not null);
                if (blob?.Data is not { Length: > 0 } pcm)
                {
                    output.WriteLine($"{model}: answered in {clock.ElapsedMilliseconds} ms with no audio");
                    continue;
                }

                // The 2.5 models answer audio/L16, bare PCM to be wrapped; gemini-3.8-flash-lite-tts answers audio/wav, a
                // whole file with a C2PA provenance chunk after the samples, written as it came so the chunk is kept.
                var whole = pcm is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', ..];
                var rate = RateOf(blob.MimeType);
                var path = Path.Combine(dir, $"{model}.wav");
                System.IO.File.WriteAllBytes(path, whole ? pcm : Wav(pcm, rate));
                var seconds = whole ? WavSeconds(pcm) : pcm.Length / 2.0 / rate;
                var usage = response.UsageMetadata;
                output.WriteLine($"{model}: OK in {clock.ElapsedMilliseconds} ms; {blob.MimeType}; {pcm.Length} bytes = {seconds:0.00} s; "
                    + $"tokens in {usage?.PromptTokenCount} out {usage?.CandidatesTokenCount}; {path}");
                answered++;
            }
            catch (Exception e)
            {
                output.WriteLine($"{model}: {e.GetType().Name} in {clock.ElapsedMilliseconds} ms: {Short(e.Message)}");
            }
        }

        output.WriteLine($"{answered} of {candidates.Length} answered with audio");
    }

    /// <summary>
    /// <c>Assets.speak</c> end to end against the real service: one line in a voice other than the probe's, written into
    /// a project folder (<c>POLSON_LIVE_OUT</c>), then asked for again to show the second costs nothing. Billed once.
    /// </summary>
    [Fact]
    public async Task Speech_Live_AssetsSpeakWritesALine()
    {
        if (string.IsNullOrWhiteSpace(this.apiKey)) return;
        if (System.Environment.GetEnvironmentVariable("POLSON_LIVE_SPEECH_PROBE") != "1") return;   // opt-in: this one bills

        var project = System.Environment.GetEnvironmentVariable("POLSON_LIVE_OUT") ?? Path.Combine(Path.GetTempPath(), "polson-speech-probe");
        using var speech = new global::Polson.ExtendedMind.SpeechGeneration.SpeechGenerator(this.apiKey);
        var budget = new global::Polson.ExtendedMind.ImageGeneration.AssetBudget(3);
        var toolkit = new global::Polson.ExtendedMind.ImageGeneration.AssetRequisitionToolkit(null,
            new global::Polson.ExtendedMind.ImageGeneration.RequisitionCache(Path.Combine(project, ".cache", "assets")), budget, "live",
            speech, new global::Polson.ExtendedMind.SpeechGeneration.SpeechCache(Path.Combine(project, ".cache", "speech")));
        var options = new global::Polson.ExtendedMind.SpeechGeneration.SpeechOptions { Voice = "Puck", Path = "assets-speak-puck.wav" };

        using var scope = global::Polson.RequisitionScope.Begin(project);
        var clock = Stopwatch.StartNew();
        var spoken = await toolkit.Speak("Who knew? Bob moved the moon so far, for fun!", options);
        output.WriteLine($"speak: {spoken.Success} in {clock.ElapsedMilliseconds} ms; {spoken.FailureName} {spoken.Error}; "
            + $"{spoken.Path} {spoken.Duration:0.00} s at {spoken.SampleRate} Hz, {spoken.ByteLength} bytes; model {spoken.Provenance?.Model}; "
            + $"tokens {budget.TokensSpent}; warnings [{string.Join("; ", spoken.Warnings)}]");
        Assert.True(spoken.Success, spoken.Error);

        var again = await toolkit.Speak("Who knew? Bob moved the moon so far, for fun!", options);
        output.WriteLine($"again: fromCache {again.Provenance?.FromCache}; spent {budget.Spent}, cache hits {budget.CacheHits}");
        Assert.Equal(1, budget.Spent);
    }

    /// <summary>
    /// Whether a direction sent as <c>speechMetadata.style</c> is followed rather than spoken, on the endpoint this key
    /// reaches. anim3's keeper said "Shouting... Ahoy!" when the direction was written into the text. Two takes of that
    /// line, with the direction and without, plus one with an inline tag and a capitalised word, are written to
    /// <c>POLSON_LIVE_OUT</c> to be transcribed and compared. Billed up to three times; a take already cached is free.
    /// </summary>
    [Fact]
    public async Task Speech_Live_AStyleIsFollowedNotSpoken()
    {
        if (string.IsNullOrWhiteSpace(this.apiKey)) return;
        if (System.Environment.GetEnvironmentVariable("POLSON_LIVE_SPEECH_PROBE") != "1") return;   // opt-in: this one bills

        var project = System.Environment.GetEnvironmentVariable("POLSON_LIVE_OUT") ?? Path.Combine(Path.GetTempPath(), "polson-speech-probe");
        using var speech = new global::Polson.ExtendedMind.SpeechGeneration.SpeechGenerator(this.apiKey);
        var budget = new global::Polson.ExtendedMind.ImageGeneration.AssetBudget(3);
        var toolkit = new global::Polson.ExtendedMind.ImageGeneration.AssetRequisitionToolkit(null,
            new global::Polson.ExtendedMind.ImageGeneration.RequisitionCache(Path.Combine(project, ".cache", "assets")), budget, "live",
            speech, new global::Polson.ExtendedMind.SpeechGeneration.SpeechCache(Path.Combine(project, ".cache", "speech")));
        using var scope = global::Polson.RequisitionScope.Begin(project);

        const string line = "Ahoy! Bear away, you're on the rocks!";
        // The third take is the guide's other half: a moment as an inline tag and a stressed word in capitals, which
        // must be performed rather than read out as "short pause".
        foreach (var (name, text, style) in new[]
                 {
                     ("style-shouting.wav", line, "shouting over the wind"), ("style-none.wav", line, (string?)null),
                     ("style-tag.wav", "Ahoy! <short pause> Bear away, you're on the ROCKS!", (string?)null)
                 })
        {
            var clock = Stopwatch.StartNew();
            var spoken = await toolkit.Speak(text, new global::Polson.ExtendedMind.SpeechGeneration.SpeechOptions { Voice = "Algenib", Style = style, Path = name });
            output.WriteLine($"{name}: {spoken.Success} {spoken.FailureName} {spoken.Error} in {clock.ElapsedMilliseconds} ms; "
                + $"{spoken.Duration:0.00} s; tokens so far {budget.TokensSpent}; sent: {spoken.Provenance?.Prompt}; warnings [{string.Join("; ", spoken.Warnings)}]");
            Assert.True(spoken.Success, spoken.Error);
        }
    }

    /// <summary>The sample rate a <c>audio/L16;codec=pcm;rate=24000</c> mime type states; 24 kHz when it does not.</summary>
    static int RateOf(string? mime) =>
        mime?.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("rate=", StringComparison.Ordinal)) is { } r && int.TryParse(r[5..], out var v) ? v : 24000;

    /// <summary>16-bit mono PCM wrapped as a WAV file.</summary>
    static byte[] Wav(byte[] pcm, int rate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        return ms.ToArray();
    }

    /// <summary>A WAV file's length from its <c>fmt </c> byte rate and <c>data</c> size, walking past any other chunk.</summary>
    static double WavSeconds(byte[] wav)
    {
        uint byteRate = 0;
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, at, 4);
            var size = BitConverter.ToUInt32(wav, at + 4);
            if (id == "fmt ") byteRate = BitConverter.ToUInt32(wav, at + 16);
            if (id == "data") return byteRate > 0 ? size / (double)byteRate : 0;
            at += 8 + (int)size + (int)(size & 1);
        }
        return 0;
    }

    static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
