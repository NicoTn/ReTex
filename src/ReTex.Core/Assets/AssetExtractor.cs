using ReTex.Core.Rap;

namespace ReTex.Core.Assets;

/// <summary>
/// Extracts retexturable assets: classes under CfgVehicles / CfgWeapons / CfgGlasses
/// whose hiddenSelections OR hiddenSelectionsTextures resolve (via the inheritance
/// chain) to a non-empty array. Resolution uses a class <b>index</b> that can span
/// multiple configs, so variants inheriting a base class in another PBO are caught.
/// </summary>
public static class AssetExtractor
{
    /// <summary>Single-config convenience (index built from this root only).</summary>
    public static List<AssetInfo> Extract(RapClass root)
    {
        var result = new List<AssetInfo>();
        CollectFrom(root.Class("CfgVehicles"), BuildIndex(new[] { root.Class("CfgVehicles") }), ClassifyVehicle, result);
        CollectFrom(root.Class("CfgWeapons"), BuildIndex(new[] { root.Class("CfgWeapons") }), ClassifyWeapon, result);
        CollectFrom(root.Class("CfgGlasses"), BuildIndex(new[] { root.Class("CfgGlasses") }), (_, _) => AssetCategory.Glasses, result);
        return result;
    }

    /// <summary>Merges the child classes of several containers into one name→class index (real definitions beat forward-decls).</summary>
    public static Dictionary<string, RapClass> BuildIndex(IEnumerable<RapClass?> containers)
    {
        var dict = new Dictionary<string, RapClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var cont in containers)
        {
            if (cont is null) continue;
            foreach (var c in cont.Classes)
                if (!dict.TryGetValue(c.Name, out var existing) || Weight(c) > Weight(existing))
                    dict[c.Name] = c;
        }
        return dict;
    }

    /// <summary>Resolves each child class of <paramref name="container"/> against <paramref name="index"/> and adds the retexturable ones.</summary>
    public static void CollectFrom(RapClass? container, Dictionary<string, RapClass> index,
        Func<RapClass, Dictionary<string, RapClass>, AssetCategory> classify, List<AssetInfo> outList)
    {
        if (container is null) return;
        foreach (var c in container.Classes)
        {
            var hs = ResolveArray(c, index, "hiddenSelections");
            var hst = ResolveArray(c, index, "hiddenSelectionsTextures");
            if (hs.Count == 0 && hst.Count == 0) continue;

            var scope = ResolveInt(c, index, "scope", 2);
            if (scope <= 0) continue;

            outList.Add(new AssetInfo
            {
                ClassName = c.Name,
                Category = classify(c, index),
                Parent = c.Parent,
                DisplayName = ResolveString(c, index, "displayName"),
                Model = ResolveString(c, index, "model"),
                Scope = scope,
                HiddenSelections = hs,
                HiddenSelectionsTextures = hst,
                SourceClassNode = c,
            });
        }
    }

    private static int Weight(RapClass c) => c.Properties.Count + c.Classes.Count;

    // --- Inheritance-chain resolution against the index ---

    private static RapClass? Parent(RapClass c, Dictionary<string, RapClass> index) =>
        c.Parent.Length > 0 && index.TryGetValue(c.Parent, out var p) && p != c ? p : null;

    private static List<string> ResolveArray(RapClass c, Dictionary<string, RapClass> index, string key)
    {
        int guard = 256;
        for (var cur = c; cur is not null && guard-- > 0; cur = Parent(cur, index))
        {
            var v = cur.Value(key);
            if (v is { IsArray: true })
            {
                var list = v.AsStringList().Where(s => s.Length > 0).ToList();
                if (list.Count > 0) return list;
            }
        }
        return new();
    }

    private static string ResolveString(RapClass c, Dictionary<string, RapClass> index, string key)
    {
        int guard = 256;
        for (var cur = c; cur is not null && guard-- > 0; cur = Parent(cur, index))
        {
            var s = cur.Value(key)?.AsString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return "";
    }

    private static int ResolveInt(RapClass c, Dictionary<string, RapClass> index, string key, int fallback)
    {
        int guard = 256;
        for (var cur = c; cur is not null && guard-- > 0; cur = Parent(cur, index))
            if (cur.Value(key)?.Raw is long l) return (int)l;
        return fallback;
    }

    private static bool HasInChain(RapClass c, Dictionary<string, RapClass> index, Func<RapClass, bool> pred)
    {
        int guard = 256;
        for (var cur = c; cur is not null && guard-- > 0; cur = Parent(cur, index))
            if (pred(cur)) return true;
        return false;
    }

    // --- Categorization ---

    public static AssetCategory ClassifyWeapon(RapClass c, Dictionary<string, RapClass> index)
    {
        bool worn = HasInChain(c, index, k =>
        {
            var ii = k.Class("ItemInfo");
            return ii is not null && (ii.Properties.ContainsKey("uniformModel")
                                      || ii.Properties.ContainsKey("type")
                                      || ii.Properties.ContainsKey("armor"));
        });
        return worn ? AssetCategory.Equipment : AssetCategory.Weapon;
    }

    public static AssetCategory ClassifyVehicle(RapClass c, Dictionary<string, RapClass> index)
    {
        var chain = string.Join("|", ChainNames(c, index)).ToLowerInvariant();
        if (chain.Contains("bag_base") || chain.Contains("backpack")) return AssetCategory.Backpack;

        if (HasInChain(c, index, k => k.Properties.ContainsKey("maxSpeed")
                                      || k.Properties.ContainsKey("fuelCapacity")
                                      || k.Class("Turrets") is not null))
            return AssetCategory.Vehicle;

        if (chain.Contains("caman") || chain.Contains("soldier") || chain.Contains("man_base")
            || HasInChain(c, index, k => k.Properties.ContainsKey("nakedUniform") || k.Properties.ContainsKey("modelSides")))
            return AssetCategory.Unit;

        return AssetCategory.Prop;
    }

    private static List<string> ChainNames(RapClass c, Dictionary<string, RapClass> index)
    {
        var names = new List<string>();
        int guard = 256;
        for (var cur = c; cur is not null && guard-- > 0; cur = Parent(cur, index))
        {
            names.Add(cur.Name);
            if (cur.Parent.Length > 0) names.Add(cur.Parent);
        }
        return names;
    }
}
