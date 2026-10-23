namespace Polson.Tests.Drawing;

using System;
using System.IO;
using System.Linq;
using Polson.Drawing.Skia;
using Xunit;

/// <summary>
/// Equations from LaTeX: parsed and typeset by CSharpMath, drawn through our own canvas, refused before parsing when too
/// long or too deep, and recorded as the line a reader would write.
/// </summary>
public class EquationTests : TestsRuntime
{
    #region Methods
    [Theory]
    [InlineData(@"x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}", "x = (−b ± √(b² − 4ac))/(2a)")]
    [InlineData(@"E = mc^2", "E = mc²")]
    [InlineData(@"a_n = a_1 + (n-1)d", "aₙ = a₁ + (n − 1)d")]
    [InlineData(@"\sum_{i=1}^{n} i = \frac{n(n+1)}{2}", "∑_(i = 1)ⁿ i = (n(n + 1))/2")]
    [InlineData(@"\sqrt[3]{27} = 3", "³√27 = 3")]
    [InlineData(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}", "(a, b; c, d)")]
    [InlineData(@"e^{i\pi} + 1 = 0", "e^(iπ) + 1 = 0")]
    public void TestAnEquationIsWrittenAsOneLineOfUnicode(string latex, string linear)
    {
        var (layout, error) = EquationLayout.Parse(latex, 40);
        Assert.Null(error);
        Assert.Equal(linear, layout!.Linear);
        Assert.True(layout.Width > 0 && layout.Ascent > 0);
    }

    [Fact]
    public void TestTooLongTooDeepAndMalformedAreRefusedWithAReason()
    {
        Assert.Contains("characters", EquationLayout.Parse(new string('x', EquationLayout.MaxLength + 1), 40).Error);
        var deep = string.Concat(Enumerable.Repeat(@"\frac{1}{", 40)) + "2" + new string('}', 40);
        Assert.Contains("nests 40 levels", EquationLayout.Parse(deep, 40).Error);
        Assert.NotNull(EquationLayout.Parse(@"\frac{1}{", 40).Error);
        Assert.Null(EquationLayout.Guard(@"\{ a \} \left( \frac{1}{2} \right)"));       // escaped braces are not nesting
        var parsed = new EquationToolkit().Parse(@"\frac{1}{2");
        Assert.False((bool)parsed["ok"]!);
        Assert.NotNull(parsed["error"]);
    }

    /// <summary>Drawn on a canvas: ink where the box says, and one line of drawn text holding the linear form.</summary>
    [Fact]
    public void TestAnEquationDrawsItsInkAndRecordsItsLine()
    {
        using var canvas = new SkiaCanvas(900, 260);
        var ctx = canvas.GetContext();
        ctx.FillStyle = "#ffffff";
        ctx.FillRect(0, 0, 900, 260);
        ctx.FillStyle = "#111111";
        var box = ctx.FillEquation(@"x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}", 40, 140, new System.Collections.Hashtable { ["size"] = 56f });

        Assert.Equal("x = (−b ± √(b² − 4ac))/(2a)", box["linear"]);
        var line = Assert.Single(canvas.DrawnText);
        Assert.Equal("x = (−b ± √(b² − 4ac))/(2a)", line.Words);
        var (x, y, x2, y2) = ((float)box["x"]!, (float)box["y"]!, (float)box["x2"]!, (float)box["y2"]!);

        // Ink inside the box, none to its right.
        int inside = 0, outside = 0;
        for (var px = 0; px < 900; px += 2)
            for (var py = 0; py < 260; py += 2)
            {
                var dark = canvas.SkBitmap.GetPixel(px, py).Red < 128;
                if (!dark) continue;
                if (px >= x - 2 && px <= x2 + 2 && py >= y - 2 && py <= y2 + 2) inside++; else outside++;
            }
        Assert.True(inside > 400, $"only {inside} dark samples inside the box");
        Assert.Equal(0, outside);

        var png = Path.Combine(Path.GetTempPath(), "polson-equation-test.png");
        File.WriteAllBytes(png, canvas.ToImageBytes("png", 100));
        Info("Equation render: {0}", png);
    }
    #endregion
}
