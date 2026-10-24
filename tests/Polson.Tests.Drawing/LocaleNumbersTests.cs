namespace Polson.Tests.Drawing;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;

using Polson.Drawing.Skia;

using Xunit;

/// <summary>
/// Numbers as places write them, held to ICU: every case in <c>Data/cldr-numbers-oracle.json</c> was formatted by Node's
/// <c>Intl.NumberFormat</c> (<c>tools/cldr_gen.js</c>), and <see cref="LocaleNumbers"/> must produce the same text.
/// </summary>
public class LocaleNumbersTests : TestsRuntime
{
    #region Methods
    [Fact]
    public void TestEveryOracleCaseIsFormattedAsIcuFormatsIt()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "cldr-numbers-oracle.json")));
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var wrong = cases.Select(c =>
        {
            var o = c.GetProperty("options");
            string? Str(string k) => o.TryGetProperty(k, out var v) ? v.GetString() : null;
            var got = LocaleNumbers.Format(c.GetProperty("value").GetDouble(), c.GetProperty("locale").GetString()!,
                Str("compact"), Str("currency"), o.TryGetProperty("percent", out var p) && p.GetBoolean(),
                o.TryGetProperty("decimals", out var d) ? d.GetInt32() : null);
            var want = c.GetProperty("text").GetString()!;
            return got == want ? null : $"{c.GetProperty("locale").GetString()} {c.GetProperty("value").GetDouble()} {o.GetRawText()}: got '{got}', ICU '{want}'";
        }).Where(w => w is not null).ToArray();

        Assert.True(cases.Length > 1000, $"only {cases.Length} oracle cases");
        Assert.True(wrong.Length == 0, $"{wrong.Length} of {cases.Length} differ from ICU:\n" + string.Join("\n", wrong.Take(40)));
    }

    /// <summary>The forms the report explainer is for, written out where a reader can see them.</summary>
    [Theory]
    [InlineData(130497000000d, "en-IN", "long", null, "13,049.7\u202fcrore")]   // a narrow no-break space
    [InlineData(1234567d, "en-IN", "long", null, "12.3\u202flakh")]
    [InlineData(130497000000d, "ja-JP", "long", null, "1305億")]
    [InlineData(130497000000d, "zh-CN", "short", "USD", "US$1305亿")]
    [InlineData(130497000000d, "de-DE", "short", "USD", "130,5\u00a0Mrd.\u00a0$")]   // no-break spaces
    [InlineData(1000d, "it-IT", "long", null, "mille")]
    [InlineData(1200000d, "fr-FR", "long", null, "1,2 million")]   // an ordinary space in French long forms
    [InlineData(2000000d, "fr-FR", "long", null, "2 millions")]
    public void TestScaleWordsCurrencyAndPluralFollowThePlace(double value, string locale, string compact, string? currency, string expected) =>
        Assert.Equal(expected, LocaleNumbers.Format(value, locale, compact, currency));

    [Fact]
    public void TestWhatIsNotCarriedIsRefusedWithWhatIs()
    {
        var e = Assert.Throws<ArgumentException>(() => LocaleNumbers.Format(1, "ru-RU"));
        Assert.Contains("en-IN", e.Message);
        Assert.Contains("CHF", Assert.Throws<ArgumentException>(() => LocaleNumbers.Format(1, "en-US", currency: "CHF")).Message);
        Assert.Throws<ArgumentException>(() => LocaleNumbers.Format(1, "en-US", compact: "tiny"));
        Assert.Throws<ArgumentException>(() => LocaleNumbers.Format(double.NaN, "en-US"));
        Assert.Equal(LocaleNumbers.Format(1234.5, "en-IN"), LocaleNumbers.Format(1234.5, "EN-in"));
    }
    #endregion
}
