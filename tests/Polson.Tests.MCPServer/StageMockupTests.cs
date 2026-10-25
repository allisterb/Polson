namespace Polson.Tests.MCPServer;

using System;
using System.IO;
using System.Linq;
using Polson.MCPServer;
using SkiaSharp;
using Xunit;

/// <summary>
/// <c>Stage.mockup</c> as a script calls it: the piece shown where it will live, filed as a mock-up and recorded as a
/// sheet, never as the work.
/// </summary>
public class StageMockupTests : TestsRuntime, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-mockup-" + Guid.NewGuid().ToString("N"));

    public StageMockupTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        using var bitmap = new SKBitmap(420, 594);
        bitmap.Erase(new SKColor(0x16, 0x24, 0x2b));
        File.WriteAllBytes(Path.Combine(root, "artifacts", "final_a.png"), bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private DrawingExecutionResult Run(string body) =>
        new JsDrawingEngine { ProjectRoot = root, Events = new RunEventLog(root) }.Execute($"{body}\nconst c = createCanvas(8, 8); c;", 8, 8, null, "png", 90);

    [Fact]
    public void TestAMockupIsWrittenAndRecordedAsASheet()
    {
        var result = Run("""
            const m = Stage.mockup('artifacts/mockup_wall_a.png', { design: 'artifacts/final_a.png', setting: 'wall', size: 'A2' });
            log(`mockup ${m.setting} ${m.size.width}x${m.size.height} ${m.image.width}x${m.image.height} ${m.corners.length}`);
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("mockup wall 420x594 1600x1000 4"));
        Assert.True(File.Exists(Path.Combine(root, "artifacts", "mockup_wall_a.png")));
        var record = File.ReadAllLines(Path.Combine(root, "events", "server.jsonl")).Single(l => l.Contains("mockup_wall_a.png"));
        Assert.Contains("\"kind\":\"mockup\"", record);
        Assert.Contains("\"motion\":\"sheet\"", record);
    }

    /// <summary>Drawn without a file, its image is a canvas, so a film's frames can go onto a phone one at a time.</summary>
    [Fact]
    public void TestAMockupWithoutAFileReturnsItsCanvas()
    {
        var result = Run("""
            const f = createCanvas(1280, 720); const x = f.getContext('2d'); x.fillStyle = '#2f8c8c'; x.fillRect(0, 0, 1280, 720);
            const m = Stage.mockup(null, { design: f, setting: 'phone', width: 800, height: 500 });
            log(`frame ${m.path} ${m.image.width}x${m.image.height}`);
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("frame null 800x500"));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "artifacts"), "mockup*"));
    }

    [Theory]
    [InlineData("Stage.mockup('artifacts/final_wall.png', { design: 'artifacts/final_a.png' })", "name the file as a mock-up")]
    [InlineData("Stage.mockup('artifacts/mockup_a.jpg', { design: 'artifacts/final_a.png' })", "writes a PNG")]
    [InlineData("Stage.mockup(null, { setting: 'wall' })", "needs a design")]
    [InlineData("Stage.mockup(null, { design: 'artifacts/final_a.png', size: 'B2' })", "'A0'–'A6'")]
    [InlineData("Stage.mockup(null, { design: 'artifacts/final_a.png', label: '' })", "always says it is a mock-up")]
    [InlineData("Stage.mockup(null, { design: 'artifacts/final_a.png', setting: 'onto', corners: [[0, 0], [10, 0], [10, 10]] })", "four points")]
    public void TestWhatCannotBeAMockupIsRefused(string call, string words)
    {
        var result = Run(call + ";");

        Assert.False(result.Success);
        Assert.Contains(words, result.Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "artifacts"), "mockup*"));
    }
}
