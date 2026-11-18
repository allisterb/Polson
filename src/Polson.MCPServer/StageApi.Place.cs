namespace Polson.MCPServer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SkiaSharp;
using Svg;
using Polson.Drawing.Skia;
using Polson.Drawing.Svg;

/// <summary><c>Stage.place</c>: a supplied picture — a logo, a photograph — fitted into a box on a paper, never stretched,
/// with how sharp it will be on the deliverable recorded as a check.</summary>
public sealed partial class StageApi
{
    #region Constants
    /// <summary>The claim <c>Stage.place</c> records for each picture it places; <c>Stage.check</c> refuses to write it.</summary>
    internal const string PlacedPrefix = "placed sharp: ";

    /// <summary>Source pixels per pixel of the deliverable below which a placed picture is enlarged, and so soft. A hair under
    /// 1, so a box rounded to a whole unit does not fail a picture drawn at its own size.</summary>
    private const double SharpRatio = 0.98;

    /// <summary>Under this share of a picture kept by a <c>cover</c> crop, the crop is said: most of the picture is gone.</summary>
    private const double CropWarning = 0.5;

    /// <summary>The claim for a fitted picture's ink against what is behind it; <c>Stage.check</c> refuses to write it.</summary>
    internal const string GroundPrefix = "placed against its ground: ";

    /// <summary>WCAG's least for large type and graphics, which a logo is: half its ink must stand this far off its ground.</summary>
    private const double GroundContrast = 3;

    /// <summary>The longest side the paper is rendered at to see what is behind a picture: enough for a 1920 screen at 1:1.</summary>
    private const double MaxProbeSide = 2000;

    /// <summary>A picture with at least this share of clear pixels has no ground of its own, so what is behind it shows.</summary>
    private const double ClearShare = 0.05;
    #endregion

