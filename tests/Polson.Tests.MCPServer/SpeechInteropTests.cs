namespace Polson.Tests.MCPServer;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Polson.ExtendedMind.ImageGeneration;
using Polson.ExtendedMind.SpeechGeneration;
using Polson.MCPServer;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// <c>Assets.speak</c> as a script reaches it: the options object maps onto <see cref="SpeechOptions"/>, the line lands
/// in the execution's project, the record names the file, and <c>JSON.stringify</c> reports the documented surface.
/// </summary>
[Collection(AssetsCollection.Name)]
public class SpeechInteropTests : TestsRuntime, IDisposable
{
    #region Fields
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-speakjs-" + Guid.NewGuid().ToString("N"));
    private readonly AssetRequisitionToolkit? was = JsDrawingEngine.Assets;
    private readonly ITestOutputHelper output;
    #endregion

    #region Methods
    public SpeechInteropTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        JsDrawingEngine.Assets = was;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private JsonElement[] Events(string type) =>
        File.ReadAllLines(Path.Combine(root, "events", "server.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == type)
            .ToArray();
    #endregion

    #region Tests
    [Fact]
    public async Task TestAScriptSpeaksALineIntoItsProject()
    {
        JsDrawingEngine.Assets = new AssetRequisitionToolkit(null, new RequisitionCache(Path.Combine(root, ".cache", "assets")),
            new AssetBudget(3), "agent", new Speaker(), new SpeechCache(Path.Combine(root, ".cache", "speech")));

        var result = await new DrawingMcpTools(null, null, null, root).ExecuteScript("""
            const spoken = await Assets.speak('Who knew? Bob moved the moon so far, for fun!',
                { voice: 'Puck', style: 'warmly', path: 'audio/line.wav' });
            log(JSON.stringify(spoken));
            const wrong = await Assets.speak('Hi.', { voice: 'Koree' });
            log(wrong.failureName + ': ' + wrong.error);
            const voices = Assets.voices;
            log('VOICES ' + JSON.stringify({ length: voices.length, isArray: Array.isArray(voices),
              gacrux: voices.find(v => v.name === 'Gacrux').description,
              mature: voices.filter(v => v.description === 'Mature').map(v => v.name) }));
            const c = createCanvas(8, 8); c.getContext('2d').fillRect(0, 0, 8, 8); c;
            """, 8, 8);

        Assert.True(File.Exists(Path.Combine(root, "audio", "line.wav")));

        var json = JsonDocument.Parse(result.Logs.First(l => l.StartsWith("[LOG] {"))["[LOG] ".Length..]).RootElement;
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("audio/line.wav", json.GetProperty("path").GetString());
        Assert.Equal("Puck", json.GetProperty("voice").GetString());
        Assert.Equal("warmly", json.GetProperty("style").GetString());
        Assert.Equal(1.0, json.GetProperty("duration").GetDouble(), 3);
        Assert.False(json.TryGetProperty("Wav", out _));
        Assert.Contains(result.Logs, l => l.Contains("InvalidRequest") && l.Contains("Did you mean Kore"));
        var voices = JsonDocument.Parse(result.Logs.First(l => l.StartsWith("[LOG] VOICES "))["[LOG] VOICES ".Length..]).RootElement;
        Assert.Equal(30, voices.GetProperty("length").GetInt32());
        Assert.Equal("Mature", voices.GetProperty("gacrux").GetString());
        Assert.Equal(["Gacrux"], voices.GetProperty("mature").EnumerateArray().Select(v => v.GetString()));
        output.WriteLine("Assets.voices is a JS array: " + voices.GetProperty("isArray").GetBoolean());

        var bought = Assert.Single(Events("asset.requisition"));
        Assert.Equal("speech", bought.GetProperty("kind").GetString());
        Assert.Equal("audio/line.wav", bought.GetProperty("artifact").GetString());
        Assert.Equal("speech", Assert.Single(Events("asset.refused")).GetProperty("kind").GetString());
        Assert.Equal(1, Assert.Single(Events("budget")).GetProperty("spent").GetInt32());
    }
    #endregion

    #region Types
    /// <summary>A second of silence as a WAV, without a network.</summary>
    private sealed class Speaker : ISpeechGenerator
    {
        public string Model => SpeechGenerator.DefaultModel;

        public Task<SpeechGenerationResult> GenerateSpeech(string text, string? style, string voice, string? model = null, CancellationToken ct = default) =>
            Task.FromResult(new SpeechGenerationResult
            {
                Success = true, Wav = WavFile.Wrap(new byte[48000], 24000), Model = Model, Prompt = SpeechGenerator.Describe(text, style),
                GeneratedUtc = DateTime.UtcNow, Charged = true, TotalTokens = 100
            });
    }
    #endregion
}
