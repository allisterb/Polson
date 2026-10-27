namespace Polson.Tests.CLI;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

/// <summary>
/// How the built CLI behaves when it is invoked, as opposed to what the generator writes once it is
/// running. It is the CLI this test run built, in its own configuration, not <c>bin/cli</c>, which holds
/// only the Release build agents run.
/// </summary>
/// <remarks>
/// Driven as a real process because the behaviour under test is argument dispatch in <c>Main</c>, and
/// the failure it guards against — a bare command that never returns — cannot be observed by calling
/// a method.
/// </remarks>
public class CommandLineTests : TestsRuntime
{
    #region Fields
    static readonly string RepoRoot = FindRepoRoot();
    static readonly string CliDll = Path.Combine(RepoRoot, "src", "Polson.CLI", "bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, new DirectoryInfo(AppContext.BaseDirectory).Name, "Polson.CLI.dll");
    #endregion

    #region Methods
    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Polson.sln"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>Runs the CLI and returns its output, killing it if it does not finish in time.</summary>
    static async Task<(int ExitCode, string Output)> RunAsync(TimeSpan timeout, params string[] args)
    {
        Assert.True(File.Exists(CliDll), $"The CLI is not built: {CliDll}.");

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        start.ArgumentList.Add(CliDll);
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"'{string.Join(' ', args)}' did not exit within {timeout.TotalSeconds:0}s.");
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>
    /// <c>techniques --json</c> is read through a pipe by the studio's form, so it is ASCII: redirected, the CLI writes in
    /// the console's code page, and a raw "§" reached the studio as a control character its JSON parser refused
    /// (2026-10-25), which hid the form's whole Graphic design field.
    /// </summary>
    [Fact]
    public async Task TestTheTechniqueCatalogueIsAsciiJsonThroughAPipe()
    {
        var (exitCode, output) = await RunAsync(TimeSpan.FromSeconds(30), "techniques", "--json");

        Assert.Equal(0, exitCode);
        Assert.DoesNotMatch("[^\\x00-\\x7F]", output);
        var rows = System.Text.Json.JsonDocument.Parse(output).RootElement;
        Assert.True(rows.GetArrayLength() > 0);
        Assert.Contains("§", rows[0].GetProperty("source").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused <c>compare</c> exits 1 and says why. <c>Main</c>'s catch-all logs to file and exits 0, so a refusal reaching
    /// it would read to the studio as a file made, and the page would offer a link to nothing.
    /// </summary>
    [Fact]
    public async Task TestARefusedComparisonExitsNonZeroAndSaysWhy()
    {
        var films = Path.Combine(Path.GetTempPath(), $"polson-films-{Guid.NewGuid():N}.json");
        File.WriteAllText(films, """[{"path": "a.mp4", "title": "Audience A", "detail": ""}]""");
        try
        {
            var (exitCode, output) = await RunAsync(TimeSpan.FromSeconds(30), "compare", "out.mp4", "--films", films);

            Assert.Equal(1, exitCode);
            Assert.Contains("2 to 6 films", output);
        }
        finally
        {
            File.Delete(films);
        }
    }

    /// <summary>
    /// A bare invocation prints the verbs and exits, rather than starting the server.
    /// </summary>
    /// <remarks>
    /// <c>server</c> is the default verb because that is how an MCP host launches this, so no
    /// arguments used to mean "start the stdio server" — which waits on standard input forever and,
    /// because stdio mode keeps standard output clear for JSON-RPC framing, prints nothing at all
    /// while doing it. To anyone typing the bare command that is a hang with no diagnostic.
    /// </remarks>
    [Fact]
    public async Task TestNoArgumentsPrintsHelpRatherThanStartingTheServer()
    {
        var (exitCode, output) = await RunAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);
        Assert.Contains("create-project", output, StringComparison.Ordinal);
        Assert.Contains("eval", output, StringComparison.Ordinal);
    }

    /// <summary>A temporary directory, and the arguments that keep <c>eval</c>'s image and SVG out of the repository root.</summary>
    static (string Dir, string[] Outputs) Scratch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "polson-cli-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (dir, ["--img", Path.Combine(dir, "out.webp"), "--svg", Path.Combine(dir, "out.svg")]);
    }

    const string BledPoster =
        "const p = Snap(1000, 1414.29); p.rect(-20, -20, 1040, 1454.29).attr({ fill: '#c0392b' }); "
      + "p.text(100, 300, 'Get screened').attr({ 'font-size': 80 }); p;";

    /// <summary><c>eval --pdf</c> writes the print file and reports it as <c>outPdf</c> does.</summary>
    [Fact]
    public async Task TestEvalWritesAPrintPdf()
    {
        var (dir, outputs) = Scratch();
        try
        {
            var script = Path.Combine(dir, "poster.js");
            File.WriteAllText(script, BledPoster);
            var pdf = Path.Combine(dir, "poster.pdf");

            var (exitCode, output) = await RunAsync(TimeSpan.FromSeconds(60),
                ["eval", script, .. outputs, "--pdf", pdf, "--pdf-size", "A2"]);

            Assert.True(exitCode == 0, output);
            Assert.True(File.Exists(pdf), output);
            Assert.Contains("/TrimBox", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)));
            Assert.Contains("trim 420 x 594 mm, bleed 3 mm", output, StringComparison.Ordinal);
            Assert.Contains("One paper unit prints at 0.42 mm", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>An SVG a run left behind can be made into a print file without rerunning anything.</summary>
    [Fact]
    public async Task TestEvalMakesAPrintPdfFromAnSvgOnDisk()
    {
        var (dir, outputs) = Scratch();
        try
        {
            var svg = Path.Combine(dir, "final.svg");
            File.WriteAllText(svg, """
                <svg xmlns="http://www.w3.org/2000/svg" width="1000" height="1414.29" viewBox="0 0 1000 1414.29">
                  <rect x="-20" y="-20" width="1040" height="1454.29" fill="#1a5276"/>
                </svg>
                """);
            var pdf = Path.Combine(dir, "final.pdf");

            var (exitCode, output) = await RunAsync(TimeSpan.FromSeconds(60),
                ["eval", "--svg-in", svg, .. outputs, "--pdf", pdf, "--pdf-size", "A2", "--pdf-bleed", "0.125in"]);

            Assert.True(exitCode == 0, output);
            Assert.True(File.Exists(pdf), output);
            Assert.Contains("bleed 3.2 mm", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A refusal exits non-zero, so <c>polson eval … --pdf … || exit 1</c> fires.</summary>
    [Theory]
    [InlineData("needs --pdf-size", false)]
    [InlineData("did not return a Snap paper", true)]
    public async Task TestEvalPdfRefusalsFailTheProcess(string expected, bool withSize)
    {
        var (dir, outputs) = Scratch();
        try
        {
            var script = Path.Combine(dir, "canvas.js");
            File.WriteAllText(script, "const c = createCanvas(100, 141); c.getContext('2d').fillRect(0, 0, 10, 10); c;");
            var pdf = Path.Combine(dir, "poster.pdf");
            string[] size = withSize ? ["--pdf-size", "A2"] : [];

            var (exitCode, output) = await RunAsync(TimeSpan.FromSeconds(60), ["eval", script, .. outputs, "--pdf", pdf, .. size]);

            Assert.NotEqual(0, exitCode);
            Assert.Contains(expected, output.ReplaceLineEndings(" "), StringComparison.Ordinal);
            Assert.False(File.Exists(pdf));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// <c>eval --sif</c> writes the last composition a script built, so a film's <c>.sif</c> is taken from its script on
    /// demand rather than written by every run — and a held letter costs two keys, not one per frame.
    /// </summary>
    [Fact]
    public async Task TestEvalWritesTheScriptsCompositionAsSif()
    {
        var (dir, outputs) = Scratch();
        try
        {
            var script = Path.Combine(dir, "film.js");
            File.WriteAllText(script, """
                const n = Motion.nodes;
                const draft = Motion.composition({ width: 64, height: 36 });         // not the last: not written
                const comp = Motion.composition({ width: 320, height: 180, fps: 24, duration: 4 });
                comp.fill({ color: '#efe6d2' });
                comp.circle({ origin: [160, 90], radius: n.linear('real', 10, 20), color: '#d23f26', desc: 'disc' });
                comp.render(1);
                """);
            var sif = Path.Combine(dir, "film.sif");

            var (exitCode, output) = await RunAsync(TimeSpan.FromSeconds(60), ["eval", script, .. outputs, "--sif", sif]);

            Assert.True(exitCode == 0, output);
            Assert.True(File.Exists(sif), output);
            var xml = File.ReadAllText(sif);
            Assert.Contains("width=\"320\"", xml, StringComparison.Ordinal);
            Assert.Contains("desc=\"disc\"", xml, StringComparison.Ordinal);
            Assert.Contains("Synfig file saved", output.ReplaceLineEndings(" "), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// A script with no composition, one Synfig cannot hold, or one that failed writes nothing and fails the process — the
    /// last because half a film written as a .sif would look finished.
    /// </summary>
    [Theory]
    [InlineData("const c = createCanvas(10, 10); c;", "built no Motion.composition")]
    [InlineData("const comp = Motion.composition(); comp.drawn((ctx, v, t) => {}, {}); comp.render(0);", "drawn layer")]
    [InlineData("const comp = Motion.composition(); comp.fill({ color: '#000' }); log(Stage.elapsedMinutes.toFixed(1));", "script failed")]
    public async Task TestEvalSifRefusalsFailTheProcess(string body, string expected)
    {
        var (dir, outputs) = Scratch();
        try
        {
            var script = Path.Combine(dir, "nofilm.js");
            File.WriteAllText(script, body);
            var sif = Path.Combine(dir, "nofilm.sif");

            var (exitCode, output) = await RunAsync(TimeSpan.FromSeconds(60), ["eval", script, .. outputs, "--sif", sif]);

            Assert.NotEqual(0, exitCode);
            Assert.Contains(expected, output.ReplaceLineEndings(" "), StringComparison.Ordinal);
            Assert.False(File.Exists(sif));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Asking for help is not a failure, so it keeps exit 0.</summary>
    [Fact]
    public async Task TestAskingForHelpSucceeds()
    {
        var (exitCode, _) = await RunAsync(TimeSpan.FromSeconds(30), "--help");

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// A command line that could not be parsed fails the process.
    /// </summary>
    /// <remarks>
    /// The parser already wrote what was wrong; the exit code was what it did not set. Without one, a
    /// mistyped flag and a successful run look identical to a script, and
    /// <c>create-project … || exit 1</c> never fires.
    /// </remarks>
    [Theory]
    [InlineData("create-project")]                                  // required values missing
    [InlineData("create-project", "dir", "id", "agy", "--nope")]     // unknown option
    public async Task TestAnUnparseableCommandLineFails(params string[] args)
    {
        var (exitCode, _) = await RunAsync(TimeSpan.FromSeconds(30), args);

        Assert.NotEqual(0, exitCode);
    }

    /// <summary>
    /// A verb that refused fails the process, and not only the ones the parser caught.
    /// </summary>
    /// <remarks>
    /// The parse-failure case above was fixed and these were not, because they are a different
    /// defect in a different place: the command line parsed perfectly and the <em>handler</em> said
    /// no. <c>RunReport.Run</c> was already returning 1 for a directory that is not there, and the
    /// dispatcher wrapped it in <c>Task.FromResult</c> and dropped it; <c>reset</c> had the same
    /// wiring. So `polson report missing-dir` printed its error and exited 0.
    /// <para>
    /// Driven as a real process rather than by calling the method, because the method was never the
    /// broken part — the wiring between it and the exit code was, and only a process can see that.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("report")]
    [InlineData("reset")]
    public async Task TestAVerbThatRefusesFailsTheProcess(string verb)
    {
        var missing = Path.Combine(Path.GetTempPath(), "polson-absent-" + Guid.NewGuid().ToString("N")[..8]);

        var (exitCode, _) = await RunAsync(TimeSpan.FromSeconds(30), verb, missing);

        Assert.NotEqual(0, exitCode);
    }
    #endregion
}
