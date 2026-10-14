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

    static readonly string[] Candidates =
    [
        "gemini-2.5-flash-tts", "gemini-2.5-pro-tts",
        "gemini-2.5-flash-preview-tts", "gemini-2.5-pro-preview-tts", "gemini-2.5-flash-lite-preview-tts"
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

        foreach (var model in Candidates)
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

                var rate = RateOf(blob.MimeType);
                var path = Path.Combine(dir, $"{model}.wav");
                System.IO.File.WriteAllBytes(path, Wav(pcm, rate));
                var usage = response.UsageMetadata;
                output.WriteLine($"{model}: OK in {clock.ElapsedMilliseconds} ms; {blob.MimeType}; {pcm.Length} bytes = {pcm.Length / 2.0 / rate:0.00} s; "
                    + $"tokens in {usage?.PromptTokenCount} out {usage?.CandidatesTokenCount}; {path}");
                answered++;
            }
            catch (Exception e)
            {
                output.WriteLine($"{model}: {e.GetType().Name} in {clock.ElapsedMilliseconds} ms: {Short(e.Message)}");
            }
        }

        output.WriteLine($"{answered} of {Candidates.Length} answered with audio");
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

    static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
