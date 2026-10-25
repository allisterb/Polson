namespace Polson.Tests.ExtendedMind;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using global::Polson.ExtendedMind.Taste;
using global::Polson.Tests;
using SkiaSharp;
using Xunit;

/// <summary>
/// <c>Taste.images</c>: Qloo's pictures of entities this session's answers named, fetched only from Qloo's host and
/// measured on arrival, and the visual-research readings over them. No live calls: a handler stands in for
/// images.qloo.com, and every picture is drawn here.
/// </summary>
public class TasteImagesTests : TestsRuntime
{
    #region Methods
    [Fact]
    public async Task APictureArrivesMeasuredAndASecondAskIsFree()
    {
        var host = new FakeHost(_ => Png(SKColors.Red));
        var (taste, registry) = Toolkit(host, Entity("R-1", "Redbrand", "urn:entity:brand", Url("r")));

        var first = await taste.Images("R-1");
        var again = await taste.Images("R-1");

        var red = Assert.Single(first);
        Assert.True(red.Success, red.Error);
        Assert.Equal("Redbrand", red.Name);
        Assert.Equal("t1", red.Answer);
        Assert.Equal(64, red.Width);
        Assert.Equal(29.2, (double)red.Lead!["hue"]!, 0);                  // OKLCH puts sRGB red near 29°
        Assert.Equal(0.75, red.Neutral, 2);                                 // three quarters white ground
        Assert.StartsWith("data:image/png;base64,", red.ToDataUri());
        Assert.True(Assert.Single(again).FromCache);
        Assert.Equal(1, host.Calls);
        Assert.Equal(1, registry.ImagesFetched);
    }

    [Fact]
    public async Task OnlyEntitiesAnAnswerNamedAreFetched()
    {
        var host = new FakeHost(_ => Png(SKColors.Red));
        var (taste, _) = Toolkit(host, Entity("R-1", "Redbrand", "urn:entity:brand", Url("r")));

        var made = Assert.Single(await taste.Images(new object?[] { new Dictionary<string, object?> { ["id"] = "invented", ["properties"] =
            new Dictionary<string, object?> { ["image"] = new Dictionary<string, object?> { ["url"] = Url("x") } } } }));

        Assert.Equal(TasteImageFailure.UnknownEntity, made.Failure);
        Assert.Equal(0, host.Calls);
    }

    [Theory]
    [InlineData("https://evil.example/i/x.jpg")]
    [InlineData("http://images.qloo.com/i/x.jpg")]                         // not https
    [InlineData("https://images.qloo.com:8443/i/x.jpg")]                   // not the default port
    [InlineData("https://images.qloo.com.evil.example/i/x.jpg")]
    public async Task AnAddressOffQloosHostIsRefusedUnfetched(string url)
    {
        var host = new FakeHost(_ => Png(SKColors.Red));
        var (taste, _) = Toolkit(host, Entity("E-1", "Elsewhere", "urn:entity:brand", url));

        Assert.Equal(TasteImageFailure.BlockedHost, Assert.Single(await taste.Images("E-1")).Failure);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task ARedirectIsNotFollowed()
    {
        var host = new FakeHost(_ =>
        {
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data");
            return moved;
        });
        var (taste, _) = Toolkit(host, Entity("M-1", "Moved", "urn:entity:brand", Url("m")));

        var moved = Assert.Single(await taste.Images("M-1"));

        Assert.Equal(TasteImageFailure.BlockedHost, moved.Failure);
        Assert.Contains("169.254.169.254", moved.Error);
        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public async Task ABodyPastTheCeilingIsRefusedWithoutALengthToGoBy()
    {
        // StreamContent declares no Content-Length, so only counting the bytes as they arrive can stop it.
        var host = new FakeHost(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new MemoryStream(new byte[QlooImageSource.MaxBytes + 1])) });
        var (taste, _) = Toolkit(host, Entity("B-1", "Big", "urn:entity:brand", Url("b")));

        Assert.Equal(TasteImageFailure.TooLarge, Assert.Single(await taste.Images("B-1")).Failure);
    }

