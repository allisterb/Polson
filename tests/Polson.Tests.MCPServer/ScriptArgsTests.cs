namespace Polson.Tests.MCPServer;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// Running one script as several variants with <c>ExecuteScript</c>'s <c>args</c>.
/// </summary>
/// <remarks>
/// trailer5 ran one <c>trailer.js</c> about twenty times and switched audience, mode and part with a
/// <c>sed</c> edit to a constant before each run. <c>args</c> replaces the edit, and the record has to
/// say which variant ran, since <c>scripts/</c> then holds the same text for every one of them.
/// </remarks>
public class ScriptArgsTests : TestsRuntime, IDisposable
{
    #region Fields
    private readonly string root = Path.Combine(Path.GetTempPath(), "polson-scriptargs-" + Guid.NewGuid().ToString("N"));
    #endregion

    #region Methods
    public ScriptArgsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private DrawingMcpTools Tools() => new(null, null, null, root);

    private JsonElement[] OfType(string type)
    {
        var file = Path.Combine(root, "events", "server.jsonl");
        if (!File.Exists(file)) return [];

        return [.. File.ReadAllLines(file)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == type)];
    }
    #endregion

    #region Reading ARGS
    [Fact]
    public async Task TestArgsAreVisibleToTheScript()
    {
        var result = await Tools().ExecuteScript(
            "log(ARGS.audience + ' ' + ARGS.part + ' ' + ARGS.films.length + ' ' + ARGS.opts.loud);",
            args: """{"audience":"b","part":2,"films":["x","y"],"opts":{"loud":true}}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("b 2 2 true"));
    }

    /// <summary><c>ARGS</c> is always defined, so a file that reads it still runs with no args.</summary>
    [Fact]
    public async Task TestAbsentArgsAreAnEmptyObject()
    {
        var result = await Tools().ExecuteScript(
            "log(JSON.stringify(ARGS) + ' ' + (ARGS.audience ?? 'a'));");

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("{} a"));
    }

    /// <summary>A plain JS object, not a CLR one: ordinary JS semantics, nothing behind it.</summary>
    [Fact]
    public async Task TestArgsAreAPlainJsObject()
    {
        var result = await Tools().ExecuteScript(
            "log((Object.getPrototypeOf(ARGS) === Object.prototype) + ' ' + ('missing' in ARGS) + ' ' + Object.keys(ARGS).join(','));",
            args: """{"mode":"encode"}""");

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("true false mode"));
    }

    [Fact]
    public async Task TestArgsDoNotCarryOverToTheNextCall()
    {
        var tools = Tools();
        await tools.ExecuteScript("1;", args: """{"audience":"b"}""");
        var result = await tools.ExecuteScript("log(ARGS.audience ?? 'none');");

        Assert.Contains(result.Logs, l => l.Contains("none"));
    }
    #endregion

    #region Refusals
    [Theory]
    [InlineData("{audience: 'b'}")]
    [InlineData("{\"f\": function () {}}")]
    [InlineData("{\"x\": undefined}")]
    [InlineData("not json")]
    public async Task TestNonJsonArgsAreRefusedByName(string args)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Tools().ExecuteScript("1;", args: args));

        Assert.Contains("'args'", ex.Message);
        Assert.Empty(OfType("script.start"));
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"b\"")]
    [InlineData("2")]
    public async Task TestArgsMustBeAnObject(string args)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Tools().ExecuteScript("1;", args: args));

        Assert.Contains("'args' must be a JSON object", ex.Message);
    }

    [Fact]
    public async Task TestOversizedArgsAreRefused()
    {
        var args = $$"""{"pad":"{{new string('x', DrawingMcpTools.MaxScriptArgsLength)}}"}""";

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Tools().ExecuteScript("1;", args: args));

        Assert.Contains("'args'", ex.Message);
        Assert.Contains("limit", ex.Message);
    }
    #endregion

    #region Record
    [Fact]
    public async Task TestArgsAreRecordedOnScriptStart()
    {
        File.WriteAllText(Path.Combine(root, "trailer.js"), "log(ARGS.audience);");

        await Tools().ExecuteScript(scriptFile: "trailer.js", args: """{"audience":"b","part":2}""");

        var recorded = Assert.Single(OfType("script.start")).GetProperty("args");
        Assert.Equal(JsonValueKind.Object, recorded.ValueKind);
        Assert.Equal("b", recorded.GetProperty("audience").GetString());
        Assert.Equal(2, recorded.GetProperty("part").GetInt32());
    }

    [Fact]
    public async Task TestNoArgsRecordsNone()
    {
        await Tools().ExecuteScript("1;");

        Assert.False(Assert.Single(OfType("script.start")).TryGetProperty("args", out _));
    }
    #endregion
}
