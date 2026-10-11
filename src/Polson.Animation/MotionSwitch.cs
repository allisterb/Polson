namespace Polson.Animation;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// Which layer a switch shows over time: one name, or names held from key to key. Names do not blend, so every key is a
/// hold, and the name changes at the key's time, as a <c>constant</c> waypoint does.
/// </summary>
internal sealed class MotionSwitchTrack
{
    #region Constructors
    private MotionSwitchTrack(IReadOnlyList<(double Time, string Name)> keys) => this.keys = keys;
    #endregion

    #region Fields
    private readonly IReadOnlyList<(double Time, string Name)> keys;
    #endregion

    #region Properties
    public IEnumerable<string> Names => keys.Select(k => k.Name).Distinct();
    #endregion

    #region Methods
    /// <summary>
    /// <c>active</c> as a script gives it: a name, or keys <c>[{ time, value }]</c> or <c>[[time, name]]</c>. An empty name
    /// shows nothing.
    /// </summary>
    public static MotionSwitchTrack Read(object? value, string who)
    {
        const string form = "a layer's name, or keys: [{ time, value: name }, ...] or [[time, name], ...]";
        switch (value)
        {
            case null: throw new ArgumentException($"{who} needs active: {form}.");
            case string name: return new([(0, name)]);
            case IList list when list.Count > 0:
                var keys = new List<(double, string)>();
                for (var i = 0; i < list.Count; i++)
                {
                    var at = $"{who}'s active key {i}";
                    var (time, name) = list[i] switch
                    {
                        IList pair when pair.Count == 2 => (pair[0], pair[1]),
                        var item when JsInterop.AsDict(item) is { } d => d.Keys.Cast<object>().Select(k => k.ToString()).FirstOrDefault(k => k is not ("time" or "value")) is { } extra
                            ? throw new ArgumentException($"{at} has no '{extra}'. A key is {{ time, value }}.")
                            : (d["time"], d["value"]),
                        _ => throw new ArgumentException($"{at} is [time, name] or {{ time, value }}.")
                    };
                    if (name is not string n) throw new ArgumentException($"{at}'s name is a string: the desc of the layer to show.");
                    keys.Add((MotionTypes.Read(time, MotionType.Real, $"{at}'s time")[0], n));
                }
                var sorted = keys.OrderBy(k => k.Item1).ToList();
                for (var i = 1; i < sorted.Count; i++)
                    if (sorted[i].Item1 - sorted[i - 1].Item1 < MotionAnimated.TimeEpsilon)
                        throw new ArgumentException($"{who}'s active has two keys at {MotionTypes.Time(sorted[i].Item1)}; one name per time.");
                return new(sorted);
            case MotionNode: throw new ArgumentException($"{who}'s active is {form}; a switch picks a layer by its name, so a number node cannot say which.");
            default: throw new ArgumentException($"{who}'s active is {form}.");
        }
    }

    /// <summary>The name shown at a time: the last key at or before it, or the first before the first key.</summary>
    public string At(double time)
    {
        var name = keys[0].Name;
        foreach (var (t, n) in keys)
        {
            if (time < t - MotionAnimated.TimeEpsilon) break;
            name = n;
        }
        return name;
    }

    /// <summary>Synfig's <c>layer_name</c>: a string, or a string held from waypoint to waypoint.</summary>
    public XElement ToSif() => keys.Count == 1
        ? new XElement("string", keys[0].Name)
        : new XElement("animated", new XAttribute("type", "string"),
            keys.Select(k => new XElement("waypoint",
                new XAttribute("time", MotionTypes.Time(k.Time)), new XAttribute("before", "constant"), new XAttribute("after", "constant"),
                new XElement("string", k.Name))));
    #endregion
}
