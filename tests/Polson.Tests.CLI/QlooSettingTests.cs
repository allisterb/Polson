namespace Polson.Tests.CLI;

using Polson.CLI;
using Xunit;

/// <summary>
/// The Qloo counts read from <c>Qloo:Budget</c> and <c>Qloo:MonthlyReserve</c>.
/// </summary>
/// <remarks>
/// Zero is a valid setting for both: a budget of zero turns Qloo off for a session, and a reserve of zero spends
/// the month to its last request. A value that is not a whole number is a typo, and falling back is what stops
/// <c>int.TryParse</c>'s zero from silently turning Qloo off.
/// </remarks>
public class QlooSettingTests
{
    #region Methods
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TestAnAbsentSettingUsesTheDefault(string? configured) =>
        Assert.Equal(120, Program.ResolveQlooSetting("Qloo:Budget", configured, 120));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("40", 40)]
    [InlineData("  2500  ", 2500)]
    public void TestAWholeNumberIsTakenZeroIncluded(string configured, int expected) =>
        Assert.Equal(expected, Program.ResolveQlooSetting("Qloo:MonthlyReserve", configured, 1000));

    [Theory]
    [InlineData("-1")]
    [InlineData("lots")]
    [InlineData("1.5")]
    public void TestAnUnusableValueFallsBackToTheDefault(string configured) =>
        Assert.Equal(1000, Program.ResolveQlooSetting("Qloo:MonthlyReserve", configured, 1000));
    #endregion
}
