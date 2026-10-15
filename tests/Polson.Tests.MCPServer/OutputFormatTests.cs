namespace Polson.Tests.MCPServer;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Polson.MCPServer;
using Xunit;

/// <summary>
/// The encoding a render is written in follows <c>outFile</c>'s name unless <c>format</c> says otherwise, and a
/// contradiction is refused. anim4 asked for <c>artifacts/00_probe.png</c> with no format and got WebP bytes under the
/// <c>.png</c> name, admitted only in <c>imageFormat</c>.
/// </summary>
public class OutputFormatTests : TestsRuntime, IDisposable
{
    #region Fields
    private readonly string root = Directory.CreateTempSubdirectory("polson-format-").FullName;
    private const string Square = "const c = createCanvas(16, 16); const x = c.getContext('2d'); x.fillStyle = '#c33'; x.fillRect(0, 0, 16, 16); c;";
    #endregion

    #region Methods
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string Magic(byte[] b) =>
        b is [0x89, 0x50, 0x4E, 0x47, ..] ? "png" : b is [0xFF, 0xD8, ..] ? "jpeg" : b.Length > 12 && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? "webp" : "?";
    #endregion

    #region Tests
    [Theory]
    [InlineData(null, null, "webp")]
    [InlineData(null, "a.png", "png")]
    [InlineData(null, "a.JPG", "jpeg")]
    [InlineData(null, "a.jpeg", "jpeg")]
    [InlineData(null, "a.webp", "webp")]
    [InlineData("png", null, "png")]
    [InlineData("jpg", "a.jpeg", "jpeg")]
    [InlineData("png", "a.out", "png")]
    public void TestTheFormatIsTheOneNamed(string? format, string? outFile, string expected) =>
        Assert.Equal(expected, DrawingMcpTools.OutputFormat(format, outFile));

    [Fact]
    public void TestAFormatThatContradictsTheNameIsRefused()
    {
        var error = Assert.Throws<ArgumentException>(() => DrawingMcpTools.OutputFormat("png", "artifacts/a.webp"));
        Assert.Contains("png bytes under a webp name", error.Message);
        Assert.Contains("name the file .png", error.Message);
    }

    /// <summary>The regression itself: a .png with no format is a PNG on disk.</summary>
    [Fact]
    public async Task TestAPngNameWritesAPng()
    {
        var result = await new DrawingMcpTools(null, null, null, root).ExecuteScript(Square, 16, 16, outFile: "artifacts/probe.png");

        Assert.True(result.Success, result.Error);
        Assert.Equal("png", Magic(File.ReadAllBytes(Path.Combine(root, "artifacts", "probe.png"))));
        Assert.Equal("png", result.ImageFormat);
    }

    /// <summary>A contradiction costs no execution and writes nothing.</summary>
    [Fact]
    public async Task TestAContradictionIsRefusedBeforeTheScriptRuns()
    {
        var result = await new DrawingMcpTools(null, null, null, root).ExecuteScript(Square, 16, 16, format: "jpeg", outFile: "artifacts/probe.png");

        Assert.False(result.Success);
        Assert.Contains("jpeg bytes under a png name", result.Error);
        Assert.False(File.Exists(Path.Combine(root, "artifacts", "probe.png")));
    }
    #endregion
}