    #region Methods
    /// <summary>
    /// Fits a picture into a box on <paramref name="target"/> (a paper or a group) and records whether it will be sharp.
    /// <paramref name="source"/> is a project path — <c>.svg</c> stays vector; <c>.png</c>, <c>.jpg</c> or <c>.webp</c> is
    /// inlined — or a bitmap or canvas. <paramref name="box"/> is <c>{ x, y, width, height }</c> in paper units.
    /// <paramref name="options"/>: <c>{ fit: 'contain' | 'cover', align: [ax, ay], focus: [fx, fy], scale }</c>.
    /// Returns <c>{ element, x, y, width, height, sourceWidth, sourceHeight, vector, ratio, sharp, kept, scale, warnings }</c>.
    /// </summary>
    /// <remarks>
    /// <c>contain</c> (the default, and a logo's) shows the whole picture inside the box, placed by <c>align</c> (0–1 on
    /// each axis, centred by default). <c>cover</c> (a photograph's) fills the box and crops the rest, keeping the point
    /// <c>focus</c> (0–1 across the picture) as near the centre as the edges allow. Neither ever changes the picture's
    /// shape. <c>scale</c> is the deliverable's pixels per paper unit; left out, it is read from the brief's sizes that
    /// share this paper's shape, or 1.
    /// </remarks>
    public Dictionary<string, object?> Place(object? target, object? source, object? box, object? options = null)
    {
        if (target is not SnapElement into)
            throw new ArgumentException("Stage.place's first argument is the paper (or a group on it) to draw into: Stage.place(paper, 'documents/logo.svg', { x, y, width, height }).");
        var (bx, by, bw, bh) = PlaceBox(box);
        var o = JsInterop.AsDict(options);
        if (options is not null && o is null) throw new ArgumentException("Stage.place's fourth argument is an options object: { fit, align, focus, scale }.");
        foreach (var key in o?.Keys.Cast<object>().Select(k => k.ToString()) ?? [])
            if (key is not ("fit" or "align" or "focus" or "scale"))
                throw new ArgumentException($"Stage.place has no option '{key}'. It takes fit ('contain' or 'cover'), align ([ax, ay]), focus ([fx, fy]) and scale.");
        var fit = o?["fit"]?.ToString() ?? "contain";
        if (fit is not ("contain" or "cover"))
            throw new ArgumentException($"Stage.place's fit is 'contain' (the whole picture, inside the box: a logo) or 'cover' (the box filled, the rest cropped: a photograph), not '{fit}'.");
        var (ax, ay) = Pair(o?["align"], "align");
        var (fx, fy) = Pair(o?["focus"], "focus");

        var warnings = new List<string>();
        var (name, picture) = PlaceSource(source, warnings);
        try
        {
            var (iw, ih) = picture.Size;
            // Contain fits the long side; cover the short one. Either way one factor, so the shape never changes.
            var s = fit == "contain" ? Math.Min(bw / iw, bh / ih) : Math.Max(bw / iw, bh / ih);
            double dw = iw * s, dh = ih * s, dx = bx + (bw - dw) * ax, dy = by + (bh - dh) * ay;
            double cx = 0, cy = 0, cw = iw, ch = ih;
            if (fit == "cover")
            {
                (dx, dy, dw, dh) = (bx, by, bw, bh);
                (cw, ch) = (bw / s, bh / s);
                cx = Math.Clamp(fx * iw - cw / 2, 0, iw - cw);
                cy = Math.Clamp(fy * ih - ch / 2, 0, ih - ch);
            }
            var kept = cw * ch / (iw * ih);
            if (kept < CropWarning)
                warnings.Add($"{name}: the crop keeps {kept:P0} of the picture. A band or a panel the picture's own shape fits may serve it better than a box it has to be cut down to.");

            // A fitted picture is usually a logo: what is behind it is rendered before it is drawn, so its ink can be held
            // against its ground and its lines of lettering measured (Look). A cropped photograph is neither.
            var paperOf = into.Paper;
            var probe = paperOf is { Width: > 0, Height: > 0 } p0 ? Math.Min(1, MaxProbeSide / Math.Max(p0.Width, p0.Height)) : 0;
            using var ground = fit == "contain" && probe > 0
                ? SvgRenderPipeline.RenderToBitmap(paperOf!.Document, Math.Max(1, (int)Math.Round(paperOf.Width * probe)),
                    Math.Max(1, (int)Math.Round(paperOf.Height * probe)), SKColors.White)
                : null;

            var element = picture.Draw(into, dx, dy, dw, dh, cx, cy, cw, ch);

            var (scale, scaleNote) = PlaceScale(o?["scale"], into.Paper);
            var look = ground is null ? null : Look(picture, dx, dy, dw, dh, ground, probe, scale);
            if (scaleNote is not null) warnings.Add(scaleNote);
            // Source pixels behind each pixel of the deliverable: the vector's own, or the smallest raster inside an SVG.
            var ratio = picture.Pixels is { } px ? px / (s * scale) : (double?)null;
            var sharp = ratio is null or >= SharpRatio;
            var drawnW = dw * scale;
            var detail = ratio is null
                ? $"vector, drawn {drawnW:0} px wide on the deliverable: sharp at any size"
                : $"drawn {drawnW:0} × {dh * scale:0} px on the deliverable from {iw:0} × {ih:0}{(picture.Vector ? " (a raster inside the SVG)" : "")}: "
                  + $"{ratio:0.00} source px per px"
                  + (sharp ? "" : $", so it is enlarged {1 / ratio:0.0}× and will look soft. Draw it at most {dw * ratio.Value:0} paper units wide"
                      + $" ({dw * ratio.Value * scale:0} px on the deliverable), use it as a softened background, or ask the director for a larger file or a vector");

            if (projectRoot is not null && source is string path && session is not null)
                session.Placed.Add(Path.GetRelativePath(projectRoot, ProjectPath.Resolve(projectRoot, path, "source", "Read")).Replace('\\', '/'));
            events?.Append("place", session?.Stage, executionId, new Dictionary<string, object?>
            {
                ["file"] = name, ["fit"] = fit,
                ["box"] = new[] { Math.Round(bx, 2), Math.Round(by, 2), Math.Round(bw, 2), Math.Round(bh, 2) },
                ["drawn"] = new[] { Math.Round(dx, 2), Math.Round(dy, 2), Math.Round(dw, 2), Math.Round(dh, 2) },
                ["source"] = new[] { Math.Round(iw, 2), Math.Round(ih, 2) },
                ["vector"] = picture.Vector, ["ratio"] = ratio is { } r ? Math.Round(r, 3) : null, ["kept"] = Math.Round(kept, 3),
                ["scale"] = Math.Round(scale, 4),
                ["contrast"] = look?.Contrast is { } c ? Math.Round(c, 2) : null,
                ["lines"] = look?.Lines.Select(l => (object?)Math.Round(l.Px, 1)).ToArray(),
            });
            foreach (var w in warnings) result?.Logs.Add("[WARN] Stage.place: " + w);
            Verdict(Clean(PlacedPrefix + name, MaxNoteLength), sharp, detail, null, false);
            if (look?.Contrast is { } contrast)
            {
                var stands = contrast >= GroundContrast;
                Verdict(Clean(GroundPrefix + name, MaxNoteLength), stands,
                    $"half its ink stands {contrast:0.0} : 1 or more off what is behind it (mostly {look.Ink} on {look.Behind})"
                    + (stands ? "" : $"; {GroundContrast:0} : 1 is the least. Ink the colour of its ground vanishes: put it on a field or panel it "
                        + "stands off, use a version made for this ground if one was supplied, or change the ground — never recolour it. "
                        + "Draw the ground before placing, since this measures what is already drawn"), null, false);
            }

            return new()
            {
                ["element"] = element,
                ["x"] = dx, ["y"] = dy, ["width"] = dw, ["height"] = dh,
                ["sourceWidth"] = iw, ["sourceHeight"] = ih,
                ["vector"] = picture.Vector, ["ratio"] = ratio, ["sharp"] = sharp, ["kept"] = kept, ["scale"] = scale,
                ["contrast"] = look?.Contrast,
                ["lines"] = look?.Lines.Select(l => (object?)new Dictionary<string, object?>
                {
                    ["y"] = l.Y, ["height"] = l.Height, ["px"] = l.Px,
                }).ToArray(),
                ["smallestLine"] = look is { Lines.Count: > 0 } ? look.Lines.Min(l => l.Px) : null,
                ["warnings"] = warnings.Cast<object?>().ToList(),
            };
        }
        finally
        {
            picture.Dispose();
        }
    }

