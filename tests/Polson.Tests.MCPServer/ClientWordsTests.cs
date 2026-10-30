namespace Polson.Tests.MCPServer;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// A logo's words are read off what it drew and held against the brief, by the server.
/// </summary>
/// <remarks>
/// polson_graphics_studio_logo (2026-10-30) passed <c>tagline: ''</c> as told, then set "DETERMINISTIC VECTOR &amp; MOTION
/// RUNTIME" under its mark with <c>paper.text</c> and a descriptor on a mock-up with <c>fillText</c>. None of it was in
/// the brief, and nothing said so.
/// </remarks>
public class ClientWordsTests : TestsRuntime, IDisposable
{
    #region Constants
    private const string Brief = "# Brief\n\nBRIEF-BEGIN\nA logo for Polson Graphics Studio, an agentic design studio.\nBRIEF-END\n\n"
        + "| Tagline or descriptor, exactly as the client wrote it (or *none given*) | Autonomous Motion Runtime |\n";
    #endregion

    #region Fields
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-words-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public ClientWordsTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        File.WriteAllText(Path.Combine(root, "brief.md"), Brief);
        Manifest("logo");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The run's own words under the mark fail a check, and the table the agent fills in is not the brief.</summary>
    [Fact]
    public async Task TestAWordNotInTheBriefOnTheMarkFailsACheckTheServerRecords()
    {
        var result = await Tools().ExecuteScript(
            "const p = Snap(400, 200); p.text(20, 60, 'Polson Graphics Studio'); p.text(20, 150, 'DETERMINISTIC VECTOR & MOTION RUNTIME'); p;",
            400, 200, outSvg: "artifacts/final.svg");

        Assert.True(result.Success, result.Error);
        var check = Assert.Single(result.Logs, l => l.StartsWith("[CHECK] FAIL Client's words on artifacts/final.svg", StringComparison.Ordinal));
        Assert.Contains("DETERMINISTIC, VECTOR, MOTION, RUNTIME", check);
        Assert.DoesNotContain("Polson", check);
        var log = File.ReadAllText(Path.Combine(root, "events", "server.jsonl"));
        Assert.Contains("\"claim\":\"Client's words on artifacts/final.svg: every word is the brief's\",\"passed\":false", log);
    }

    /// <summary>The client's name alone passes, set a letter at a time or not, and is recorded as passing.</summary>
    [Fact]
    public async Task TestTheClientsNameAlonePasses()
    {
        var result = await Tools().ExecuteScript(
            "const c = createCanvas(300, 100); const x = c.getContext('2d'); x.font = '24px sans-serif'; "
            + "x.fillText('POLSON', 10, 40); for (const [i, l] of [...'GRAPHICS'].entries()) x.fillText(l, 10 + i * 20, 80); c;",
            300, 100, outFile: "artifacts/final.png");

        Assert.True(result.Success, result.Error);
        Assert.Single(result.Logs, l => l.StartsWith("[CHECK] PASS Client's words on artifacts/final.png", StringComparison.Ordinal));
    }

    /// <summary>A brand sheet's own labels and measurements are not reported; a line about the client is, as a warning.</summary>
    [Fact]
    public async Task TestABrandSheetWarnsOfAWordNotInTheBriefAndPassesItsLabelsDigits()
    {
        var result = await Tools().ExecuteScript(
            "const c = createCanvas(600, 300); const x = c.getContext('2d'); x.font = '16px sans-serif'; "
            + "x.fillText('Polson Graphics Studio', 20, 40); x.fillText('#0A0F1E 24px', 20, 80); x.fillText('Autonomous Vector Runtime', 20, 120); c;",
            600, 300, outFile: "artifacts/brand_sheet.png");

        Assert.True(result.Success, result.Error);
        var warning = Assert.Single(result.Logs, l => l.StartsWith("[WARN] words:", StringComparison.Ordinal));
        Assert.Contains("Autonomous, Vector, Runtime", warning);
        Assert.DoesNotContain("0A0F1E", warning);
        Assert.DoesNotContain(result.Logs, l => l.StartsWith("[CHECK]", StringComparison.Ordinal));
        Assert.Contains("render.words", File.ReadAllText(Path.Combine(root, "events", "server.jsonl")), StringComparison.Ordinal);
    }

    /// <summary>A mock-up's design is read too, as it is composed.</summary>
    [Fact]
    public async Task TestAMockupsWordsNotInTheBriefAreWarned()
    {
        var result = await Tools().ExecuteScript(
            "const d = createCanvas(340, 220); const x = d.getContext('2d'); x.font = '18px sans-serif'; "
            + "x.fillText('Polson Graphics Studio', 10, 40); x.fillText('Deterministic Pipeline', 10, 80); "
            + "Stage.mockup('artifacts/mockup_card.png', { design: d, setting: 'card' }); d;",
            340, 220);

        Assert.True(result.Success, result.Error);
        var warning = Assert.Single(result.Logs, l => l.StartsWith("[WARN] words: artifacts/mockup_card.png", StringComparison.Ordinal));
        Assert.Contains("Deterministic, Pipeline", warning);
    }

    /// <summary>Another workflow's final is not held to a logo's rule: a poster sets words of its own by design.</summary>
    [Fact]
    public async Task TestAnotherWorkflowsFinalIsNotChecked()
    {
        Manifest("poster");
        var result = await Tools().ExecuteScript(
            "const p = Snap(400, 200); p.text(20, 60, 'The music business is murder.'); p;", 400, 200, outSvg: "artifacts/final.svg");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(result.Logs, l => l.Contains("Client's words", StringComparison.Ordinal));
    }

    /// <summary>The agent cannot write the verdict itself.</summary>
    [Fact]
    public async Task TestStageCheckRefusesToWriteTheVerdict()
    {
        var result = await Tools().ExecuteScript("Stage.check(\"Client's words on artifacts/final.svg: every word is the brief's\", true);");

        Assert.False(result.Success);
        Assert.Contains("recorded by the server", result.Error);
    }

    private void Manifest(string workflow) =>
        File.WriteAllText(Path.Combine(root, "project.json"), $"{{ \"id\": \"w\", \"workflow\": \"{workflow}\" }}");

    private DrawingMcpTools Tools() => new(null, null, null, root);
    #endregion
}
