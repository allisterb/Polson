namespace Polson.Tests.Drawing;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Polson.Drawing.Skia;
using Xunit;

/// <summary>
/// <c>Equation.equivalent</c>: identities proved by simplification, refuted by a counterexample, agreed numerically where
/// neither settles them, held to a domain where one is given, and refused before anything recurses when too deep.
/// </summary>
public class EquationEquivalentTests : TestsRuntime
{
    #region Methods
    private static Dictionary<string, object?> Check(string a, string? b = null, object? options = null) =>
        new EquationToolkit().Equivalent(a, b, options);

    [Theory]
    [InlineData(@"(x+1)^2", @"x^2+2x+1")]
    [InlineData(@"\sin^2 x + \cos^2 x", @"1")]
    [InlineData(@"\sin(2x)", @"2\sin x \cos x")]                      // \sin x \cos x is sin(x)·cos(x)
    [InlineData(@"\int_0^2 \frac{x^2}{2} dx", @"\frac{4}{3}")]          // an italic dx, as agents write it
    [InlineData(@"\frac{d}{dx} x^3", @"3x^2")]
    [InlineData(@"e^{i\pi} + 1", @"0")]
    [InlineData(@"(a+b)^2 = a^2 + 2ab + b^2", null)]                         // one argument: an equation that holds
    [InlineData(@"\int_0^2 \frac{x^2}{2}\,dx", @"\left[\frac{x^3}{6}\right]_0^2")]    // the evaluation bracket
    public void TestAnIdentityIsProved(string a, string b)
    {
        var r = Check(a, b);
        Assert.True((string)r["verdict"]! == "proved", $"{a} = {b}: {r["verdict"]}, {r["detail"]}");
    }

    [Theory]
    [InlineData(@"(x+1)^2", @"x^2+2x+2")]
    [InlineData(@"\int_0^1 x^2 dx", @"\frac{1}{4}")]
    [InlineData(@"(a+b)^2 = a^2 + b^2", null)]                              // one argument: an equation that does not hold
    [InlineData(@"\int_0^2 \frac{x^2}{2}\,dx", @"\left[\frac{x^3}{2}\right]_0^2")]   // integrated wrongly
    public void TestAWrongStepDiffers(string a, string? b)
    {
        var r = Check(a, b);
        Assert.Equal("differs", r["verdict"]);
    }

    /// <summary>The perturbed formulas from the read-back probe each differ from the famous one, with a counterexample.</summary>
    [Theory]
    [InlineData(@"\frac{-b + \sqrt{b^2 - 4ac}}{2a}", @"\frac{-b + \sqrt{b^2 - 5ac}}{2a}")]
    [InlineData(@"\sum_{k=1}^{n} k", @"\frac{n(n+2)}{2}")]
    [InlineData(@"\frac{a^2 - b^2}{a - b}", @"a - b")]
    public void TestAPerturbedFormulaDiffersAtAPoint(string a, string b)
    {
        var r = Check(a, b, Opts(("domain", Opts(("a", new[] { 1d, 3d }), ("b", new[] { 0.1d, 0.4d }), ("c", new[] { 0.1d, 0.4d })))));
        Assert.Equal("differs", r["verdict"]);
        var at = Assert.IsType<Dictionary<string, object?>>(r["at"]);
        Assert.NotEmpty(at);
        var values = Assert.IsType<object?[]>(r["values"]);
        Assert.NotEqual(values[0], values[1]);
    }

    /// <summary>
    /// Two equations are a step of a solution when lhs − rhs of one is a constant multiple of the other's; squaring away a
    /// root, or a different answer, is not.
    /// </summary>
    [Fact]
    public void TestEquationsAreEquivalentWhenOneIsAMultipleOfTheOther()
    {
        Assert.Equal("proved", Check("2x + 3 = 7", "2x = 4")["verdict"]);
        Assert.Equal("proved", Check("2x = 4", "x = 2")["verdict"]);
        Assert.Equal("proved", Check("x^2 - 4 = 0", "(x-2)(x+2) = 0")["verdict"]);
        Assert.Equal("differs", Check("2x = 4", "x = 3")["verdict"]);
        var lost = Check("x^2 = 4", "x = 2");
        Assert.Equal("differs", lost["verdict"]);
        Assert.Contains("not equivalent", (string)lost["detail"]!);
        Assert.Equal(("unread", "b"), ((string)Check("x = 2", "x + 2")["verdict"]!, (string)Check("x = 2", "x + 2")["side"]!));
    }

