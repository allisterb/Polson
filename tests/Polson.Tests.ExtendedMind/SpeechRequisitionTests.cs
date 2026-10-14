namespace Polson.Tests.ExtendedMind;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using global::Polson;
using global::Polson.Tests;
using Polson.ExtendedMind.ImageGeneration;
using Polson.ExtendedMind.SpeechGeneration;
using Xunit;

/// <summary>
/// <c>Assets.speak</c> offline: a fake transport returns a fixed WAV and counts its calls, so the cache, the budget, the
/// refusals and the file the line lands in are all checked without speaking to anything.
/// </summary>
public class SpeechRequisitionTests : TestsRuntime, IDisposable
{
    #region Fields
    readonly string project = Path.Combine(Path.GetTempPath(), "polson-speak-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public SpeechRequisitionTests() => Directory.CreateDirectory(project);

    public void Dispose()
    {
        if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        GC.SuppressFinalize(this);
    }

    AssetRequisitionToolkit Toolkit(ISpeechGenerator? speech, AssetBudget? budget = null) =>
        new(null, new RequisitionCache(ImageGeneratorTests.TempCacheDir()), budget ?? new AssetBudget(5), "Director",
            speech, new SpeechCache(ImageGeneratorTests.TempCacheDir()));

    /// <summary>A WAV as gemini-3.8-flash-lite-tts sends one: 24 kHz mono 16-bit, with a provenance chunk after the samples.</summary>
    internal static byte[] ServiceWav(double seconds, bool provenance = true)
    {
        var pcm = new byte[(int)(seconds * 24000) * 2];
        var wav = WavFile.Wrap(pcm, 24000);
        if (!provenance) return wav;
        var chunk = Encoding.ASCII.GetBytes("C2PA").Concat(BitConverter.GetBytes(8)).Concat(Encoding.ASCII.GetBytes("manifest")).ToArray();
        var whole = wav.Concat(chunk).ToArray();
        BitConverter.GetBytes(whole.Length - 8).CopyTo(whole, 4);
        return whole;
    }
    #endregion

    #region Tests
    /// <summary>A spoken line lands in the project as the service sent it, provenance chunk and all, and is charged once.</summary>
    [Fact]
    public async Task TestALineIsWrittenIntoTheProjectAndChargedOnce()
    {
        var fake = new FakeSpeechGenerator(ServiceWav(2.0));
        var budget = new AssetBudget(5);
        var toolkit = Toolkit(fake, budget);

        SpeechAsset spoken;
        RequisitionRecord record;
        using (var scope = RequisitionScope.Begin(project))
        {
            spoken = await toolkit.Speak("Who knew? Bob moved the moon so far, for fun!", new SpeechOptions { Voice = "puck" });
            record = Assert.Single(scope.Records);
        }

        Assert.True(spoken.Success, spoken.Error);
        Assert.Equal("Puck", spoken.Voice);
        Assert.Equal("Puck", fake.LastVoice);
        Assert.StartsWith("audio/", spoken.Path);
        Assert.EndsWith(".wav", spoken.Path);
        Assert.Equal(2.0, spoken.Duration, 3);
        Assert.Equal(24000, spoken.SampleRate);
        Assert.Empty(spoken.Warnings);

        var written = File.ReadAllBytes(Path.Combine(project, spoken.Path));
        Assert.Equal(fake.Wav, written);
        Assert.Contains("C2PA", Encoding.ASCII.GetString(written));

        Assert.Equal(1, budget.Spent);
        Assert.Equal(104, budget.TokensSpent);
        Assert.Equal(SpeechGenerator.DefaultModel, spoken.Provenance!.Model);
        Assert.Equal("Director", spoken.Provenance.Requester);
        Assert.Equal("speech", record.Kind);
        Assert.Equal(Path.GetFullPath(Path.Combine(project, spoken.Path)), record.Artifact);
        Assert.False(record.FromCache);
    }

    /// <summary>The same line in the same voice is not bought twice; a new path gets the cached bytes.</summary>
    [Fact]
    public async Task TestTheSameLineIsServedFromTheCache()
    {
        var fake = new FakeSpeechGenerator(ServiceWav(1.0));
        var budget = new AssetBudget(5);
        var toolkit = Toolkit(fake, budget);
        using var scope = RequisitionScope.Begin(project);

        var first = await toolkit.Speak("Hello there.");
        var again = await toolkit.Speak("Hello there.", new SpeechOptions { Path = "lines/hello.wav" });
        var other = await toolkit.Speak("Hello there.", new SpeechOptions { Voice = "Charon" });

        Assert.True(again.Success, again.Error);
        Assert.Equal(2, fake.Calls);
        Assert.Equal(2, budget.Spent);
        Assert.Equal(1, budget.CacheHits);
        Assert.True(again.Provenance!.FromCache);
        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, other.Id);
        Assert.True(File.Exists(Path.Combine(project, "lines", "hello.wav")));
        Assert.True(scope.Records[1].FromCache);
    }

    /// <summary>A direction goes before a colon, which the model reads as how to say the line rather than as words.</summary>
    [Fact]
    public async Task TestADirectionIsSentAheadOfTheLine()
    {
        var fake = new FakeSpeechGenerator(ServiceWav(1.5));
        using var scope = RequisitionScope.Begin(project);

        var spoken = await Toolkit(fake).Speak("Land ho!", new SpeechOptions { Style = "shout it, hoarse from the wind:" });

        Assert.True(spoken.Success, spoken.Error);
        Assert.Equal("shout it, hoarse from the wind: Land ho!", fake.LastPrompt);
        Assert.Equal(fake.LastPrompt, spoken.Provenance!.Prompt);
        Assert.Equal("shout it, hoarse from the wind", spoken.Style);
    }