    [Fact]
    public async Task AnErrorPageIsUndecodableNotABlankPicture()
    {
        var host = new FakeHost(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>gateway</html>") });
        var (taste, _) = Toolkit(host, Entity("H-1", "Html", "urn:entity:brand", Url("h")));

        var page = Assert.Single(await taste.Images("H-1"));

        Assert.Equal(TasteImageFailure.Undecodable, page.Failure);
        Assert.Empty(page.ToDataUri());
    }

    [Fact]
    public async Task TheTypeOptionKeepsOneDomainOfAProfile()
    {
        var host = new FakeHost(_ => Png(SKColors.Blue));
        var (taste, _) = Toolkit(host,
            Entity("F-1", "A film", "urn:entity:movie", Url("f")), Entity("B-1", "A brand", "urn:entity:brand", Url("b")));

        var films = await taste.Images(taste.Get("t1")!, new System.Dynamic.ExpandoObject().With("type", "film"));

        Assert.Equal(["A film"], films.Select(f => f.Name));
    }

    [Fact]
    public void TheColourFieldFindsTheCrowdAndTheGaps()
    {
        TasteImage Led(string name, double? hue) => new()
        {
            Success = true, Name = name, Lightness = 0.5,
            Lead = hue is { } h ? new() { ["hue"] = h, ["chroma"] = 0.1, ["concentration"] = 0.9, ["color"] = "#000000" } : null,
        };

        var field = TasteVisual.ColourField([Led("a", 250), Led("b", 262), Led("c", 245), Led("d", 30), Led("e", null)]);

        Assert.Equal(5, field["n"]);
        Assert.Equal(["blue 240–270°"], (object?[])field["crowded"]!);
        Assert.Equal(1, ((Dictionary<string, object?>)field["neutral"]!)["count"]);
        Assert.Equal(10, ((object?[])field["open"]!).Length);
        Assert.Contains("crowded in blue 240–270° (3: a, b, c)", (string)field["summary"]!);
    }

    [Fact]
    public void TheVocabularyCountsEachWordOncePerEntityAndSkipsTheScaffolding()
    {
        var words = TasteVisual.Vocabulary([
            ("One", "A clean, minimalist aesthetic with clean typography."),
            ("Two", "The brand features a clean and playful identity."),
            ("Three", "Playful, hand-drawn packaging."),
        ]).Cast<Dictionary<string, object?>>().ToArray();

        Assert.Equal("clean", words[0]["word"]);
        Assert.Equal(2, words[0]["count"]);                                // "clean" twice in One is still One
        Assert.Contains(words, w => (string)w["word"]! == "playful");
        Assert.DoesNotContain(words, w => (string)w["word"]! is "brand" or "features" or "aesthetic");
    }

    [Theory]
    [InlineData(255, 255, 255, 1.0, 0.0)]
    [InlineData(0, 0, 0, 0.0, 0.0)]
    [InlineData(0, 0, 255, 0.452, 0.313)]
    public void OklchMatchesOttossonsReference(byte r, byte g, byte b, double lightness, double chroma)
    {
        var (L, C, _) = TasteColour.Oklch(r, g, b);

        Assert.Equal(lightness, L, 2);
        Assert.Equal(chroma, C, 2);
    }

    [Fact]
    public async Task APictureCarriesItsYearAndItsPlaceInTheAnswer()
    {
        var film = Entity("F-1", "A film", "urn:entity:movie", Url("f"));
        ((Dictionary<string, object?>)film["properties"]!)["release_year"] = 2019L;
        var ranked = Entity("F-2", "A ranked film", "urn:entity:movie", Url("g"));
        ranked["audienceRank"] = 7;
        var (taste, _) = Toolkit(new FakeHost(_ => Png(SKColors.Teal)), Entity("B-1", "A brand", "urn:entity:brand", Url("b")), film, ranked);

        var pictures = await taste.Images(taste.Get("t1")!);

        Assert.Equal([1, 2, 7], pictures.Select(p => p.Rank));              // a profile's audienceRank wins over position
        Assert.Equal([null, 2019, null], pictures.Select(p => p.Year));
        Assert.All(pictures, p => Assert.Matches("^#[0-9A-F]{6}$", (string)p.Lead!["color"]!));
    }

    /// <summary>A concept is measured by the same code as a Qloo picture, so the two can stand on one matrix.</summary>
    [Fact]
    public async Task APictureOfYourOwnIsMeasuredAsAQlooPictureIs()
    {
        var png = (await Png(SKColors.Red).Content.ReadAsByteArrayAsync());
        var (taste, _) = Toolkit(new FakeHost(_ => Png(SKColors.Red)), Entity("R-1", "Redbrand", "urn:entity:brand", Url("r")));
        var qloo = Assert.Single(await taste.Images("R-1"));

        var mine = taste.Measure(new Lossless(png));
        var viaUri = taste.Measure("data:image/png;base64," + Convert.ToBase64String(png));

        Assert.Equal(qloo.Lightness, (double)mine["lightness"]!);
        Assert.Equal(qloo.Chroma, (double)mine["chroma"]!);
        Assert.Equal(qloo.Neutral, (double)mine["neutral"]!);
        Assert.Equal(mine["chroma"], viaUri["chroma"]);
        Assert.True(qloo.Chroma is > 0.02 and < 0.1);                     // a quarter red, the rest white
        Assert.Contains("canvas.bitmap", Assert.Throws<ArgumentException>(() => taste.Measure(42)).Message);
    }

    private sealed class Lossless(byte[] png) : global::Polson.ILosslessImageSource
    {
        public byte[] ToLosslessBytes() => png;
    }

    [Fact]
    public void AFinishedColourIsPlacedInTheFieldsOwnSectors()
    {
        var taste = new TasteToolkit(new TasteRegistry());

        Assert.Equal("blue 240–270°", taste.Oklch("#0000ff")["sector"]);   // OKLCH blue is 264°, not HSL's 240°
        Assert.Equal("pink-red 0–30°", taste.Oklch("#f00")["sector"]);
        Assert.Equal(true, taste.Oklch("#808080")["neutral"]);
        Assert.Null(taste.Oklch("#808080")["sector"]);
        Assert.Throws<ArgumentException>(() => taste.Oklch("blue"));
    }

    private static (TasteToolkit, TasteRegistry) Toolkit(FakeHost host, params Dictionary<string, object?>[] entities)
    {
        var registry = new TasteRegistry();
        registry.Add(id => new TasteResult { Id = id, Op = "profile", Ok = true, Entities = [.. entities] });
        var cache = Path.Combine(Path.GetTempPath(), "polson-qloo-images-" + Guid.NewGuid().ToString("N"));
        return (new TasteToolkit(registry, images: new QlooImageSource(cache, host)), registry);
    }

    private static Dictionary<string, object?> Entity(string id, string name, string type, string url) => new()
    {
        ["id"] = id, ["name"] = name, ["type"] = type, ["types"] = new[] { type },
        ["properties"] = new Dictionary<string, object?>
        {
            ["image"] = new Dictionary<string, object?> { ["url"] = url },
            ["style_description"] = $"{name}'s look.",
        },
    };

    private static string Url(string name) => $"https://images.qloo.com/i/{name}-420x-auto.jpg";

    /// <summary>A 64 px square: a quarter of it <paramref name="colour"/>, the rest white.</summary>
    private static HttpResponseMessage Png(SKColor colour)
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = colour };
            canvas.DrawRect(0, 0, 32, 32, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data.ToArray()) };
    }

    private sealed class FakeHost(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }
    #endregion
}

internal static class ExpandoExtensions
{
    /// <summary>A JS options object as Jint hands it over: an <see cref="System.Dynamic.ExpandoObject"/>.</summary>
    public static System.Dynamic.ExpandoObject With(this System.Dynamic.ExpandoObject o, string key, object? value)
    {
        ((IDictionary<string, object?>)o)[key] = value;
        return o;
    }
}
