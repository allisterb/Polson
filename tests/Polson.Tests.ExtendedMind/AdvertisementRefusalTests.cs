namespace Polson.Tests.ExtendedMind;

using System;
using System.Threading.Tasks;
using Polson.ExtendedMind.ImageGeneration;
using Xunit;

/// <summary>
/// A cutout is one element of an ad, never the ad.
/// </summary>
/// <remarks>
/// The product type's packshot step generates the product as a cutout and sets its name, the claim and the layout
/// in code. The form classifier does not run on a cutout, and asked offline it let "an advertisement for a cold brew
/// coffee" through as merely Ambiguous (2026-10-19), so without this the whole ad could be bought in one call.
/// </remarks>
public class AdvertisementRefusalTests : TestsRuntime
{
    #region Tests
    [Theory]
    [InlineData("an advertisement for a cold brew coffee", "advertisement")]
    [InlineData("a billboard showing a coffee can at night", "billboard")]
    [InlineData("an ad for Nightshift cold brew", "ad")]
    [InlineData("print ads for a canned coffee", "ads")]
    [InlineData("a FLYER for a cafe opening", "flyer")]
    public void TestAFinishedAdvertisingPieceIsNamed(string descriptor, string piece)
    {
        Assert.Equal(piece, AssetRequisitionToolkit.NamedAdvertisement(descriptor));
    }

    /// <summary>The product itself, and words that merely contain the letters, are not refused.</summary>
    [Theory]
    [InlineData("a matte black 250 ml aluminium can, plain cream band, no text")]
    [InlineData("a packshot of a cold brew can with condensation")]
    [InlineData("an ad hoc arrangement of coffee beans")]
    [InlineData("a road sign, adhesive label peeling, shadows")]
    [InlineData("headphones, three-quarter view")]
    [InlineData("")]
    public void TestTheProductIsNotRefused(string descriptor)
    {
        Assert.Null(AssetRequisitionToolkit.NamedAdvertisement(descriptor));
    }

    [Fact]
    public async Task TestTheRefusalSaysWhatToAskForAndSpendsNothing()
    {
        var budget = new AssetBudget(5);
        using var generator = new ImageGenerator("dummy-key-not-used-offline");
        var toolkit = new AssetRequisitionToolkit(
            generator, new RequisitionCache(ImageGeneratorTests.TempCacheDir()), budget, "Designer");

        var cutout = await toolkit.Cutout("an advertisement for a cold brew coffee");

        Assert.False(cutout.Success);
        Assert.Equal(ImageGenerationFailure.InvalidRequest, cutout.Failure);
        Assert.Contains("Ask for the product itself", cutout.Error, StringComparison.Ordinal);
        Assert.Equal(0, budget.Spent);
    }
    #endregion
}
