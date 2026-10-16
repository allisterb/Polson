namespace Polson.MCPServer;

using System;
using System.Linq;
using Polson.Drawing.Svg;

/// <summary>
/// A request for a print PDF — size, bleed and safe margin — parsed and reported the same way by every caller.
/// </summary>
/// <remarks>
/// Shared by <c>ExecuteScript</c>'s <c>outPdf</c> and the CLI's <c>eval --pdf</c>, so the defaults, the refusals
/// and the line that reports the result cannot drift apart between the two. Each caller passes its own spelling
/// of the parameters, because a refusal should name the thing its reader typed.
/// </remarks>
public static class PrintPdfRequest
{
    #region Fields
    /// <summary>The MCP tool's parameter names.</summary>
    public static readonly Names Tool = new("outPdf", "pdfSize", "pdfBleed", "pdfSafeMargin");

    /// <summary>The CLI's option names.</summary>
    public static readonly Names Cli = new("--pdf", "--pdf-size", "--pdf-bleed", "--pdf-safe-margin");

    public const string DefaultBleed = "3mm";
    #endregion

    #region Methods
    /// <summary>The page, bleed and safe margin a request asks for.</summary>
    /// <exception cref="ArgumentException">The path is not a .pdf, no size was given, or a value cannot be read.</exception>
    public static (PrintSize Size, float BleedPt, float SafePt) Parse(string outPdf, string? size, string? bleed, string? safeMargin, Names names)
    {
        if (!outPdf.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{names.Out} '{outPdf}' must end in .pdf.");
        if (string.IsNullOrWhiteSpace(size))
            throw new ArgumentException(
                $"{names.Out} '{outPdf}' needs {names.Size} — the trimmed page the poster prints at, e.g. 'A2' or '18x24in'. "
              + "Every size check on the poster depends on it, so it is never assumed.");

        return (PrintSize.Parse(size),
                PrintSize.ParseLength(string.IsNullOrWhiteSpace(bleed) ? DefaultBleed : bleed, names.Bleed),
                PrintSize.ParseLength(
                    string.IsNullOrWhiteSpace(safeMargin) ? FormattableString.Invariant($"{SvgPdfWriter.DefaultSafeMarginMm}mm") : safeMargin,
                    names.Safe));
    }

    /// <summary>The one line that says what was written and how the paper's units print.</summary>
    public static string Summary(string outPdf, PdfPrintReport report, float safePt) =>
        FormattableString.Invariant(
            $"[PDF] {outPdf}: trim {report.Trim.Describe()}, bleed {report.BleedPt / PrintSize.PointsPerMm:0.#} mm, ")
      + (safePt > 0 ? FormattableString.Invariant($"text kept {safePt / PrintSize.PointsPerMm:0.#} mm inside the trim. ") : "safe margin not checked. ")
      + FormattableString.Invariant(
            $"One paper unit prints at {report.MillimetresPerUnit:0.####} mm, so a cap height of h units prints at h x {report.MillimetresPerUnit:0.####} mm.")
      + (report.Bitmaps.Count == 0 ? "" : FormattableString.Invariant(
            $" {report.Bitmaps.Count} bitmap(s) placed; the lowest prints at {report.Bitmaps.Min(b => b.EffectivePpi):0} ppi (floor {SvgPdfWriter.MinimumPpi:0})."));
    #endregion

    #region Types
    /// <summary>How one caller spells the four parameters.</summary>
    public sealed record Names(string Out, string Size, string Bleed, string Safe);
    #endregion
}