    /// <summary>A line far longer than its words allow is spoken, and the result says what that may mean.</summary>
    [Fact]
    public async Task TestALengthTheWordsDoNotExplainIsWarnedAbout()
    {
        using var scope = RequisitionScope.Begin(project);
        var spoken = await Toolkit(new FakeSpeechGenerator(ServiceWav(9.0))).Speak("Land ho!", new SpeechOptions { Style = "warmly" });

        Assert.True(spoken.Success);
        Assert.Contains("direction were read aloud", Assert.Single(spoken.Warnings));
    }

    /// <summary>Every refusal happens before the service is reached and spends nothing, and each says what to do instead.</summary>
    [Theory]
    [InlineData("", "Kore", null, null, "needs the line")]
    [InlineData("Hi.", "Koree", null, null, "Did you mean Kore")]
    [InlineData("Hi.", "Kore", "like Morgan Freeman", null, "Morgan Freeman")]
    [InlineData("Hi.", "Kore", null, "../outside.wav", "outside this project")]
    [InlineData("Hi.", "Kore", null, "audio/line.mp3", "ends .wav")]
    public async Task TestARefusalSpendsNothing(string text, string voice, string? style, string? path, string message)
    {
        var fake = new FakeSpeechGenerator(ServiceWav(1.0));
        var budget = new AssetBudget(5);
        using var scope = RequisitionScope.Begin(project);

        var spoken = await Toolkit(fake, budget).Speak(text, new SpeechOptions { Voice = voice, Style = style, Path = path });

        Assert.False(spoken.Success);
        Assert.Contains(message, spoken.Error);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(0, budget.Spent);
        Assert.True(Assert.Single(scope.Records).Refused);
        Assert.Empty(Directory.GetFiles(project, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TestAnOverLongLineIsRefused()
    {
        var fake = new FakeSpeechGenerator(ServiceWav(1.0));
        var spoken = await Toolkit(fake).Speak(new string('a', AssetRequisitionToolkit.MaxSpeechCharacters + 1));
        Assert.False(spoken.Success);
        Assert.Contains("Split it into lines", spoken.Error);
        Assert.Equal(0, fake.Calls);
    }

    /// <summary>Without credentials, or without budget, the remedy names the way to go on without a voice.</summary>
    [Fact]
    public async Task TestUnconfiguredOrSpentSpeechSaysHowToGoOn()
    {
        using var scope = RequisitionScope.Begin(project);
        var off = await Toolkit(null).Speak("Hi.");
        Assert.Equal(ImageGenerationFailure.NotConfigured, off.Failure);
        Assert.Contains("Motion.speech(text)", off.Remedy);

        var spent = await Toolkit(new FakeSpeechGenerator(ServiceWav(1.0)), new AssetBudget(0)).Speak("Hi.");
        Assert.Equal(ImageGenerationFailure.BudgetExhausted, spent.Failure);
        Assert.Contains("Motion.speech(text)", spent.Remedy);
    }

    /// <summary>
    /// What the service sends is read for what it claims: a whole 16-bit WAV is kept as it is, bare samples are kept only
    /// when labelled as L16 and are wrapped at the labelled rate, and anything else is refused rather than guessed at.
    /// </summary>
    [Fact]
    public void TestServiceAudioIsReadForWhatItClaims()
    {
        var whole = ServiceWav(1.0);
        Assert.Same(whole, WavFile.Normalise(whole, "audio/wav"));

        var wrapped = WavFile.Normalise(new byte[48000], "audio/L16;codec=pcm;rate=24000")!;
        Assert.Equal(1.0, WavFile.Seconds(wrapped), 3);
        Assert.Equal(24000, WavFile.SampleRate(wrapped));

        Assert.Null(WavFile.Normalise(new byte[48000], null));
        Assert.Null(WavFile.Normalise(new byte[48000], "audio/mpeg"));
        var float32 = WavFile.Wrap(new byte[4000], 24000);
        float32[20] = 3;                                         // format tag 3, IEEE float
        Assert.Null(WavFile.Normalise(float32, "audio/wav"));
        Assert.Null(WavFile.Normalise("RIFF0000WAVE"u8.ToArray(), "audio/wav"));
    }
    #endregion

    #region Types
    /// <summary>Returns fixed bytes and counts its calls; never touches a network.</summary>
    internal sealed class FakeSpeechGenerator(byte[] wav) : ISpeechGenerator
    {
        public byte[] Wav { get; } = wav;
        public int Calls { get; private set; }
        public string? LastPrompt { get; private set; }
        public string? LastVoice { get; private set; }
        public string Model => SpeechGenerator.DefaultModel;

        public Task<SpeechGenerationResult> GenerateSpeech(string prompt, string voice, string? model = null, CancellationToken ct = default)
        {
            Calls++;
            LastPrompt = prompt;
            LastVoice = voice;
            return Task.FromResult(new SpeechGenerationResult
            {
                Success = true, Wav = Wav, Model = model ?? Model, Prompt = prompt, GeneratedUtc = DateTime.UtcNow,
                Charged = true, TotalTokens = 104
            });
        }
    }
    #endregion
}
