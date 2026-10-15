namespace Polson.ExtendedMind.Taste;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// The parameters of one Qloo request, kept in canonical order so the same question always has the same
/// spelling — which is what the cache keys on and what provenance records.
/// </summary>
/// <remarks>
/// <para>
/// Lists are joined with commas, which is how Qloo's own client sends them on a GET. Numbers are written
/// in the invariant culture, so a German locale cannot turn <c>0.9</c> into <c>0,9</c> and a list.
/// </para>
/// <para>
/// Setting a parameter to null, an empty string or an empty list removes it. That is how an unset
/// optional stays unset: Qloo treats a present-but-empty parameter differently from an absent one.
/// </para>
/// </remarks>
public sealed class QlooQuery : IEnumerable<KeyValuePair<string, string>>
{
    #region Constructors
    public QlooQuery() { }

    public QlooQuery(IEnumerable<KeyValuePair<string, object?>> parameters)
    {
        foreach (var (name, value) in parameters) this[name] = value;
    }
    #endregion

    #region Indexers
    /// <summary>The parameter's value as it will be sent, or null when it is not set.</summary>
    public object? this[string name]
    {
        get => values.TryGetValue(name, out var value) ? value : null;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (Format(value) is { Length: > 0 } text) values[name.Trim()] = text;
            else values.Remove(name.Trim());
        }
    }
    #endregion

    #region Properties
    public int Count => values.Count;

    public IEnumerable<string> Names => values.Keys;
    #endregion

    #region Methods
    /// <summary>Sets a parameter and returns this query, for chaining.</summary>
    public QlooQuery Set(string name, object? value)
    {
        this[name] = value;
        return this;
    }

    public bool Has(string name) => values.ContainsKey(name);

    /// <summary>A copy, so a profile can derive a baseline query without disturbing the audience one.</summary>
    public QlooQuery Clone() => new(values.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)));

    /// <summary>
    /// The query string, escaped and in canonical order. Never carries the key, which travels in a header,
    /// so this is safe to log, cache on and show as provenance.
    /// </summary>
    public string ToQueryString() =>
        string.Join("&", values.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    public override string ToString() => ToQueryString();

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>One parameter value as Qloo reads it on a GET.</summary>
    internal static string? Format(object? value) => value switch
    {
        null => null,
        string s => s.Trim(),
        bool b => b ? "true" : "false",
        IFormattable f when value is not Enum => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable items => string.Join(",", items.Cast<object?>().Select(Format).Where(s => s is { Length: > 0 })),
        _ => value.ToString()?.Trim(),
    };
    #endregion

    #region Fields
    private readonly SortedDictionary<string, string> values = new(StringComparer.Ordinal);
    #endregion
}
