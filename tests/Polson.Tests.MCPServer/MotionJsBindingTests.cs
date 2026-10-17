namespace Polson.Tests.MCPServer;

using Polson.MCPServer;
using SkiaSharp;
using Xunit;

/// <summary>
/// How the composition's calls bind from JavaScript, which the C# tests of the same calls cannot show: Jint picks an
/// overload by the arguments' JavaScript shapes.
/// </summary>
public class MotionJsBindingTests : TestsRuntime
{
    #region Methods
    private static DrawingExecutionResult Execute(string body) =>
        new JsDrawingEngine().Execute($"const comp = Motion.composition({{ width: 40, height: 40 }}); {body} comp.render(0);", 40, 40, null, "png", 90);

    /// <summary>
    /// <c>comp.drawn(fn)</c>, its options left out as the docs allow, draws. It threw "takes a function first": a lone
    /// function arrives as a <c>Func</c>, and the overload that catches a missing function took any <c>object</c>.
    /// </summary>
    [Fact]
    public void ADrawnLayerTakesItsFunctionAlone()
    {
        var result = Execute("comp.drawn((ctx, v, t) => { ctx.fillStyle = '#ff0000'; ctx.fillRect(0, 0, 20, 20); });");

        Assert.True(result.Success, result.Error);
        using var frame = SKBitmap.Decode(result.ImageBytes);
        Assert.Equal(SKColors.Red, frame.GetPixel(10, 10));
    }

    /// <summary><c>comp.text</c> returns a handle whose boxes read in JavaScript as plain objects.</summary>
    [Fact]
    public void ATextReportsItsBoundsToTheScript()
    {
        var result = Execute("""
            const title = comp.text({ text: 'Hi', family: 'Arial', size: 20, origin: [20, 20] });
            const b = title.boundsAt(0);
            const letters = title.lettersAt(0);
            log(`bounds ${b.width > 0 && b.height > 0} letters ${letters.length} first ${letters[0].text} words ${title.text}`);
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("bounds true letters 2 first H words Hi", System.StringComparison.Ordinal));
    }

    /// <summary>Options with no function before them are still refused with what to write.</summary>
    [Fact]
    public void ADrawnLayerWithNoFunctionIsRefusedWithWhatToWrite()
    {
        var result = Execute("comp.drawn({ values: {} });");

        Assert.False(result.Success);
        Assert.Contains("takes a function first", result.Error);
    }
    #endregion
}
