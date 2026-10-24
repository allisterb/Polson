namespace Polson.Tests.ExtendedMind;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Polson.ExtendedMind.ImageGeneration;
using SkiaSharp;
using Xunit;

/// <summary>
/// A repeated cutout is free in time as well as money: the second call in a session is served keyed and split from memory,
/// says it came from the cache, and a one-view cutout answers <c>cell(view)</c> with its one cell.
/// </summary>
/// <remarks>nightshift1 (2026-10-24) measured 8–21 s for cache hits inside 30-second scripts, and found <c>fromCache</c> undefined.</remarks>
public class CutoutMemoTests : TestsRuntime, IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "polson-cutout-memo-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public async Task TestARepeatComesFromMemoryAndSaysItCameFromTheCache()
    {
        var generator = new CountingGenerator(Sheet());
        var toolkit = new AssetRequisitionToolkit(generator, new RequisitionCache(Path.Combine(dir, "cache")), new AssetBudget(5), "test");

        var first = await toolkit.Cutout("a plain grey block, no text");
        var second = await toolkit.Cutout("a plain grey block, no text");

        Assert.True(first.Success && second.Success, first.Error ?? second.Error);
        Assert.Equal(1, generator.Calls);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Same(first.Bytes, second.Bytes);                  // keyed and encoded once, not again
        Assert.Same(second.Cells[0], second.Cell("front"));     // one view: any view's name finds it
    }

    private static byte[] Sheet()
    {
        using var bitmap = new SKBitmap(120, 120);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0xFF, 0x00, 0xFF));
            using var paint = new SKPaint { Color = new SKColor(128, 128, 128) };
            canvas.DrawRect(SKRect.Create(30, 20, 60, 80), paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class CountingGenerator(byte[] png) : IImageGenerator
    {
        public int Calls { get; private set; }

        public string Model => "fake-image-model";

        public Task<ImageGenerationResult> GenerateImage(string prompt, string? model = null, string? aspectRatio = null,
            IReadOnlyList<byte[]>? conditionOn = null, CancellationToken ct = default)
        {
            Calls++;
            ImageGenerator.TryReadPngSize(png, out var width, out var height);
            return Task.FromResult(new ImageGenerationResult
            {
                Success = true, ImageBytes = png, Width = width, Height = height, MimeType = "image/png",
                Model = model ?? Model, Prompt = prompt, Hash = ImageGenerator.HashOf(model ?? Model, prompt, aspectRatio, conditionOn),
                GeneratedUtc = DateTime.UtcNow, ElapsedMs = 1, Charged = true,
            });
        }
    }
}
