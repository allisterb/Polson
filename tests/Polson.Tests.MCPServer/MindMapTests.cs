namespace Polson.Tests.MCPServer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polson.Drawing.Skia;
using Polson.ExtendedMind.Taste;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// <c>Stage.mindMap</c>: the run's mind map (Manual 29 §2a), parsed from the outline an agent writes, its measured nodes
/// checked against the Taste answers they cite, and drawn. The answers are built here; no Qloo call is made.
/// </summary>
public class MindMapTests : TestsRuntime, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-map-" + Guid.NewGuid().ToString("N"));

    private const string Outline = """
        # Nightshift — cold brew, steeped twenty hours

        - Synonyms
          - slow
            - steeped
        - Antonyms
            - instant
        - Clichés
        	- steam rising
        - Stock phrases
          - "burning the midnight oil"
        - Audience A — Berlin 30–34
          - *Suspiria* (t1)
          - "Slow Burn" (`t1`)
          - *Blade Runner* (t1)
          - *Somebody Else* (t9)
          - Feierabend, the evening off (assumed)
        """;

    public MindMapTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void TestAnOutlineParsesWhateverItsIndentationAndKeepsWhereEachNodeCameFrom()
    {
        var map = MindMap.Parse(Outline);

        Assert.Equal("Nightshift — cold brew, steeped twenty hours", map.Text);
        Assert.Equal(["Synonyms", "Antonyms", "Clichés", "Stock phrases", "Audience A — Berlin 30–34"], map.Children.Select(b => b.Text));
        Assert.Equal(["instant"], map.Children[1].Children.Select(c => c.Text));          // four spaces nest as two do
        Assert.Equal(["steam rising"], map.Children[2].Children.Select(c => c.Text));     // and so does a tab
        Assert.Equal("Synonyms → slow → steeped", map.Leaves.First().Trail);

        var audience = map.Children[4].Children;
        Assert.Equal((MindMapKind.Measured, "t1", (bool?)false), (audience[0].Kind, audience[0].Handle, audience[0].IsTag));
        Assert.Equal(("Slow Burn", "t1", (bool?)true), (audience[1].Text, audience[1].Handle, audience[1].IsTag));
        Assert.Equal((MindMapKind.Assumed, "Feierabend, the evening off"), (audience[4].Kind, audience[4].Text));
        Assert.Equal(MindMapKind.Plain, map.Children[3].Children[0].Kind);
        Assert.Equal("burning the midnight oil", map.Children[3].Children[0].Text);
    }

    [Theory]
    [InlineData("- a\n- b", "'# ' heading")]
    [InlineData("# Only a centre\n\nSome prose.", "no branches")]
    public void TestAnOutlineWithoutACentreOrBranchesIsRefusedInWords(string outline, string words) =>
        Assert.Contains(words, Assert.Throws<ArgumentException>(() => MindMap.Parse(outline)).Message);

    /// <summary>
    /// A measured node is looked for in the answer it cites, as Taste.checkReferences looks for a direction's references:
    /// one the answer did not return, or one citing an answer this session never had, is listed and not counted as
    /// evidence.
    /// </summary>
    [Fact]
    public void TestMeasuredNodesAreCheckedAgainstTheirAnswersAndTheMapIsDrawn()
    {
        File.WriteAllText(Path.Combine(root, "mindmap.md"), Outline);
        var session = new SessionContext();
        session.Taste.Add(id => new TasteResult
        {
            Id = id, Op = "profile", Label = "audience A", Ok = true,
            Entities = [new Dictionary<string, object?> { ["id"] = "e-1", ["name"] = "Suspiria", ["type"] = "urn:entity:movie",
                ["tags"] = new object?[] { new Dictionary<string, object?> { ["name"] = "Slow burn" } } }],
        });

        var result = new JsDrawingEngine { ProjectRoot = root }.Execute("""
            const canvas = createCanvas(1200, 900), ctx = canvas.getContext('2d');
            const m = Stage.mindMap('mindmap.md', ctx);
            log(JSON.stringify({ b: m.branches.length, d: m.depth, n: m.leaves.length, me: m.measured, as: m.assumed, u: m.unverified }));
            log(m.leaves.filter(l => l.branch.startsWith('Audience')).map(l => l.kind + ':' + l.verified).join(' '));
            canvas;
            """, 1200, 900, session, "png", 90);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("\"b\":5,\"d\":3,\"n\":9,\"me\":4,\"as\":1"));   // centre → Synonyms → slow → steeped
        Assert.Contains(result.Logs, l => l.Contains("Blade Runner (not returned by t1)") && l.Contains("t9 is not an answer of this session"));
        Assert.Contains(result.Logs, l => l.Contains("measured:true measured:true measured:false measured:false assumed:null"));
        Assert.DoesNotContain(result.Logs, l => l.StartsWith("[WARN] layout", StringComparison.Ordinal));
        Assert.NotEmpty(result.ImageBytes!);
    }

    [Fact]
    public void TestAMapIsReadWithoutACanvasAndItsFileMustBeInTheProject()
    {
        File.WriteAllText(Path.Combine(root, "mindmap.md"), Outline);

        var read = new JsDrawingEngine { ProjectRoot = root }.Execute(
            "const m = Stage.mindMap('mindmap.md'); log(m.typeSize + ' ' + m.leaves[0].trail); createCanvas(8, 8);", 8, 8, null, "png", 90);
        var missing = new JsDrawingEngine { ProjectRoot = root }.Execute("Stage.mindMap('nope.md');", 8, 8, null, "png", 90);

        Assert.True(read.Success, read.Error);
        Assert.Contains(read.Logs, l => l.Contains("0 Synonyms → slow → steeped"));
        Assert.False(missing.Success);
        Assert.Contains("is not in the project", missing.Error);
    }
}
