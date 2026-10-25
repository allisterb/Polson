namespace Polson.Tests.CLI;

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Polson.CLI;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// The techniques a director requests or denies for one production: the catalogue's integrity, the flags, the section
/// they write into the instructions, and the report's check of a finished run against them.
/// </summary>
/// <remarks>
/// poster6 and nightshift3 (2026-10-25) both used what their workflows required and neither took the alternative grid
/// offered beside it: a request makes an option an instruction for one production, and a denial removes a default.
/// </remarks>
public class TechniquesTests : TestsRuntime, IDisposable
{
    #region Fields
    readonly string root = Path.Combine(Path.GetTempPath(), "polson-tech-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    CreateProjectOptions Options(string name, string workflow, string[]? request = null, string[]? deny = null) =>
        new() { Directory = root, Id = name, Sdk = "claude", Workflow = workflow, Prompt = "A test brief.", Request = request ?? [], Deny = deny ?? [] };

    static string[] Chosen(JsonNode? manifest, string list) =>
        [.. (manifest?["techniques"]?[list] as JsonArray ?? []).Select(n => n!.GetValue<string>())];

    JsonNode Manifest(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, name, "project.json")))!;
    #endregion

    #region Tests
    /// <summary>Every technique is whole, for a workflow that exists and carries the section, citing a manual that exists.</summary>
    [Fact]
    public void TestTheCatalogueIsWhole()
    {
        Assert.NotEmpty(Techniques.All);
        Assert.Equal(Techniques.All.Count, Techniques.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in Techniques.All)
        {
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", t.Id);
            Assert.NotEmpty(t.Workflows);
            Assert.All(t.Workflows, w => Assert.Contains(w, ProjectGenerator.KnownWorkflows));
            var e = t.Evidence;
            Assert.True(e.Scripts is not null || e.Artifacts is not null || e.Direction is not null || e.Judged is not null, $"{t.Id} has no evidence");
            foreach (var pattern in new[] { e.Scripts, e.Artifacts, e.Direction }.OfType<string>()) _ = new Regex(pattern);
            var manual = Regex.Match(t.Source, @"Manual (\d+)");
            Assert.True(manual.Success, $"{t.Id} cites no manual: {t.Source}");
            Assert.NotNull(PolsonManuals.Find(manual.Groups[1].Value));
        }
    }

    /// <summary>A workflow a technique applies to must carry the section, or a request would vanish from the instructions.</summary>
    [Fact]
    public void TestEveryWorkflowWithTechniquesCarriesTheSection()
    {
        foreach (var workflow in Techniques.All.SelectMany(t => t.Workflows).Distinct())
        {
            var t = Techniques.For(workflow)[0];
            Assert.True(ProjectGenerator.Create(Options($"carry-{workflow}", workflow, request: [t.Id])), workflow);
            var instructions = File.ReadAllText(Path.Combine(root, $"carry-{workflow}", "CLAUDE.md"));
            Assert.Contains("## Techniques the director chose for this production", instructions);
            Assert.Contains(t.Request, instructions);
            Assert.DoesNotContain("{{TECHNIQUES}}", instructions);
        }
    }

    [Fact]
    public void TestChoicesAreWrittenToTheInstructionsAndTheManifest()
    {
        Assert.True(ProjectGenerator.Create(Options("chosen", "motion", request: ["brand-language,alternative-grid"], deny: ["mystery-reveal"])));

        var manifest = Manifest("chosen");
        Assert.Equal(["alternative-grid", "brand-language"], Chosen(manifest, "request"));     // catalogue order
        Assert.Equal(["mystery-reveal"], Chosen(manifest, "deny"));
        var instructions = File.ReadAllText(Path.Combine(root, "chosen", "CLAUDE.md"));
        Assert.Contains("**Alternative grid** (Manual 30 §2)", instructions);
        Assert.Contains("**Do not use**", instructions);
        Assert.Contains("**Mystery → reveal** (Manual 25 §14a)", instructions);
    }

    [Fact]
    public void TestNoChoicesWriteNothing()
    {
        Assert.True(ProjectGenerator.Create(Options("plain", "poster")));

        Assert.Null(Manifest("plain")["techniques"]);
        Assert.DoesNotContain("Techniques the director chose", File.ReadAllText(Path.Combine(root, "plain", "CLAUDE.md")));
    }

    [Theory]
    [InlineData("motion", "alternative-grids", null, "no such technique")]
    [InlineData("logo", "matte-wipe", null, "is for motion, not 'logo'")]
    [InlineData("motion", "film-grain", "film-grain", "both requested and denied")]
    [InlineData("painting", "kit-of-parts", null, "has no techniques")]
    public void TestABadChoiceIsRefusedBeforeAnythingIsWritten(string workflow, string request, string? deny, string words)
    {
        var (choices, error) = Techniques.Parse(workflow, [request], deny is null ? [] : [deny]);

        Assert.Null(choices);
        Assert.Contains(words, error);
        Assert.False(ProjectGenerator.Create(Options("refused", workflow, [request], deny is null ? null : [deny])));
        Assert.False(Directory.Exists(Path.Combine(root, "refused")));
    }

    /// <summary>A reset keeps the choices, as it keeps the workflow; giving either flag again replaces them.</summary>
    [Fact]
    public void TestAResetKeepsTheChoicesUnlessGivenAgain()
    {
        Assert.True(ProjectGenerator.Create(Options("kept", "motion", request: ["matte-wipe"], deny: ["film-grain"])));

        var reset = Options("kept", "");
        reset.Reset = true;
        Assert.True(ProjectGenerator.Create(reset));
        Assert.Equal(["matte-wipe"], Chosen(Manifest("kept"), "request"));
        Assert.Equal(["film-grain"], Chosen(Manifest("kept"), "deny"));

        var replaced = Options("kept", "", deny: ["camera-move"]);
        replaced.Reset = true;
        Assert.True(ProjectGenerator.Create(replaced));
        Assert.Empty(Chosen(Manifest("kept"), "request"));
        Assert.Equal(["camera-move"], Chosen(Manifest("kept"), "deny"));
    }

    /// <summary>The report reads the run against the choices: used, not used, or judged — and a denied one used is a warning.</summary>
    [Fact]
    public void TestTheReportChecksTheRunAgainstTheChoices()
    {
        var dir = Path.Combine(root, "run");
        Directory.CreateDirectory(Path.Combine(dir, "scripts"));
        Directory.CreateDirectory(Path.Combine(dir, "artifacts"));
        File.WriteAllText(Path.Combine(dir, "scripts", "0007.js"), "const g = Layout.linesFrom(sketch, { count: 8 });\ncomp.noise({ size: 2 });");
        File.WriteAllText(Path.Combine(dir, "artifacts", "mockup_wall_a.png"), "x");
        File.WriteAllText(Path.Combine(dir, "direction.md"), "# Direction\n\n## Brand Language\nThe vocabulary.");
        var choices = new Techniques.Choices(["alternative-grid", "brand-language", "mockups", "kit-of-parts", "mystery-reveal"], ["film-grain", "matte-wipe"]);

        var outcomes = Techniques.Check(dir, choices).ToDictionary(o => o.Id);

        Assert.Equal("used", outcomes["alternative-grid"].Status);
        Assert.Contains("Layout.linesFrom(", outcomes["alternative-grid"].Detail);
        Assert.Equal("used", outcomes["brand-language"].Status);
        Assert.Equal("used", outcomes["mockups"].Status);
        Assert.Equal("not used", outcomes["kit-of-parts"].Status);
        Assert.Equal("judged", outcomes["mystery-reveal"].Status);
        Assert.Equal(("used", false), (outcomes["film-grain"].Status, outcomes["film-grain"].Requested));
        Assert.Equal("not used", outcomes["matte-wipe"].Status);
    }

    [Fact]
    public void TestTheRunReportCarriesTheChecksAndTheirWarnings()
    {
        Assert.True(ProjectGenerator.Create(Options("reported", "motion", request: ["kit-of-parts"], deny: ["film-grain"])));
        var dir = Path.Combine(root, "reported");
        File.WriteAllText(Path.Combine(dir, "scripts", "0001.js"), "comp.noise({ size: 2 });");

        var writer = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(writer);
        try { Assert.Equal(0, RunReport.Run(new ReportOptions { ProjectDir = dir, Json = true })); }
        finally { Console.SetOut(previous); }
        var report = JsonNode.Parse(writer.ToString())!;

        Assert.Equal(2, (report["techniques"] as JsonArray)!.Count);
        var warnings = (report["warnings"] as JsonArray)!.Select(w => w!.ToString()).ToArray();
        Assert.Contains(warnings, w => w.StartsWith("Requested technique not found: Kit of parts", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("Denied technique used: Film grain", StringComparison.Ordinal));
    }
    #endregion
}
