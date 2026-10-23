namespace Polson.Drawing.Skia;

using System;
using System.Collections.Generic;

/// <summary>
/// Equations from LaTeX, measured without drawing: <c>Equation.parse(latex, { size })</c>.
/// </summary>
/// <remarks>
/// Exposed to the JavaScript sandbox. Members follow .NET naming here; Jint resolves the JS camelCase spelling onto them,
/// so a script calling <c>Equation.parse(...)</c> reaches <c>Parse(...)</c>. Named <c>Equation</c>, not <c>Math</c>, which
/// would shadow JavaScript's own. Drawing is <c>ctx.fillEquation(...)</c>; this answers what an equation says and how big
/// it is before anything is placed, as <c>ctx.measureWrappedText</c> does for a paragraph.
/// </remarks>
public sealed class EquationToolkit
{
    #region Methods
    /// <summary>
    /// The equation parsed and typeset: <c>{ ok, latex, linear, width, height, ascent, descent, size }</c>, or
    /// <c>{ ok: false, error }</c> naming what the parser refused. <paramref name="options"/>: <c>{ size? }</c>, the em in
    /// canvas units (default 32).
    /// </summary>
    public Dictionary<string, object?> Parse(string latex, object? options = null)
    {
        var size = Size(options, "Equation.parse");
        var (layout, error) = EquationLayout.Parse(latex, size);
        return layout is null
            ? new() { ["ok"] = false, ["latex"] = latex, ["error"] = error }
            : Describe(layout);
    }
    #endregion

    #region Internal methods
    internal static Dictionary<string, object?> Describe(EquationLayout layout) => new()
    {
        ["ok"] = true,
        ["latex"] = layout.Latex,
        ["linear"] = layout.Linear,
        ["width"] = Math.Round(layout.Width, 2),
        ["height"] = Math.Round(layout.Ascent + layout.Descent, 2),
        ["ascent"] = Math.Round(layout.Ascent, 2),
        ["descent"] = Math.Round(layout.Descent, 2),
        ["size"] = layout.Size,
    };

    internal static float Size(object? options, string method)
    {
        var o = JsInterop.AsDict(options);
        if (o is null) return DefaultSize;
        foreach (var key in o.Keys)
            if (key?.ToString() != "size") throw new ArgumentException($"{method}'s options are {{ size }}; '{key}' is not one.");
        return o["size"] is { } s ? Convert.ToSingle(s, System.Globalization.CultureInfo.InvariantCulture) : DefaultSize;
    }
    #endregion

    #region Fields
    internal const float DefaultSize = 32;
    #endregion
}
