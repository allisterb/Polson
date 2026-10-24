namespace Polson.Tests.MCPServer;

using Polson.MCPServer;
using Xunit;

/// <summary>
/// What a script that awaits renders: what its last line names, as a script that does not await does; and when it names
/// nothing, the last surface it made, with a warning when that may be the wrong one.
/// </summary>
/// <remarks>
/// uberpitch1 (2026-10-24) built a contact sheet from a scratch canvas, ended with a bare <c>RESULT;</c> in a script that
/// awaited, and was shown the scratch canvas, silently — then spent its time debugging scaling that worked.
/// </remarks>
public class ResultFallbackTests : TestsRuntime
{
    [Theory]
    [InlineData("sheet;")]
    [InlineData("sheet")]
    [InlineData("    sheet;   // the contact sheet")]
    [InlineData("log('drawn'); sheet;")]
    public void TestAnAwaitingScriptsTrailingNameIsItsResult(string last)
    {
        var result = new JsDrawingEngine().Execute($"""
            await Promise.resolve(1);
            const sheet = createCanvas(40, 30);
            const scratch = createCanvas(20, 20);
            {last}
            """, 40, 40, null, "png", 90);

        Assert.True(result.Success, result.Error);
        Assert.Equal((40, 30), (result.ResultCanvas!.Width, result.ResultCanvas.Height));
        Assert.Null(JsDrawingEngine.FallbackWarning(result));
    }

    [Fact]
    public void TestAnAwaitingScriptThatNamesNothingFromSeveralSurfacesIsWarned()
    {
        var result = new JsDrawingEngine().Execute("""
            await Promise.resolve(1);
            const sheet = createCanvas(40, 40);
            const scratch = createCanvas(20, 20);
            log('done');
            """, 40, 40, null, "png", 90);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.FallbackSurfaces);
        Assert.StartsWith("[WARN] This script uses await and returned nothing", JsDrawingEngine.FallbackWarning(result));
    }

    [Theory]
    [InlineData("await Promise.resolve(1); const sheet = createCanvas(40, 40); const scratch = createCanvas(20, 20); return sheet;")]
    [InlineData("await Promise.resolve(1); const sheet = createCanvas(40, 40); log('one');")]
    [InlineData("const sheet = createCanvas(40, 40); const scratch = createCanvas(20, 20); log('sync');")]
    public void TestAReturnedSurfaceALoneOneOrAScriptThatDoesNotAwaitIsNotWarned(string script)
    {
        var result = new JsDrawingEngine().Execute(script, 40, 40, null, "png", 90);

        Assert.True(result.Success, result.Error);
        Assert.Null(JsDrawingEngine.FallbackWarning(result));
    }

    [Theory]
    [InlineData("const a = 1;\nreturn a;")]
    [InlineData("x = 1;\n}")]
    [InlineData("doIt();")]
    [InlineData("const a = 1;\nreturn;")]
    [InlineData("if (ok) sheet;")]
    [InlineData("log('a; b')")]
    public void TestOnlyALoneTrailingNameIsRewritten(string script) =>
        Assert.Equal(script, JsDrawingEngine.ReturnTrailingName(script));
}
