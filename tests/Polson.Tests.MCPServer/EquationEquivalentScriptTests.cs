namespace Polson.Tests.MCPServer;

using System.Text.Json;

using Polson.MCPServer;
using Xunit;

/// <summary>
/// <c>Equation.equivalent</c> from a script: options arrive as JavaScript objects and arrays, and the verdict goes back
/// as one. The C# tests pass a Hashtable; a script does not.
/// </summary>
public class EquationEquivalentScriptTests : TestsRuntime
{
    #region Methods
    private static JsonElement Run(string call)
    {
        var result = new JsDrawingEngine().Execute($"const c = createCanvas(4, 4); log(JSON.stringify({call})); c;", 4, 4, null, "png", 90);
        Assert.True(result.Success, result.Error);
        return JsonDocument.Parse(result.Logs.Last(l => l.StartsWith("[LOG] "))[6..]).RootElement;
    }

    [Fact]
    public void TestADomainGivenInJavaScriptDecidesTheVerdict()
    {
        Assert.Equal("differs", Run(@"Equation.equivalent('\\sqrt{x^2}', 'x')").GetProperty("verdict").GetString());
        var held = Run(@"Equation.equivalent('\\sqrt{x^2}', 'x', { domain: { x: [0, 5] }, samples: 12 })");
        Assert.Equal("agrees", held.GetProperty("verdict").GetString());
        Assert.Equal(12, held.GetProperty("points").GetInt32());
    }

    [Fact]
    public void TestAnEquationAndItsCounterexampleReachTheScript()
    {
        var r = Run(@"Equation.equivalent('(a+b)^2 = a^2 + b^2')");
        Assert.Equal("differs", r.GetProperty("verdict").GetString());
        Assert.Equal("(a + b)²", r.GetProperty("a").GetString());
        Assert.True(r.GetProperty("at").TryGetProperty("a", out _));
        Assert.Equal(2, r.GetProperty("values").GetArrayLength());
        Assert.Equal("proved", Run(@"Equation.equivalent('\\int_0^2 \\frac{x^2}{2}\\,dx', '\\frac{4}{3}')").GetProperty("verdict").GetString());
    }

    /// <summary>
    /// A key that does not apply is null, never absent: absent, Jint resolved <c>r.values</c> to the dictionary's own
    /// Values property, and a proved check showed a list of every value in the result (2026-10-23).
    /// </summary>
    [Fact]
    public void TestAKeyThatDoesNotApplyIsNull()
    {
        var r = Run(@"Equation.equivalent('8/6', '4/3')");
        Assert.Equal("proved", r.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("values").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("at").ValueKind);
        Assert.Equal(JsonValueKind.Null, Run(@"Equation.equivalent('8/6', '3/4')").GetProperty("at").ValueKind);   // a constant has no point
    }
    #endregion
}
