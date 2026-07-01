using System.Globalization;
using System.Text;

namespace ReTex.Core.Rap;

/// <summary>Serializes a parsed class body back to config.cpp text (properties, arrays, nested classes).</summary>
public static class RapWriter
{
    /// <summary>
    /// Writes the inner body of <paramref name="node"/> (properties + nested classes) at the given
    /// indent. Properties in <paramref name="skip"/> are omitted (so the caller can re-emit its own).
    /// Nested classes keep their parent (e.g. <c>class ItemInfo: ItemInfo</c>): dropping it severs
    /// inheritance from the enclosing class's inherited subclass, losing members like a uniform's
    /// type/containerClass/mass.
    /// </summary>
    public static string WriteBody(RapClass node, int indent = 8, ISet<string>? skip = null)
    {
        var sb = new StringBuilder();
        WriteBody(sb, node, indent, skip ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return sb.ToString();
    }

    private static void WriteBody(StringBuilder sb, RapClass node, int indent, ISet<string> skip)
    {
        var pad = new string(' ', indent);

        foreach (var (key, val) in node.Properties)
        {
            if (skip.Contains(key)) continue;
            sb.Append(pad).Append(key).Append(val.IsArray ? "[] = " : " = ")
              .Append(Format(val)).AppendLine(";");
        }

        foreach (var sub in node.Classes)
        {
            sb.Append(pad).Append("class ").Append(sub.Name);
            if (sub.Parent.Length > 0) sb.Append(": ").Append(sub.Parent);
            sb.AppendLine(" {");
            WriteBody(sb, sub, indent + 4, EmptySkip);
            sb.Append(pad).AppendLine("};");
        }
    }

    private static readonly HashSet<string> EmptySkip = new();

    private static string Format(RapValue v) => v.Raw switch
    {
        string s => "\"" + s.Replace("\"", "\"\"") + "\"",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        List<RapValue> arr => "{" + string.Join(", ", arr.Select(Format)) + "}",
        _ => "\"\"",
    };
}
