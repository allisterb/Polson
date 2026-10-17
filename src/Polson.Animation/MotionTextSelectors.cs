// Ported from EffectCraft, crates/text/src/selectors.rs.
// Copyright (c) 2026 ArtCraft Team and the EffectCraft contributors.
// Licensed under the MIT License or the Apache License, Version 2.0, at your option; used here under MIT.
// See reference/README.md (the EffectCraft row) for the source and its terms.

namespace Polson.Animation;

using System;

/// <summary>
/// Text selector maths: how much each character, word or line is affected by an animator. Pure functions.
/// </summary>
/// <remarks>
/// <b>Ported from EffectCraft</b> (<c>crates/text/src/selectors.rs</c>, MIT OR Apache-2.0), whose behaviour follows
/// After Effects' public documentation of the Range and Wiggly selectors; the formulas are EffectCraft's. Selection is a
/// fraction: 1 is fully selected, and a negative value inverts the animated property's effect, as a negative amount does.
/// </remarks>
internal static class MotionTextSelectors
{
    #region Types
    public enum Shape { Square, RampUp, RampDown, Triangle, Round, Smooth }

    /// <summary>How a selector combines with the selectors above it.</summary>
    public enum Mode { Add, Subtract, Intersect, Min, Max, Difference }

    /// <summary>What a selector counts.</summary>
    public enum BasedOn { Characters, CharactersExcludingSpaces, Words, Lines }

    /// <summary>A range selector's parameters as fractions of the unit count, not percent.</summary>
    public readonly record struct Range(
        double Start = 0, double End = 1, double Offset = 0, double Amount = 1, Shape Shape = Shape.Square,
        double Smoothness = 1, double EaseHigh = 0, double EaseLow = 0);

    /// <summary>A wiggly selector's parameters; amounts as fractions, phases in degrees.</summary>
    public readonly record struct Wiggly(
        double Max = 1, double Min = -1, double WigglesPerSecond = 2, double Correlation = 0.5,
        double TemporalPhase = 0, double SpatialPhase = 0, bool LockDimensions = false, ulong Seed = 0);
    #endregion

    #region Methods
    /// <summary>Selection before the first selector: nothing for add, max and difference; everything for the rest.</summary>
    public static double Initial(Mode mode) => mode is Mode.Subtract or Mode.Intersect or Mode.Min ? 1 : 0;

    /// <summary>The selection so far combined with this selector's value, clamped to −1…1.</summary>
    public static double Combine(Mode mode, double acc, double v) => Math.Clamp(mode switch
    {
        Mode.Add => acc + v,
        Mode.Subtract => acc - v,
        Mode.Intersect => acc * v,
        Mode.Min => Math.Min(acc, v),
        Mode.Max => Math.Max(acc, v),
        _ => Math.Abs(acc - v),
    }, -1, 1);

    /// <summary>The shape's profile over the range, <paramref name="f"/> 0 at its start and 1 at its end.</summary>
    public static double ShapeValue(Shape shape, double f) => shape switch
    {
        Shape.Square => f is >= 0 and <= 1 ? 1 : 0,
        // Ramps hold their end values outside the range: a cascade animates the offset across it.
        Shape.RampUp => Math.Clamp(f, 0, 1),
        Shape.RampDown => 1 - Math.Clamp(f, 0, 1),
        _ when f is < 0 or > 1 => 0,
        Shape.Triangle => 1 - Math.Abs(2 * f - 1),
        Shape.Round => Math.Sqrt(Math.Max(0, 1 - Math.Pow(2 * f - 1, 2))),
        _ => Smoothstep(1 - Math.Abs(2 * f - 1)),
    };

