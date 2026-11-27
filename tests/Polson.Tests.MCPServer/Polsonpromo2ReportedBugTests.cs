namespace Polson.Tests.MCPServer;

using System.Linq;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// Defects reported by the <c>polsonpromo2</c> run (a 150-second narrated promo, 2026-11-25), each reproduced before it
/// was fixed.
/// </summary>
public class Polsonpromo2ReportedBugTests : TestsRuntime
{
    #region Methods
    /// <summary>
    /// Jint 4.9.2's <c>concat</c> throws a bare NullReferenceException on an array made by <c>flat</c> or <c>flatMap</c>,
    /// as receiver or argument, await or none: <c>SCENES.flatMap(...).concat(...)</c> cost four runs. Both now return a
    /// copy, which concat takes.
    /// </summary>
    [Theory]
    [InlineData("[[1],[2]].flat().concat([3])", "1,2,3")]
    [InlineData("[[1],[2]].flatMap(x => x).concat([3])", "1,2,3")]
    [InlineData("[3].concat([[1],[2]].flatMap(x => x))", "3,1,2")]
    [InlineData("[[1,[2]],[3]].flat(2).concat([4])", "1,2,3,4")]
    [InlineData("[1,2].flatMap(function (x) { return [x * this.k]; }, { k: 10 }).concat([0])", "10,20,0")]
    public void AFlattenedArrayConcatenates(string expression, string expected)
    {
        var result = new JsDrawingEngine().Execute($"log(({expression}).join(','));", 20, 20, null, "png", 90);

        Assert.True(result.Success, result.Error);
        Assert.Equal("[LOG] " + expected, Assert.Single(result.Logs));
    }

    /// <summary>A fault inside the engine names the script's line, as a JavaScript error does.</summary>
    [Fact]
    public void AnEngineFaultIsPlaced()
    {
        // A host function's FormatException has no JavaScript position of its own.
        var result = new JsDrawingEngine().Execute("""
            const a = 1;
            log('drawn');
            const c = createCanvas('wide', 10);
            """, 20, 20, null, "png", 90);

        Assert.False(result.Success);
        Assert.Contains("(line 3: `const c = createCanvas('wide', 10);`)", result.Error);
        Assert.Equal("[LOG] drawn", Assert.Single(result.Logs));
    }

    /// <summary>
    /// A composition's frame inside an <c>if</c> is not the result of a script that awaits, so the render fell back to the
    /// script's only canvas, a scratch one for measuring text: an 86-byte image and no warning.
    /// </summary>
    [Fact]
    public void AnUnreturnedFrameOfACompositionIsWarned()
    {
        var result = new JsDrawingEngine().Execute("""
            await Promise.resolve(1);
            const measure = createCanvas(4, 4).getContext('2d');
            const comp = Motion.composition({ width: 64, height: 36, fps: 12, duration: 1 });
            comp.fill({ color: '#123456' });
            if (true) { comp.render(0.5); }
            """, 64, 36, null, "png", 90);

        Assert.True(result.Success, result.Error);
        Assert.StartsWith("[WARN] This script uses await, built a composition", JsDrawingEngine.FallbackWarning(result));

        var returned = new JsDrawingEngine().Execute("""
            await Promise.resolve(1);
            const measure = createCanvas(4, 4).getContext('2d');
            const comp = Motion.composition({ width: 64, height: 36, fps: 12, duration: 1 });
            comp.fill({ color: '#123456' });
            if (true) { return comp.render(0.5); }
            """, 64, 36, null, "png", 90);

        Assert.True(returned.Success, returned.Error);
        Assert.Null(JsDrawingEngine.FallbackWarning(returned));
        Assert.Equal((64, 36), (returned.ResultCanvas!.Width, returned.ResultCanvas.Height));
    }
    #endregion
}
