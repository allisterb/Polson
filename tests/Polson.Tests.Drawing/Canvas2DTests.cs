namespace Polson.Tests.Drawing;

using System;
using System.Collections.Generic;
using System.Linq;
using Polson.Drawing.Skia;
using Polson.Drawing.Svg;
using Polson.MCPServer;
using SkiaSharp;
using Xunit;

public class Canvas2DTests : TestsRuntime
{
    #region Canvas Creation and Path Drawing Tests
    [Fact]
    public void TestCanvasCreationAndBasicDrawing()
    {
        var canvas = new SkiaCanvas(400, 300);
        Assert.Equal(400, canvas.Width);
        Assert.Equal(300, canvas.Height);

        var ctx = canvas.GetContext("2d");
        Assert.NotNull(ctx);

        ctx.FillStyle = "#ff0000";
        ctx.FillRect(10, 10, 100, 80);

        ctx.StrokeStyle = "#0000ff";
        ctx.LineWidth = 4f;
        ctx.StrokeRect(150, 20, 100, 50);

        ctx.ClearRect(20, 30, 20, 20);

        var webpBytes = canvas.ToImageBytes();
        Assert.NotNull(webpBytes);
        Assert.True(webpBytes.Length > 0);

        // Verify WebP signature
        Assert.Equal((byte)'R', webpBytes[0]);
        Assert.Equal((byte)'I', webpBytes[1]);
        Assert.Equal((byte)'F', webpBytes[2]);
        Assert.Equal((byte)'F', webpBytes[3]);
    }

    [Fact]
    public void TestPathConstructionAndArcs()
    {
        var canvas = new SkiaCanvas(500, 500);
        var ctx = canvas.GetContext("2d");

        ctx.BeginPath();
        ctx.MoveTo(50, 50);
        ctx.LineTo(200, 50);
        ctx.LineTo(200, 200);
        ctx.ClosePath();
        ctx.FillStyle = "#10b981";
        ctx.Fill();

        ctx.BeginPath();
        ctx.Arc(350, 200, 50, 0, MathF.PI * 2f);
        ctx.FillStyle = "#3b82f6";
        ctx.Fill();

        ctx.BeginPath();
        ctx.RoundRect(50, 300, 150, 100, 20f);
        ctx.StrokeStyle = "#f59e0b";
        ctx.LineWidth = 3f;
        ctx.Stroke();

        var imgBytes = canvas.ToImageBytes();
        Assert.True(imgBytes.Length > 0);
    }
    #endregion

    #region State Stack and Transforms Tests
    [Fact]
    public void TestStateStackAndTransforms()
    {
        var canvas = new SkiaCanvas(400, 400);
        var ctx = canvas.GetContext("2d");

        ctx.FillStyle = "#ff0000";
        ctx.Save();

        ctx.Translate(100, 100);
        ctx.Rotate(MathF.PI / 4f);
        ctx.Scale(2f, 2f);
        ctx.FillStyle = "#00ff00";
        ctx.FillRect(0, 0, 50, 50);

        ctx.Restore();
        ctx.FillRect(0, 0, 20, 20); // Should be red (#ff0000)

        var bmp = canvas.ToBitmap();
        Assert.NotNull(bmp);
        Assert.Equal(400, bmp.Width);
    }
    #endregion

    #region Gradients and Patterns Tests
    [Fact]
    public void TestLinearAndRadialGradients()
    {
        var canvas = new SkiaCanvas(600, 400);
        var ctx = canvas.GetContext("2d");

        // Linear gradient
        var linGrad = ctx.CreateLinearGradient(0, 0, 600, 0);
        linGrad.AddColorStop(0f, "#ff0000");
        linGrad.AddColorStop(0.5f, "#00ff00");
        linGrad.AddColorStop(1f, "#0000ff");

        ctx.FillStyle = linGrad;
        ctx.FillRect(0, 0, 600, 200);

        // Radial gradient
        var radGrad = ctx.CreateRadialGradient(300, 300, 20, 300, 300, 100);
        radGrad.AddColorStop(0f, "#ffffff");
        radGrad.AddColorStop(1f, "#000000");

        ctx.FillStyle = radGrad;
        ctx.FillRect(0, 200, 600, 200);

        var dataUri = canvas.ToDataUri();
        Assert.StartsWith("data:image/webp;base64,", dataUri);
    }
    #endregion

    #region Text Rendering and Metrics Tests
    [Fact]
    public void TestTextRenderingAndMetrics()
    {
        var canvas = new SkiaCanvas(600, 400);
        var ctx = canvas.GetContext("2d");

        ctx.Font = "bold 32px sans-serif";
        ctx.FillStyle = "#ffffff";
        ctx.TextAlign = "center";
        ctx.TextBaseline = "middle";

        var metrics = ctx.MeasureText("Skia Canvas 2D");
        Assert.NotNull(metrics);
        Assert.True(Convert.ToSingle(metrics["width"]) > 0);

        ctx.FillText("Skia Canvas 2D", 300, 200);

        var imgBytes = canvas.ToImageBytes();
        Assert.True(imgBytes.Length > 0);
    }

