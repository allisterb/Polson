namespace Polson.Tests.MCPServer;

using Polson.MCPServer;
using SkiaSharp;
using Xunit;

/// <summary>
/// How the composition's calls bind from JavaScript, which the C# tests of the same calls cannot show: Jint picks an
/// overload by the arguments' JavaScript shapes.
/// </summary>
public class MotionJsBindingTests : TestsRuntime
{
    #region Methods
    private static DrawingExecutionResult Execute(string body) =>
        new JsDrawingEngine().Execute($"const comp = Motion.composition({{ width: 40, height: 40 }}); {body} comp.render(0);", 40, 40, null, "png", 90);

    /// <summary>
    /// <c>comp.drawn(fn)</c>, its options left out as the docs allow, draws. It threw "takes a function first": a lone
    /// function arrives as a <c>Func</c>, and the overload that catches a missing function took any <c>object</c>.
    /// </summary>
    [Fact]
    public void ADrawnLayerTakesItsFunctionAlone()
    {
        var result = Execute("comp.drawn((ctx, v, t) => { ctx.fillStyle = '#ff0000'; ctx.fillRect(0, 0, 20, 20); });");

        Assert.True(result.Success, result.Error);
        using var frame = SKBitmap.Decode(result.ImageBytes);
        Assert.Equal(SKColors.Red, frame.GetPixel(10, 10));
    }

    /// <summary><c>comp.text</c> returns a handle whose boxes read in JavaScript as plain objects.</summary>
    [Fact]
    public void ATextReportsItsBoundsToTheScript()
    {
        var result = Execute("""
            const title = comp.text({ text: 'Hi', family: 'Arial', size: 20, origin: [20, 20] });
            const b = title.boundsAt(0);
            const letters = title.lettersAt(0);
            log(`bounds ${b.width > 0 && b.height > 0} letters ${letters.length} first ${letters[0].text} words ${title.text}`);
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("bounds true letters 2 first H words Hi", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>Motion.pathPoints</c> comes back as arrays a script can map over and change, and a region takes them back — as it
    /// takes the path itself.
    /// </summary>
    [Fact]
    public void PathPointsReadAsArraysAScriptCanChange()
    {
        var result = Execute("""
            const [square] = Motion.pathPoints('M0 0 H20 V20 H0 Z');
            const moved = square.points.map(p => ({ ...p, point: [p.point[0] + 10, p.point[1] + 10] }));
            comp.region({ points: moved, loop: square.loop, color: '#ff0000' });
            comp.region({ path: 'M0 30 H10 V40 H0 Z', color: '#0000ff' });
            comp.ellipse({ origin: [35, 5], radius: [4, 2], color: '#00ff00' });
            log(`count ${square.points.length} hole ${square.hole} first ${moved[0].point.join(',')}`);
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("count 4 hole false first 10,10", System.StringComparison.Ordinal));
        using var frame = SKBitmap.Decode(result.ImageBytes);
        Assert.Equal(SKColors.Red, frame.GetPixel(25, 25));
        Assert.Equal(SKColors.Blue, frame.GetPixel(5, 35));
        Assert.Equal(SKColors.Lime, frame.GetPixel(35, 5));
    }

    /// <summary><c>Motion.fitPoints</c> takes a script's own array of points and hands back points a region takes as they are.</summary>
    [Fact]
    public void FitPointsTakesAScriptsPolylineForARegion()
    {
        var result = Execute("""
            const ring = Array.from({ length: 120 }, (_, i) => [20 + 15 * Math.cos(i * Math.PI / 60), 20 + 10 * Math.sin(i * Math.PI / 60)]);
            const fitted = Motion.fitPoints(ring, { closed: true, error: 0.1 });
            comp.region({ points: fitted, color: '#ff0000' });
            log(`fitted ${fitted.length} keys ${Object.keys(fitted[0]).join(',')}`);
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Logs, l => l.Contains("keys point,t1,t2", System.StringComparison.Ordinal) && !l.Contains("fitted 120", System.StringComparison.Ordinal));
        using var frame = SKBitmap.Decode(result.ImageBytes);
        Assert.Equal(SKColors.Red, frame.GetPixel(20, 20));
    }

    /// <summary>A camera, a 3D group, a corner pin and a shadow, all from a script, as the reference writes them.</summary>
    [Fact]
    public void CameraWarpAndShadowBindFromAScript()
    {
        var result = Execute("""
            comp.fill({ color: '#ffffff' });
            comp.camera({ distance: 28 });
            const card = comp.group({ z: 0 });
            card.rectangle({ point1: [15, 15], point2: [25, 25], color: '#ff0000' });
            const pin = comp.warp({ from: [0, 0, 10, 10], to: [[0, 30], [10, 30], [10, 40], [0, 40]] });
            pin.rectangle({ point1: [0, 0], point2: [10, 10], color: '#0000ff' });
            const lifted = comp.group();
            lifted.rectangle({ point1: [28, 2], point2: [34, 8], color: '#00ff00' });
            lifted.shadow({ offset: [3, 3], size: 0 });
            """);

        Assert.True(result.Success, result.Error);
        using var frame = SKBitmap.Decode(result.ImageBytes);
        Assert.Equal(SKColors.Red, frame.GetPixel(11, 11));           // pushed in: the 10-unit card now spans past 11
        Assert.Equal(SKColors.Blue, frame.GetPixel(5, 35));
        Assert.True(frame.GetPixel(35, 10).Red < 200, "no shadow under the green square");
    }

    /// <summary>A write-on, a wipe, a colour correction and stepped time, all from a script.</summary>
    [Fact]
    public void TrimWipeGradeAndStepBindFromAScript()
    {
        var result = Execute("""
            comp.fill({ color: '#ffffff' });
            comp.outline({ points: [[2, 5], [38, 5]], width: 4, color: '#000000', trimEnd: 0.5 });
            const card = comp.group();
            card.rectangle({ point1: [0, 10], point2: [40, 40], color: '#ff0000' });
            card.wipe({ type: 'linear', completion: 0.5 });
            const graded = comp.group();
            graded.rectangle({ point1: [30, 30], point2: [40, 40], color: '#404040' });
            graded.colorCorrect({ exposure: 1 });
            comp.posterizeTime({ fps: 12 });
            """);

        Assert.True(result.Success, result.Error);
        using var frame = SKBitmap.Decode(result.ImageBytes);
        Assert.Equal(SKColors.Black, frame.GetPixel(10, 5));            // the first half of the line
        Assert.Equal(SKColors.White, frame.GetPixel(30, 5));            // trimmed away
        Assert.Equal(SKColors.White, frame.GetPixel(10, 20));           // wiped
        Assert.Equal(SKColors.Red, frame.GetPixel(28, 20));
        Assert.True(frame.GetPixel(35, 35).Red > 0x80, "the grade did not brighten the square");
    }

    /// <summary>Options with no function before them are still refused with what to write.</summary>
    [Fact]
    public void ADrawnLayerWithNoFunctionIsRefusedWithWhatToWrite()
    {
        var result = Execute("comp.drawn({ values: {} });");

        Assert.False(result.Success);
        Assert.Contains("takes a function first", result.Error);
    }
    #endregion
}
