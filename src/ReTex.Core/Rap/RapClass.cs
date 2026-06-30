namespace ReTex.Core.Rap;

/// <summary>A config class node (CfgVehicles, a vehicle class, etc.) from a rapified config.</summary>
public sealed class RapClass
{
    public string Name { get; set; } = "";

    /// <summary>Inherited (parent) class name, or "" if none.</summary>
    public string Parent { get; set; } = "";

    public Dictionary<string, RapValue> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RapClass> Classes { get; } = new();

    /// <summary>Forward-declared subclasses (e.g. "class Foo;").</summary>
    public List<string> ExternalClasses { get; } = new();

    public RapClass? Class(string name) =>
        Classes.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public RapValue? Value(string name) =>
        Properties.TryGetValue(name, out var v) ? v : null;

    public string StringOr(string name, string fallback = "") =>
        Value(name)?.AsString() ?? fallback;

    /// <summary>Depth-first search for a descendant class by name.</summary>
    public RapClass? FindDescendant(string name)
    {
        foreach (var c in Classes)
        {
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            var found = c.FindDescendant(name);
            if (found is not null) return found;
        }
        return null;
    }

    public override string ToString() =>
        Parent.Length > 0 ? $"class {Name}: {Parent}" : $"class {Name}";
}
