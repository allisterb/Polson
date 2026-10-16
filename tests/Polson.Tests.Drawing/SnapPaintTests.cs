namespace Polson.Tests.Drawing;

using System;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// A fill or stroke that is not a colour is refused, rather than drawn black.
/// </summary>
/// <remarks>
/// The poster2 run passed an object where a colour belonged and its title rendered black, with nothing to say why.
/// </remarks>
public class SnapPaintTests : TestsRuntime
{
    #region Methods
    private static DrawingExecutionResult Run(string script) =>
        new JsDrawingEngine().Execute(script, 200, 100, null, "png", 100, render: false);

    [Theory]
    [InlineData("['#f5d33f', 'title']")]
    [InlineData("{ colour: '#f5d33f' }")]
    [InlineData("'yelow'")]
    [InlineData("'#12345'")]
    public void TestANonColourFillIsRefusedWithTheFix(string fill)
    {
        var result = Run($"const p = Snap(200, 100); p.rect(0, 0, 50, 50).attr({{ fill: {fill} }}); p;");

        Assert.False(result.Success);
        Assert.Contains("is not a colour", result.Error);
        Assert.Contains("'#rrggbb'", result.Error);
    }

    [Theory]
    [InlineData("'#f5d33f'")]
    [InlineData("'#fff'")]
    [InlineData("'rgb(10, 20, 30)'")]
    [InlineData("'hsl(200, 50%, 40%)'")]
    [InlineData("'rebeccapurple'")]
    [InlineData("'none'")]
    [InlineData("'currentColor'")]
    public void TestColoursAndPaintKeywordsAreAccepted(string fill)
    {
        var result = Run($"const p = Snap(200, 100); p.rect(0, 0, 50, 50).attr({{ fill: {fill}, stroke: {fill} }}); p;");

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void TestAGradientIsStillAPaint()
    {
        var result = Run("const p = Snap(200, 100); const g = p.gradient('l(0, 0, 1, 0)#000-#fff'); p.rect(0, 0, 50, 50).attr({ fill: g }); p;");

        Assert.True(result.Success, result.Error);
    }
    #endregion
}