    private static float Metric(Dictionary<string, object> m, string key) => Convert.ToSingle(m[key]);

    /// <summary>
    /// <c>actualBoundingBox*</c> is the ink of the string, <c>fontBoundingBox*</c> the face's ascent and descent. Until
    /// 2026-11-17 the "actual" ascent was the face's: 90.5 for an "H" whose ink is 72 tall, so a cap height read from it
    /// was a quarter too large.
    /// </summary>
    /// <summary>
    /// A font is looked up once per family list, weight and slant, not on every <c>ctx.font</c>. On Cloud Run each lookup
    /// was a 12 ms fontconfig match, and a whole-film flash check that sets fonts on every frame ran past the script
    /// limit (2026-11-24). The cached face still honours weight, slant and fallback.
    /// </summary>
    [Fact]
    public void TestFontLookupsAreRememberedAndStillHonourTheirStyle()
    {
        var bold = CanvasRenderingContext2D.ResolveFamily("sans-serif", SKFontStyleWeight.Bold, SKFontStyleSlant.Upright);
        Assert.Same(bold, CanvasRenderingContext2D.ResolveFamily(" Sans-Serif ", SKFontStyleWeight.Bold, SKFontStyleSlant.Upright));

        var regular = CanvasRenderingContext2D.ResolveFamily("sans-serif", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright);
        Assert.NotSame(bold, regular);
        Assert.True(bold.FontWeight > regular.FontWeight, $"bold {bold.FontWeight}, regular {regular.FontWeight}");

        var fallback = CanvasRenderingContext2D.ResolveFamily("No Such Face, Arial", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright);
        Assert.Equal("Arial", fallback.FamilyName);

        // Set over and over, as a frame callback does, and still draws.
        var canvas = new SkiaCanvas(200, 60);
        var ctx = canvas.GetContext("2d");
        for (var i = 0; i < 50; i++)
        {
            ctx.Font = i % 2 == 0 ? "bold 32px sans-serif" : "32px Arial";
            ctx.FillText("Hi", 10, 40);
        }
        Assert.Equal(32f, ctx.MeasureText("H") is { } m ? Metric(m, "fontBoundingBoxAscent") / 0.905f : 0f, 0.5f);
    }

    [Fact]
    public void TestMeasureTextGivesTheInkAndTheFacesAscentApart()
    {
        var ctx = new SkiaCanvas(10, 10).GetContext("2d");
        ctx.Font = "100px Arial";

        var h = ctx.MeasureText("H");
        Assert.InRange(Metric(h, "actualBoundingBoxAscent"), 70f, 74f);       // Arial's cap height is 0.716 em
        Assert.InRange(Metric(h, "actualBoundingBoxDescent"), -1f, 1f);
        Assert.InRange(Metric(h, "fontBoundingBoxAscent"), 89f, 92f);         // its ascent, 0.905 em
        Assert.InRange(Metric(h, "fontBoundingBoxDescent"), 20f, 23f);        // its descent, 0.212 em
        Assert.True(Metric(h, "actualBoundingBoxLeft") <= 0f && Metric(h, "actualBoundingBoxRight") > 50f);

        var g = ctx.MeasureText("g");
        Assert.True(Metric(g, "actualBoundingBoxDescent") > 15f, $"g descends {Metric(g, "actualBoundingBoxDescent")}");
        Assert.True(Metric(g, "actualBoundingBoxAscent") < Metric(h, "actualBoundingBoxAscent"));

        foreach (var nothing in new[] { "", " " })
        {
            var m = ctx.MeasureText(nothing);
            Assert.Equal(0f, Metric(m, "actualBoundingBoxAscent"));
            Assert.Equal(0f, Metric(m, "actualBoundingBoxDescent"));
        }
    }