    /// <summary><c>{ x, y, width, height }</c>, each a number, the size above 0.</summary>
    private static (double X, double Y, double W, double H) PlaceBox(object? box)
    {
        var d = JsInterop.AsDict(box) ?? throw new ArgumentException("Stage.place's third argument is the box to fit into: { x, y, width, height }, in paper units.");
        double Num(string key) => d.Contains(key) && d[key] is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture)
            : throw new ArgumentException($"Stage.place's box has no {key}: it is {{ x, y, width, height }}, in paper units.");
        var (x, y, w, h) = (Num("x"), Num("y"), Num("width"), Num("height"));
        return w > 0 && h > 0 && double.IsFinite(x + y + w + h) ? (x, y, w, h)
            : throw new ArgumentException($"Stage.place's box needs a width and a height above 0, not {w} × {h}.");
    }

    /// <summary>An <c>[x, y]</c> option, each 0–1; the centre when absent.</summary>
    private static (double X, double Y) Pair(object? value, string name)
    {
        if (value is null) return (0.5, 0.5);
        if (value is System.Collections.IEnumerable list && value is not string && list.Cast<object?>().ToArray() is [var a, var b])
        {
            var (x, y) = (Convert.ToDouble(a, CultureInfo.InvariantCulture), Convert.ToDouble(b, CultureInfo.InvariantCulture));
            if (x is >= 0 and <= 1 && y is >= 0 and <= 1) return (x, y);
        }
        throw new ArgumentException($"Stage.place's {name} is [x, y], each 0 to 1 across the {(name == "focus" ? "picture" : "box")}: [0.5, 0.5] is the centre.");
    }

    /// <summary>
    /// The deliverable's pixels per paper unit: the option, or the widest of the brief's sizes that share the paper's shape
    /// over the paper's width, or 1 — with a note when the brief names sizes and none has this shape.
    /// </summary>
    private (double Scale, string? Note) PlaceScale(object? given, SnapPaper? paper)
    {
        if (given is not null)
        {
            var scale = Convert.ToDouble(given, CultureInfo.InvariantCulture);
            return scale > 0 && double.IsFinite(scale) ? (scale, null)
                : throw new ArgumentException($"Stage.place's scale is the deliverable's pixels per paper unit, above 0 (2 for a 2160-wide sign built at 1080), not {scale}.");
        }
        if (paper is not { Width: > 0, Height: > 0 } p || DrawingMcpTools.AskedSizes("final.png", projectRoot) is not { } asked) return (1, null);
        var shaped = asked.Where(a => Math.Abs((double)a.W / a.H - (double)p.Width / p.Height) < 0.01).ToArray();
        return shaped.Length > 0 ? (shaped.Max(a => a.W) / (double)p.Width, null)
            : (1, $"the brief's sizes ({string.Join(", ", DrawingMcpTools.SizeNames(asked))}) are not this paper's shape, so its sharpness is measured "
                + "at one pixel per unit: pass { scale } for the size it will be rendered at.");
    }

    /// <summary>The picture a source names, read once, with the name it is recorded under.</summary>
    private (string Name, PlacedPicture Picture) PlaceSource(object? source, List<string> warnings)
    {
        switch (source)
        {
            case SkiaBitmapWrapper b: return ("a bitmap", new PlacedRaster(b.Bitmap.Copy()));
            case SkiaCanvas c: return ("a canvas", new PlacedRaster(c.SkBitmap.Copy()));
            case string path when !string.IsNullOrWhiteSpace(path):
                var full = ProjectPath.Resolve(projectRoot, path, "source", "Read");
                if (!File.Exists(full)) throw new ArgumentException($"Stage.place: '{path}' is not in the project. A supplied file is in documents/, e.g. 'documents/logo.svg'.");
                var name = projectRoot is null ? path : Path.GetRelativePath(projectRoot, full).Replace('\\', '/');
                var bytes = File.ReadAllBytes(full);
                return Path.GetExtension(full).ToLowerInvariant() switch
                {
                    ".svg" => (name, PlacedVector.Read(bytes, name, warnings)),
                    ".png" or ".jpg" or ".jpeg" or ".webp" => (name, PlacedRaster.Read(bytes, name)),
                    var ext => throw new ArgumentException($"Stage.place draws an .svg, .png, .jpg or .webp, not '{ext}'. "
                        + (ext == ".pdf" ? "For a logo supplied as a PDF, ask the director for an SVG or a large PNG of it." : "")),
                };
            default:
                throw new ArgumentException("Stage.place's second argument is the picture: a project path such as 'documents/logo.svg', or a bitmap or canvas.");
        }
    }

    /// <summary>
    /// A fitted picture rendered alone over the same pixels as <paramref name="ground"/>, the paper as it was before it:
    /// its ink (opaque pixels; on a picture with no clear pixels, those unlike its border), the median contrast of that
    /// ink against what is behind it — only where the picture has clear pixels, since an opaque one carries its own
    /// ground — and its lines of ink, top to bottom, in paper units and in the deliverable's pixels.
    /// </summary>
    /// <remarks>
    /// francis_fashions_2 (2026-11-18) placed a black wordmark on the black it had sampled from that wordmark, and
    /// recorded its lettering 87 px tall where its capitals were 55: the padlock in the second line stands taller than the
    /// letters, and the first line was the smaller. Hence the median of the ink, and every line rather than one.
    /// </remarks>
    private static PlacedLook Look(PlacedPicture picture, double dx, double dy, double dw, double dh, SKBitmap ground, double probe, double scale)
    {
        int pw = Math.Max(1, (int)Math.Round(dw * probe)), ph = Math.Max(1, (int)Math.Round(dh * probe));
        var solo = new SnapPaper((float)dw, (float)dh);
        var (iw, ih) = picture.Size;
        picture.Draw(solo, 0, 0, dw, dh, 0, 0, iw, ih);
        using var alone = SvgRenderPipeline.RenderToBitmap(solo.Document, pw, ph);
        var a = alone.Pixels;
        var g = ground.Pixels;
        int gx = (int)Math.Round(dx * probe), gy = (int)Math.Round(dy * probe);

        var clear = a.Count(c => c.Alpha < 16) / (double)a.Length;
        var transparent = clear >= ClearShare;
        // An opaque picture's own ground is the colour its border mostly is; its ink is what differs from that.
        var border = transparent ? SKColors.Transparent : Mode(Enumerable.Range(0, pw).SelectMany(x => new[] { a[x], a[(ph - 1) * pw + x] }));
        bool Ink(SKColor c) => c.Alpha >= 128 && (transparent || Math.Abs(c.Red - border.Red) + Math.Abs(c.Green - border.Green) + Math.Abs(c.Blue - border.Blue) > 48);

        var rows = new int[ph];
        var contrasts = new List<double>();
        var inks = new List<SKColor>();
        var behinds = new List<SKColor>();
        for (var y = 0; y < ph; y++)
            for (var x = 0; x < pw; x++)
            {
                var c = a[y * pw + x];
                if (!Ink(c)) continue;
                rows[y]++;
                if (!transparent) continue;
                int u = gx + x, v = gy + y;
                if (u < 0 || v < 0 || u >= ground.Width || v >= ground.Height) continue;
                var b = g[v * ground.Width + u];
                contrasts.Add(Contrast(c, b));
                inks.Add(c);
                behinds.Add(b);
            }

        // A line of ink is a run of rows each holding a fifth of the busiest row's ink, so an icon standing between two lines
        // of lettering (francis_fashions_2's padlock) does not join them into one; a run under 3 rows is a rule or a speck.
        var lines = new List<(double Y, double Height, double Px)>();
        var least = Math.Max(2, Math.Max(pw / 100, rows.Max() / 5));
        for (int y = 0, start = -1; y <= ph; y++)
        {
            var inked = y < ph && rows[y] >= least;
            if (inked && start < 0) start = y;
            if (!inked && start >= 0)
            {
                if (y - start >= 3)
                    lines.Add((Math.Round(dy + start / probe, 2), Math.Round((y - start) / probe, 2), Math.Round((y - start) / probe * scale, 1)));
                start = -1;
            }
        }

        double? median = contrasts.Count == 0 ? (transparent ? 1 : null) : contrasts.Order().ElementAt(contrasts.Count / 2);
        return new(median, Hex(inks.Count > 0 ? Mode(inks) : SKColors.Transparent), Hex(behinds.Count > 0 ? Mode(behinds) : SKColors.White), lines);

        static string Hex(SKColor c) => $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";
    }

    /// <summary>The commonest colour, bucketed to 16 levels a channel so anti-aliasing does not split it.</summary>
    private static SKColor Mode(IEnumerable<SKColor> colours) =>
        colours.GroupBy(c => (c.Red >> 4, c.Green >> 4, c.Blue >> 4)).MaxBy(group => group.Count())!.First();

    /// <summary>WCAG 2 contrast between two colours, alpha ignored.</summary>
    private static double Contrast(SKColor a, SKColor b)
    {
        static double L(SKColor c)
        {
            static double Lin(byte v) => v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            return 0.2126 * Lin(c.Red) + 0.7152 * Lin(c.Green) + 0.0722 * Lin(c.Blue);
        }
        var (la, lb) = (L(a), L(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
    #endregion

    #region Child types
    /// <summary>What <see cref="Look"/> measured: the median contrast of the ink against its ground (null for an opaque
    /// picture), the commonest ink and ground colours, and the lines of ink.</summary>
    private sealed record PlacedLook(double? Contrast, string Ink, string Behind, List<(double Y, double Height, double Px)> Lines);

    /// <summary>A picture to place: its size in its own units, and its pixels across, or null when it is all vector.</summary>
    private abstract class PlacedPicture : IDisposable
    {
        public abstract (double W, double H) Size { get; }

        /// <summary>Source pixels per unit of <see cref="Size"/>; null for pure vector.</summary>
        public abstract double? Pixels { get; }

        public abstract bool Vector { get; }

        /// <summary>Draws the crop (<paramref name="cx"/>, <paramref name="cy"/>, <paramref name="cw"/>, <paramref name="ch"/>, in
        /// source units) into the rectangle (<paramref name="x"/>, <paramref name="y"/>, <paramref name="w"/>, <paramref name="h"/>).</summary>
        public abstract SnapElement Draw(SnapElement into, double x, double y, double w, double h, double cx, double cy, double cw, double ch);

        public virtual void Dispose() { }
    }

    private sealed class PlacedRaster(SKBitmap bitmap) : PlacedPicture
    {
        public static PlacedRaster Read(byte[] bytes, string name)
        {
            // The size first, from the header, so a hostile file cannot make the decoder allocate past the engine's cap.
            using (var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new ArgumentException($"Stage.place: {name} is not a picture this engine can read."))
                Polson.Drawing.Svg.ScriptLimits.CheckRaster(codec.Info.Width, codec.Info.Height, $"Stage.place: {name}");
            return new(SKBitmap.Decode(bytes) ?? throw new ArgumentException($"Stage.place: {name} could not be decoded."));
        }

        public override (double W, double H) Size => (bitmap.Width, bitmap.Height);
        public override double? Pixels => 1;
        public override bool Vector => false;

        public override SnapElement Draw(SnapElement into, double x, double y, double w, double h, double cx, double cy, double cw, double ch)
        {
            // A crop is cut from the pixels, so the deliverable carries only what shows; the shape matches the box already.
            // Not disposed here: the wrapper would dispose the bitmap, which Dispose below owns.
            var whole = new SkiaBitmapWrapper(bitmap);
            var shown = cw < bitmap.Width || ch < bitmap.Height
                ? whole.ExtractSubset((int)Math.Round(cx), (int)Math.Round(cy), Math.Max(1, (int)Math.Round(cw)), Math.Max(1, (int)Math.Round(ch)))
                : whole;
            // The pixels are inlined as the element is made, so the crop can go at once.
            var image = into.Image(shown, (float)x, (float)y, (float)w, (float)h);
            ((SvgImage)image.Node).AspectRatio = new SvgAspectRatio(SvgPreserveAspectRatio.none);
            if (!ReferenceEquals(shown, whole)) shown.Dispose();
            return image;
        }

        public override void Dispose() => bitmap.Dispose();
    }

    private sealed class PlacedVector(SvgDocument document, SvgViewBox view, double? pixels) : PlacedPicture
    {
        public static PlacedVector Read(byte[] bytes, string name, List<string> warnings)
        {
            ScriptLimits.Ensure();
            SvgDocument document;
            try
            {
                using var stream = new MemoryStream(bytes);
                document = SvgDocument.Open<SvgDocument>(stream);
            }
            catch (Exception e) when (e is System.Xml.XmlException or InvalidOperationException or ArgumentException)
            {
                throw new ArgumentException($"Stage.place: {name} is not an SVG this engine can read ({e.Message}). Ask for the logo as a PNG instead.");
            }
            var view = document.ViewBox is { Width: > 0, Height: > 0 } vb ? vb
                : document.Width is { Type: not SvgUnitType.Percentage, Value: > 0 } && document.Height is { Type: not SvgUnitType.Percentage, Value: > 0 }
                    ? new SvgViewBox(0, 0, document.Width.Value, document.Height.Value)
                    : throw new ArgumentException($"Stage.place: {name} gives no size — no viewBox and no width and height — so it cannot be fitted. Ask for it with a viewBox, or as a PNG.");

            // An SVG may be a raster in a wrapper. Its sharpness is then that raster's: the fewest pixels per unit among them.
            double? pixels = null;
            foreach (var image in document.Descendants().OfType<SvgImage>())
            {
                var href = image.Href ?? "";
                var at = href.IndexOf(";base64,", StringComparison.Ordinal);
                if (!href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || at < 0)
                {
                    warnings.Add($"{name} refers to a picture outside itself ('{(href.Length > 60 ? href[..60] + "…" : href)}'), which is not drawn.");
                    continue;
                }
                try
                {
                    using var codec = SKCodec.Create(new SKMemoryStream(Convert.FromBase64String(href[(at + 8)..])));
                    if (codec is null || image.Width.Value <= 0) continue;
                    var perUnit = codec.Info.Width / image.Width.Value;
                    pixels = Math.Min(pixels ?? double.MaxValue, perUnit);
                }
                catch (FormatException) { }
            }
            if (pixels is not null)
                warnings.Add($"{name} is a raster picture inside an SVG wrapper, not vector shapes: it is only as sharp as that picture.");
            return new(document, view, pixels);
        }

        public override (double W, double H) Size => (view.Width, view.Height);
        public override double? Pixels => pixels;
        public override bool Vector => true;

        public override SnapElement Draw(SnapElement into, double x, double y, double w, double h, double cx, double cy, double cw, double ch)
        {
            // A nested <svg> whose viewBox is the crop, in the source's own units, and whose viewport is the drawn rectangle:
            // the same shape, so nothing is stretched, and a nested viewport clips what the crop leaves out.
            var fragment = new SvgFragment
            {
                X = new SvgUnit((float)x), Y = new SvgUnit((float)y), Width = new SvgUnit((float)w), Height = new SvgUnit((float)h),
                ViewBox = new SvgViewBox((float)(view.MinX + cx), (float)(view.MinY + cy), (float)cw, (float)ch),
                AspectRatio = new SvgAspectRatio(SvgPreserveAspectRatio.none),
            };
            foreach (var child in document.Children) fragment.Children.Add(child.DeepCopy());
            var element = new SnapElement(fragment, into.Paper);
            into.Node.Children.Add(fragment);
            return element;
        }
    }
    #endregion
}