    /// <summary>A sum's bound is drawn as an integer: Gauss's formula agrees, or proves, and one off by n does not.</summary>
    [Fact]
    public void TestASumIsCheckedOverWholeNumbers()
    {
        var r = Check(@"\sum_{k=1}^{n} k = \frac{n(n+1)}{2}");
        Assert.True((string)r["verdict"]! is "proved" or "agrees",
            $"{r["verdict"]}: {r["detail"]} ({r["a"]} | {r["b"]}) at {string.Join(",", ((IDictionary<string, object?>?)r.GetValueOrDefault("at"))?.Select(p => $"{p.Key}={p.Value}") ?? [])} values {string.Join(" vs ", (object?[]?)r.GetValueOrDefault("values") ?? [])}");
        Assert.Equal(("∑_(k = 1)ⁿ k", "(n(n + 1))/2"), ((string)r["a"]!, (string)r["b"]!));      // split at its own =, not k = 1
        Assert.Equal("differs", Check(@"\sum_{k=1}^{n} k = \frac{n^2}{2}")["verdict"]);
    }

    /// <summary>
    /// Identities that hold only on part of the domain differ by default, with the point where they fail, and hold once the
    /// domain says where they are used. A cancelled factor is proved with its condition.
    /// </summary>
    [Fact]
    public void TestADomainDecidesAConditionalIdentity()
    {
        var everywhere = Check(@"\sqrt{x^2}", @"x");
        Assert.Equal("differs", everywhere["verdict"]);
        Assert.True((double)((Dictionary<string, object?>)everywhere["at"]!)["x"]! < 0);
        Assert.Equal("agrees", Check(@"\sqrt{x^2}", @"x", Opts(("domain", Opts(("x", new[] { 0d, 5d })))))["verdict"]);
        Assert.Equal("agrees", Check(@"\ln(ab)", @"\ln a + \ln b", Opts(("domain", Opts(("a", new[] { 0.1d, 9d }), ("b", new[] { 0.1d, 9d })))))["verdict"]);

        var cancelled = Check(@"\frac{x^2-1}{x-1}", @"x+1");
        Assert.Equal("proved", cancelled["verdict"]);
        Assert.Contains("x", (string)cancelled["provided"]!);
    }

    /// <summary>A Riemann sum is not the integral, and a numerical integral agrees with its exact value.</summary>
    [Fact]
    public void TestConstantsAreComparedAsNumbers()
    {
        Assert.Equal("differs", Check(@"\sum_{k=1}^{10} \frac{1}{10}\left(\frac{k-1}{10}\right)^2", @"\frac{1}{3}")["verdict"]);
        Assert.Equal("proved", Check(@"\sum_{k=1}^{10} \frac{1}{10}\left(\frac{k-1}{10}\right)^2", @"0.285")["verdict"]);
        var r = Check(@"\int_0^1 e^{-x^2} dx", @"0.746824132812427");
        Assert.Contains((string)r["verdict"]!, new[] { "proved", "agrees" });
    }

    /// <summary>What cannot be read says which side and why; what is too deep is refused before anything recurses.</summary>
    [Fact]
    public void TestBadInputIsUnreadAndDeepInputIsRefusedFirst()
    {
        var bad = Check(@"\frac{1}{2", @"x");
        Assert.Equal(("unread", "a"), ((string)bad["verdict"]!, (string)bad["side"]!));
        Assert.Equal("unread", Check(@"x + 1")["verdict"]);                 // one argument must be an equation

        var parens = new string('(', 60) + "x" + new string(')', 60);
        var deep = Check(parens, "x");
        Assert.Equal("unread", deep["verdict"]);
        Assert.Contains("nest", (string)deep["detail"]!);
        Assert.Contains("characters", (string)Check(string.Join("+", Enumerable.Repeat("x", 400)), "400x")["detail"]!);

        // A long but legal chain is within the length bound and is settled on its own stack.
        var chain = Check(string.Join("+", Enumerable.Repeat("x", 250)), "250x");
        Assert.Equal("proved", chain["verdict"]);
    }

    /// <summary>The same check draws the same points, so a verdict and its counterexample can be re-run.</summary>
    [Fact]
    public void TestAVerdictIsReproducible()
    {
        var first = Check(@"\sqrt{x^2}", @"x");
        var second = Check(@"\sqrt{x^2}", @"x");
        Assert.Equal(((Dictionary<string, object?>)first["at"]!)["x"], ((Dictionary<string, object?>)second["at"]!)["x"]);
    }

    /// <summary>A slow simplification is stopped at its budget rather than holding the script.</summary>
    [Fact]
    public void TestABudgetBoundsTheWait()
    {
        var watch = Stopwatch.StartNew();
        Check(@"(a+b+c+d)^{30}", @"(d+c+b+a)^{30}", Opts(("budget", 600d)));
        Assert.True(watch.ElapsedMilliseconds < 600 + 2000 + 2500, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TestOptionsAreChecked()
    {
        Assert.Throws<ArgumentException>(() => Check("x", "x", Opts(("domains", Opts()))));
        Assert.Throws<ArgumentException>(() => Check("x", "x", Opts(("domain", Opts(("x", new[] { 5d, 1d }))))));
    }

    private static Hashtable Opts(params (string Key, object? Value)[] pairs)
    {
        var h = new Hashtable();
        foreach (var (k, v) in pairs) h[k] = v;
        return h;
    }
    #endregion
}
