namespace Polson.Tests.CLI;

using Polson.CLI;
using Polson.ExtendedMind.DocumentProcessing;
using Xunit;

/// <summary>
/// The order the Nemotron endpoints are tried in, from <c>Nemotron:Prefer</c>.
/// </summary>
/// <remarks>
/// A value that names neither endpoint falls back to the default rather than disabling either: a typo should cost
/// the preference, not the reading.
/// </remarks>
public class NemotronPreferTests
{
    #region Fields
    private static readonly NemotronEndpoint Nim = new(NemotronEndpoint.Nim, NemotronEndpoint.NimBaseUrl, "k", NemotronEndpoint.NimModel);
    private static readonly NemotronEndpoint Nebius = new(NemotronEndpoint.Nebius, "https://tf.example/v1", "k", NemotronEndpoint.NebiusModel);
    #endregion

    #region Methods
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nim")]
    [InlineData("tokenfactory")]
    public void TestNimIsFirstUnlessNebiusIsNamed(string? prefer) =>
        Assert.Equal([Nim, Nebius], Program.ResolveNemotronEndpoints(prefer, Nim, Nebius));

    [Theory]
    [InlineData("nebius")]
    [InlineData("Nebius")]
    public void TestNebiusCanBePreferredWithNimAsItsBackup(string prefer) =>
        Assert.Equal([Nebius, Nim], Program.ResolveNemotronEndpoints(prefer, Nim, Nebius));

    /// <summary><c>Verify:Reader</c>: Gemini unless Nemotron is named; a typo costs the choice, not the reading.</summary>
    [Theory]
    [InlineData(null, VideoReader.GeminiReader)]
    [InlineData("", VideoReader.GeminiReader)]
    [InlineData("Gemini", VideoReader.GeminiReader)]
    [InlineData("nemotron", VideoReader.NemotronReader)]
    [InlineData(" Nemotron ", VideoReader.NemotronReader)]
    [InlineData("nebius", VideoReader.GeminiReader)]
    public void TestFilmsAreReadByGeminiUnlessNemotronIsNamed(string? configured, string expected) =>
        Assert.Equal(expected, Program.ResolveVerifyReader(configured));
    #endregion
}