    /// <summary>
    /// Ease high and low: positive values slow the change near the fully selected (high) or unselected (low) end, negative
    /// make it abrupt. A cubic Bézier with control values <c>(1 − low) / 3</c> and <c>1 − (1 − high) / 3</c>.
    /// </summary>
    public static double Ease(double v, double high, double low)
    {
        if (high == 0 && low == 0) return v;
        var t = Math.Clamp(v, 0, 1);
        var p1 = (1 - Math.Clamp(low, -1, 1)) / 3;
        var p2 = 1 - (1 - Math.Clamp(high, -1, 1)) / 3;
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    /// <summary>The selection of unit <paramref name="i"/> of <paramref name="n"/>, after any random reordering.</summary>
    public static double RangeValue(in Range r, int i, int n)
    {
        var count = Math.Max(n, 1);
        var (s, e) = r.Start > r.End ? (r.End, r.Start) : (r.Start, r.End);
        s += r.Offset;
        e += r.Offset;
        var unit = 1.0 / count;
        var c = (i + 0.5) * unit;

        double v;
        if (r.Shape == Shape.Square)
        {
            var w = Math.Clamp(r.Smoothness, 0, 1) * unit;
            double Edge(double d) => w > 1e-12 ? Math.Clamp(d / w + 0.5, 0, 1) : d >= 0 ? 1 : 0;
            v = e - s <= 1e-12 ? 0 : Math.Min(Edge(c - s), Edge(e - c));
        }
        else if (e - s <= 1e-12)
        {
            // A degenerate range: ramps step at the point, the other shapes select nothing.
            v = r.Shape switch { Shape.RampUp => c >= s ? 1 : 0, Shape.RampDown => c < s ? 1 : 0, _ => 0 };
        }
        else
        {
            v = Ease(ShapeValue(r.Shape, (c - s) / (e - s)), r.EaseHigh, r.EaseLow);
        }
        return v * r.Amount;
    }

    /// <summary>One SplitMix64 step: deterministic hashing for seeds and noise lattices.</summary>
    public static ulong SplitMix(ulong x)
    {
        unchecked
        {
            x += 0x9E37_79B9_7F4A_7C15;
            var z = x;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EB;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Randomize order: a seeded permutation, <c>p[i]</c> the position unit <c>i</c> takes.</summary>
    public static int[] RandomOrder(int n, ulong seed)
    {
        var p = new int[n];
        for (var i = 0; i < n; i++) p[i] = i;
        var s = SplitMix(seed ^ 0x5EED);
        for (var i = n - 1; i >= 1; i--)
        {
            s = SplitMix(s);
            var j = (int)(s % (ulong)(i + 1));
            (p[i], p[j]) = (p[j], p[i]);
        }
        return p;
    }

    /// <summary>Smooth 1-D value noise in −1…1: seeded lattice values under a quintic.</summary>
    public static double Noise1(double x, ulong seed, ulong channel)
    {
        var i = Math.Floor(x);
        var f = x - i;
        double Lattice(double k) => (Hash3(seed, channel, unchecked((ulong)(long)k)) >> 11) / (double)(1UL << 53) * 2 - 1;
        var a = Lattice(i);
        var b = Lattice(i + 1);
        var t = f * f * f * (f * (f * 6 - 15) + 10);
        return a + (b - a) * t;
    }

    /// <summary>The wiggly selection of unit <paramref name="i"/> at time <paramref name="t"/> for dimension <paramref name="dim"/>.</summary>
    public static double WigglyValue(in Wiggly w, double t, int i, int dim)
    {
        unchecked
        {
            var ch = w.LockDimensions ? 0UL : (ulong)dim;
            var tau = t * w.WigglesPerSecond + w.TemporalPhase / 360;
            var shared = Noise1(tau, w.Seed, ch * 2);
            // Each unit's own channel; the spatial phase slides the pattern along the units.
            var along = i * 0.61 + w.SpatialPhase / 360;
            var own = 0.5 * Noise1(tau + along, w.Seed ^ 0xA5A5, ch * 2 + 1)
                + 0.5 * Noise1(tau * 1.37 + along * 1.7, w.Seed ^ ((ulong)i + 1) * 0x9E37, ch);
            var c = Math.Clamp(w.Correlation, 0, 1);
            var v = Math.Clamp(c * shared + (1 - c) * Math.Clamp(own, -1, 1), -1, 1);
            return w.Min + (v + 1) * 0.5 * (w.Max - w.Min);
        }
    }

    private static ulong Hash3(ulong a, ulong b, ulong c)
    {
        unchecked
        {
            return SplitMix(SplitMix(SplitMix(a) ^ b * 0x632B_E59B_D9B4_E019) ^ c * 0x8CB9_2BA7_2F3D_8DD7);
        }
    }

    private static double Smoothstep(double t) => t * t * (3 - 2 * t);
    #endregion
}
