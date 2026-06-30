namespace ReTex.Core.Rap;

/// <summary>A config value: a string, number, or array (of values).</summary>
public sealed class RapValue
{
    /// <summary>Underlying value: string, long, double, or List&lt;RapValue&gt;.</summary>
    public object? Raw { get; init; }

    public bool IsArray => Raw is List<RapValue>;

    public string AsString() => Raw switch
    {
        string s => s,
        long l => l.ToString(),
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "",
    };

    public IReadOnlyList<RapValue> AsArray() =>
        Raw as List<RapValue> ?? (IReadOnlyList<RapValue>)Array.Empty<RapValue>();

    /// <summary>Flattened list of string elements (for arrays of strings like hiddenSelections).</summary>
    public IReadOnlyList<string> AsStringList() =>
        AsArray().Select(v => v.AsString()).ToList();

    public override string ToString() =>
        IsArray ? "{" + string.Join(", ", AsArray().Select(v => v.ToString())) + "}" : AsString();
}