    /// <summary>The distances are from the point <c>fillText</c> is given, under <c>textAlign</c> and <c>textBaseline</c>,
    /// and they match where the ink is drawn.</summary>
    [Fact]
    public void TestMeasureTextMatchesTheInkFillTextDraws()
    {
        var canvas = new SkiaCanvas(300, 200);
        var ctx = canvas.GetContext("2d");
        ctx.FillStyle = "#ffffff";
        ctx.FillRect(0, 0, 300, 200);
        ctx.Font = "100px Arial";
        ctx.FillStyle = "#000000";
        ctx.TextAlign = "center";
        ctx.TextBaseline = "middle";
        var m = ctx.MeasureText("H");
        ctx.FillText("H", 150, 100);

        using var bitmap = SKBitmap.Decode(canvas.ToImageBytes());
        var rows = Enumerable.Range(0, bitmap.Height).Where(y => Enumerable.Range(0, bitmap.Width).Any(x => bitmap.GetPixel(x, y).Red < 128)).ToArray();
        var cols = Enumerable.Range(0, bitmap.Width).Where(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).Red < 128)).ToArray();

        Assert.InRange(rows.First(), 100 - Metric(m, "actualBoundingBoxAscent") - 1.5f, 100 - Metric(m, "actualBoundingBoxAscent") + 1.5f);
        Assert.InRange(rows.Last() + 1, 100 + Metric(m, "actualBoundingBoxDescent") - 1.5f, 100 + Metric(m, "actualBoundingBoxDescent") + 1.5f);
        Assert.InRange(cols.First(), 150 - Metric(m, "actualBoundingBoxLeft") - 1.5f, 150 - Metric(m, "actualBoundingBoxLeft") + 1.5f);
        Assert.InRange(cols.Last() + 1, 150 + Metric(m, "actualBoundingBoxRight") - 1.5f, 150 + Metric(m, "actualBoundingBoxRight") + 1.5f);
    }
    #endregion

    #region Skia Procedural Shaders and Filters Tests
    [Fact]
    public void TestSkiaProceduralNoiseAndFilters()
    {
        var canvas = new SkiaCanvas(400, 400);
        var ctx = canvas.GetContext("2d");
        var skia = new SkiaApi();

        // Procedural Perlin noise
        var noise = skia.Shader.PerlinNoiseTurbulence(0.05f, 0.05f, 4, 12345);
        ctx.FillStyle = noise;
        ctx.FillRect(0, 0, 400, 400);

        // Native Gaussian Blur filter
        ctx.Filter = skia.ImageFilter.Blur(5f, 5f);
        ctx.FillStyle = "#ffffff";
        ctx.FillRect(100, 100, 200, 200);

        var imgBytes = canvas.ToImageBytes();
        Assert.True(imgBytes.Length > 0);
    }
    #endregion

    #region Cross-Engine SVG Layering Tests
    [Fact]
    public void TestCrossEngineSvgDrawing()
    {
        var canvas = new SkiaCanvas(600, 600);
        var ctx = canvas.GetContext("2d");

        // Background in Canvas 2D
        ctx.FillStyle = "#0f172a";
        ctx.FillRect(0, 0, 600, 600);

        // Vector SVG object created via Snap
        var paper = Snap.Create(300, 300);
        paper.Circle(150, 150, 100).Attr("fill", "#6366f1");
        paper.Rect(100, 100, 100, 100).Attr("fill", "#ec4899");

        // Draw SVG onto 2D Canvas
        ctx.DrawSvg(paper, 150, 150, 300, 300);

        var imgBytes = canvas.ToImageBytes();
        Assert.NotNull(imgBytes);
        Assert.True(imgBytes.Length > 0);
    }
    #endregion

    #region Sandboxed Jint JavaScript Canvas Execution Tests
    [Fact]
    public void TestJsCanvasScriptExecution()
    {
        var engine = new JsDrawingEngine();
        var jsCode = @"
            var canvas = createCanvas(800, 600);
            var ctx = canvas.getContext('2d');

            // Draw radial background
            var radGrad = ctx.createRadialGradient(400, 300, 50, 400, 300, 350);
            radGrad.addColorStop(0, '#312e81');
            radGrad.addColorStop(1, '#0f172a');
            ctx.fillStyle = radGrad;
            ctx.fillRect(0, 0, 800, 600);

            // Draw generative circles with globalCompositeOperation
            ctx.globalCompositeOperation = 'screen';
            var colors = ['#f43f5e', '#8b5cf6', '#06b6d4', '#10b981'];
            for (var i = 0; i < 4; i++) {
                ctx.beginPath();
                ctx.arc(300 + i * 60, 300, 70, 0, Math.PI * 2);
                ctx.fillStyle = colors[i];
                ctx.globalAlpha = 0.7;
                ctx.fill();
            }

            console.log('2D Canvas scene generated successfully');
            canvas;
        ";

        var result = engine.Execute(jsCode, 800, 600);
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ImageBytes);
        Assert.True(result.ImageBytes.Length > 0);
        Assert.Contains("[LOG] 2D Canvas scene generated successfully", result.Logs);
    }

    [Fact]
    public void TestJsSkiaProceduralNoiseScene()
    {
        var engine = new JsDrawingEngine();
        var jsCode = @"
            var canvas = createCanvas(600, 600);
            var ctx = canvas.getContext('2d');

            // Apply Skia procedural perlin noise
            ctx.fillStyle = Skia.Shader.perlinNoiseTurbulence(0.04, 0.04, 3, 999);
            ctx.fillRect(0, 0, 600, 600);

            // Add text overlay
            ctx.font = 'bold 36px Arial';
            ctx.fillStyle = '#ffffff';
            ctx.textAlign = 'center';
            ctx.shadowColor = 'rgba(0,0,0,0.8)';
            ctx.shadowBlur = 8;
            ctx.fillText('Procedural Terrain', 300, 300);

            canvas;
        ";

        var result = engine.Execute(jsCode, 600, 600);
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ImageBytes);
        Assert.True(result.ImageBytes.Length > 0);
    }
    #endregion
}
