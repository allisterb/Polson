namespace Polson.Tests.Drawing;

using System;
using System.Linq;
using Polson.Drawing.Svg;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// Font-family lists that name no installed family. The machine's fonts are injected, so the cases do not depend on
/// what this machine has; one case uses the real lookup with a name no machine has.
/// </summary>
public class FontAuditTests : TestsRuntime
{
    #region Fields
    private static readonly string[] Families = ["Franklin Gothic", "Franklin Gothic Book", "Arial", "Bodoni MT"];
    #endregion

    #region Methods
    private static string Svg(string body) =>
        $"""<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="1414">{body}</svg>""";

    private static bool Installed(string name) => Families.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Franklin Gothic has every weight; Bodoni MT only a regular, so a heavy request lands at 400.</summary>
    private static int? FaceWeight(string family, int weight, bool italic) =>
        family.Equals("Franklin Gothic", StringComparison.OrdinalIgnoreCase) ? weight
        : family.Equals("Bodoni MT", StringComparison.OrdinalIgnoreCase) ? 400
        : null;

    private static string[] Check(string body) => [.. FontAudit.Check(Svg(body), Installed, Families, FaceWeight)];

    /// <summary>poster3's case: a weight written into the name, answered with the family and the weight that draw it.</summary>
    [Fact]
    public void TestAWeightInTheNameIsAnsweredWithTheFamilyAndWeight()
    {
        var warning = Assert.Single(Check("""<text id="title" font-family="'Franklin Gothic Heavy'">BE COOL</text>"""));

        Assert.Contains("\"'Franklin Gothic Heavy'\" (#title) names no installed family", warning);
        Assert.Contains("use font-family \"'Franklin Gothic'\" with font-weight 900", warning);
    }

    [Fact]
    public void TestAWeightAndSlantInTheNameAreBothNamed()
    {
        var warning = Assert.Single(Check("""<text font-family="Franklin Gothic Bold Italic">x</text>"""));

        Assert.Contains("with font-weight 700 and font-style italic", warning);
    }

    /// <summary>A family with no face near the weight asked for gets no suggestion: an agent would follow it.</summary>
    [Fact]
    public void TestNoSuggestionWhenTheFamilyHasNoFaceAtThatWeight()
    {
        var warning = Assert.Single(Check("""<text font-family="'Bodoni MT Black'">x</text>"""));

        Assert.DoesNotContain("use font-family", warning);
        Assert.Contains("Skia.Font.families()", warning);
    }

    [Fact]
    public void TestAListWithAnInstalledNameIsNotReported()
    {
        Assert.Empty(Check("""
            <text font-family="'Helvetica Neue', Arial, sans-serif">x</text>
            <text font-family="'Franklin Gothic', Impact, sans-serif" font-weight="900">y</text>
            """));
    }

    /// <summary>A generic family is not a name to check; a missing first choice falling to one still is reported.</summary>
    [Fact]
    public void TestGenericFamiliesAreNotNamesButDoNotExcuseAMissingOne()
    {
        Assert.Empty(Check("""<text font-family="sans-serif">x</text>"""));
        Assert.Single(Check("""<text font-family="Didot, serif">x</text>"""));
    }

    /// <summary>One warning per list however often it is used, with up to three of the places it is.</summary>
    [Fact]
    public void TestOneWarningPerListWithWhereItIsUsed()
    {
        var warning = Assert.Single(Check("""
            <g font-family="Didot"><text id="a">1</text></g>
            <text id="b" style="fill:#000; font-family: Didot">2</text>
            <text font-family="Didot">3</text>
            <text id="d" font-family="Didot">4</text>
            """));

        Assert.Contains("(<g>, #b, <text>, …)", warning);
    }

    /// <summary>The real lookup: a family no machine has is reported.</summary>
    [Fact]
    public void TestTheMachinesLookupReportsAMissingFamily()
    {
        var warning = Assert.Single(FontAudit.Check(Svg("""<text font-family="'Polson Nonexistent Face 7Q'">x</text>""")));

        Assert.Contains("names no installed family", warning);
    }

    /// <summary>
    /// lanternkeepercloud1 set a poster's English in <c>sans-serif</c>, which the audit passes over and a browser resolves
    /// its own way. Where a paper's family is set, a generic-only list and a list of absent faces are refused; a generic
    /// after an installed face is a fallback and passes, as do <c>inherit</c> and an empty value.
    /// </summary>
    [Theory]
    [InlineData("sans-serif", "only a generic family")]
    [InlineData("serif, monospace", "only a generic family")]
    [InlineData("'Absent Face', sans-serif", "names no installed family")]
    [InlineData("'Noto Serif CJK KR', 'Noto Sans CJK KR', serif", null)]
    [InlineData("'Absent Face', Inter, sans-serif", null)]
    [InlineData("inherit", null)]
    [InlineData("", null)]
    public void TestAPapersFamilyMustNameAnInstalledFace(string list, string? refusal)
    {
        static bool Installed(string f) => f is "Inter" or "Noto Serif CJK KR" or "Noto Sans CJK KR";
        if (refusal is null)
        {
            Assert.Equal(list, FontAudit.Require(list, Installed));
            return;
        }
        var e = Assert.Throws<ArgumentException>(() => FontAudit.Require(list, Installed));
        Assert.Contains(refusal, e.Message);
    }

    /// <summary>The refusal reaches a script through <c>attr()</c>, naming the list.</summary>
    [Fact]
    public void TestAScriptSettingAGenericOnlyFamilyIsRefused()
    {
        var result = new JsDrawingEngine().Execute("const p = Snap(100, 40); p.text(10, 30, 'Hi').attr({ 'font-family': 'sans-serif' }); p;",
            100, 40, null, "png", 90);
        Assert.False(result.Success);
        Assert.Contains("font-family \"sans-serif\" names only a generic family", result.Error);
    }
    #endregion
}
