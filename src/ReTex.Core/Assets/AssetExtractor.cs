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
            // Keep empty slots for materials: only some selections carry an rvmat, so the array is
            // sparse (e.g. {"", "arms.rvmat"}). Dropping the empties would shift each material onto
            // the wrong selection index. Textures rarely have interior gaps, so they stay filtered.
            var hsm = ResolveArray(c, index, "hiddenSelectionsMaterials", keepEmpty: true);
            if (hs.Count == 0 && hst.Count == 0) continue;

            var scope = ResolveInt(c, index, "scope", 2);
            if (scope <= 0) continue;

            var category = classify(c, index);
            outList.Add(new AssetInfo
            {
                ClassName = c.Name,
                Category = category,
                Subcategory = ClassifySubcategory(c, index, category),
                Parent = c.Parent,
                DisplayName = ResolveString(c, index, "displayName"),
                Model = ResolveString(c, index, "model"),
                Scope = scope,
                HiddenSelections = hs,
                HiddenSelectionsTextures = hst,
                HiddenSelectionsMaterials = hsm,
                SourceClassNode = c,
            });
        }
    }

    private static int Weight(RapClass c) => c.Properties.Count + c.Classes.Count;

    // --- Inheritance-chain resolution against the index ---

    private static RapClass? Parent(RapClass c, Dictionary<string, RapClass> index) =>
        c.Parent.Length > 0 && index.TryGetValue(c.Parent, out var p) && p != c ? p : null;

    /// <param name="keepEmpty">When true, the full array (including empty-string slots) is returned as
    /// long as it has at least one non-empty entry - needed to keep sparse arrays like
    /// hiddenSelectionsMaterials index-aligned with hiddenSelections.</param>
    private static List<string> ResolveArray(RapClass c, Dictionary<string, RapClass> index, string key, bool keepEmpty = false)
    {
        int guard = 256;
        for (var cur = c; cur is not null && guard-- > 0; cur = Parent(cur, index))
        {
            var v = cur.Value(key);
            if (v is { IsArray: true })
            {
                var all = v.AsStringList().ToList();
                var nonEmpty = all.Where(s => s.Length > 0).ToList();
                if (nonEmpty.Count > 0) return keepEmpty ? all : nonEmpty;
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
                                      || ii.Properties.ContainsKey("armor")
                                      // A uniform's `type` (801) usually comes from a vanilla base
                                      // outside the mod index; uniformClass/containerClass identify
                                      // wearables (uniform/vest/backpack-item) regardless.
                                      || ii.Properties.ContainsKey("uniformClass")
                                      || ii.Properties.ContainsKey("containerClass"));
        });
        return worn ? AssetCategory.Equipment : AssetCategory.Weapon;
    }

    public static AssetCategory ClassifyVehicle(RapClass c, Dictionary<string, RapClass> index)
    {
        var chain = string.Join("|", ChainNames(c, index)).ToLowerInvariant();
        if (chain.Contains("bag_base") || chain.Contains("backpack")) return AssetCategory.Backpack;

        // Backpacks whose Bag_Base ancestry lives in a DEPENDENCY mod or the base game (outside the
        // scanned mod's PBOs) can't be caught by the name check above - the resolvable chain
        // dead-ends before reaching Bag_Base (e.g. CTR jump-packs inherit via TIOW's
        // B_AssaultPack_Base). Fall back to `maximumLoad`, the Bag_Base cargo-capacity property that
        // only backpacks carry (cars/tanks/props don't), which is defined locally in the chain.
        if (HasInChain(c, index, k => k.Properties.ContainsKey("maximumLoad")))
            return AssetCategory.Backpack;

        if (HasInChain(c, index, k => k.Properties.ContainsKey("maxSpeed")
                                      || k.Properties.ContainsKey("fuelCapacity")
                                      || k.Class("Turrets") is not null))
            return AssetCategory.Vehicle;

        if (chain.Contains("caman") || chain.Contains("soldier") || chain.Contains("man_base")
            || HasInChain(c, index, k => k.Properties.ContainsKey("nakedUniform") || k.Properties.ContainsKey("modelSides")))
            return AssetCategory.Unit;

        return AssetCategory.Prop;
    }

    /// <summary>Assigns an ACE/Arsenal-style browsing group using config values first and class
    /// ancestry/name hints as a fallback for mods whose base classes live outside the scanned PBOs.</summary>
    public static AssetSubcategory ClassifySubcategory(RapClass c, Dictionary<string, RapClass> index, AssetCategory category)
    {
        if (category == AssetCategory.Glasses) return AssetSubcategory.Facewear;
        if (category == AssetCategory.Backpack) return AssetSubcategory.Backpack;
        if (category == AssetCategory.Unit) return AssetSubcategory.Man;

        var chain = string.Join("|", ChainNames(c, index)).ToLowerInvariant();
        int itemType = ResolveNestedInt(c, index, "ItemInfo", "type", -1);
        int weaponType = ResolveInt(c, index, "type", -1);

        if (category == AssetCategory.Equipment)
        {
            if (ResolveNestedString(c, index, "ItemInfo", "uniformClass").Length > 0
                || itemType == 801 || chain.Contains("uniform")) return AssetSubcategory.Uniform;
            if (itemType == 701 || chain.Contains("vest") || chain.Contains("platecarrier")) return AssetSubcategory.Vest;
            if (itemType == 605 || chain.Contains("helmet") || chain.Contains("headgear")) return AssetSubcategory.Headgear;
            // Arsenal attachment item types: muzzle, optic, pointer and bipod.
            if (itemType is 101 or 201 or 301 or 302) return AssetSubcategory.WeaponAttachment;
            return AssetSubcategory.Other;
        }

        if (category == AssetCategory.Weapon)
        {
            if (weaponType == 2 || chain.Contains("pistol") || chain.Contains("handgun")) return AssetSubcategory.Handgun;
            if (weaponType == 4 || chain.Contains("launcher")) return AssetSubcategory.Launcher;
            if (itemType is 101 or 201 or 301 or 302 || chain.Contains("optic") || chain.Contains("muzzle")
                || chain.Contains("acc_")) return AssetSubcategory.WeaponAttachment;
            return AssetSubcategory.Rifle;
        }

        if (category == AssetCategory.Vehicle)
        {
            if (chain.Contains("helicopter") || chain.Contains("heli_")) return AssetSubcategory.Helicopter;
            if (chain.Contains("plane") || chain.Contains("airplane")) return AssetSubcategory.Plane;
            if (chain.Contains("ship") || chain.Contains("boat") || chain.Contains("submarine")) return AssetSubcategory.Boat;
            if (chain.Contains("staticweapon") || chain.Contains("static_")) return AssetSubcategory.StaticWeapon;
            if (chain.Contains("tank") || chain.Contains("apc") || chain.Contains("wheeled_apc")) return AssetSubcategory.ArmoredVehicle;
            return AssetSubcategory.Car;
        }

        if (category == AssetCategory.Prop)
            return chain.Contains("house") || chain.Contains("building") ? AssetSubcategory.Building : AssetSubcategory.Prop;

        return AssetSubcategory.Other;
    }

    private static string ResolveNestedString(RapClass c, Dictionary<string, RapClass> index, string nestedClass, string key)
    {
        int guard = 256;
        for (var cur = c; cur is not null && guard-- > 0; cur = Parent(cur, index))
        {
            var s = cur.Class(nestedClass)?.Value(key)?.AsString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return "";
    }

    private static int ResolveNestedInt(RapClass c, Dictionary<string, RapClass> index, string nestedClass, string key, int fallback)
    {
        int guard = 256;
        for (var cur = c; cur is not null && guard-- > 0; cur = Parent(cur, index))
            if (cur.Class(nestedClass)?.Value(key)?.Raw is long l) return (int)l;
        return fallback;
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
