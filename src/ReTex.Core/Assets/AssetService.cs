using ReTex.Core.Mods;
using ReTex.Core.Pbo;
using ReTex.Core.Rap;

namespace ReTex.Core.Assets;

/// <summary>Loads retexturable assets from a PBO or a whole mod by reading each PBO's config.</summary>
public static class AssetService
{
    /// <summary>Parses a single PBO's config (config.bin or config.cpp); returns null if none/unreadable.</summary>
    private static (RapClass? Root, string Addon, string Prefix) LoadConfig(string pboPath)
    {
        using var arc = new PboArchive(pboPath);

        var cfg = FindConfig(arc, "config.bin") ?? FindConfig(arc, "config.cpp");
        if (cfg is null) return (null, "", "");

        byte[] bytes;
        try { bytes = arc.Extract(cfg); }
        catch (NotSupportedException) { return (null, "", ""); } // compressed/encrypted

        RapClass root;
        try
        {
            root = RapReader.IsRapified(bytes)
                ? RapReader.Parse(bytes)
                : CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch { return (null, "", ""); }

        var addon = root.Class("CfgPatches")?.Classes.FirstOrDefault()?.Name ?? "";
        return (root, addon, arc.Prefix ?? "");
    }

    /// <summary>Loads every config carried by a dependency PBO. Large mods often use a small root
    /// config plus compiled sub-configs such as Army/config.bin; the actual wearable classes may
    /// exist only in those sub-configs.</summary>
    private static List<(RapClass Root, string Addon, string Prefix)> LoadAllConfigs(string pboPath)
    {
        using var arc = new PboArchive(pboPath);
        var entries = arc.Entries.Where(e =>
            Path.GetFileName(e.FileName.Replace('\\', '/')).Equals("config.bin", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(e.FileName.Replace('\\', '/')).Equals("config.cpp", StringComparison.OrdinalIgnoreCase));
        var result = new List<(RapClass, string, string)>();
        foreach (var cfg in entries)
        {
            try
            {
                var bytes = arc.Extract(cfg);
                var root = RapReader.IsRapified(bytes)
                    ? RapReader.Parse(bytes)
                    : CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                var addon = root.Class("CfgPatches")?.Classes.FirstOrDefault()?.Name ?? "";
                result.Add((root, addon, arc.Prefix ?? ""));
            }
            catch { /* skip an unreadable/unsupported sub-config, keep the others */ }
        }
        return result;
    }

    /// <summary>Extracts assets from one PBO (inheritance resolved within that config only).</summary>
    public static List<AssetInfo> LoadFromPbo(string pboPath)
    {
        var (root, addon, prefix) = LoadConfig(pboPath);
        if (root is null) return new();

        var assets = AssetExtractor.Extract(root);
        foreach (var a in assets) { a.SourcePbo = pboPath; a.SourceAddon = addon; a.SourcePrefix = prefix; }
        return assets;
    }

    /// <summary>
    /// Extracts assets across all of a mod's PBOs, resolving inheritance against a GLOBAL class
    /// index spanning every config - so variants that inherit a base class (with hiddenSelections)
    /// from a different PBO are caught.
    /// </summary>
    public static List<AssetInfo> LoadForMod(ArmaMod mod, IReadOnlyList<ArmaMod>? installedMods = null,
        Action<string>? diagnostic = null)
    {
        // 1. Parse every PBO's config.
        var parsed = new List<(RapClass Root, string Pbo, string Addon, string Prefix)>();
        foreach (var pbo in mod.PboPaths)
        {
            try
            {
                var (root, addon, prefix) = LoadConfig(pbo);
                if (root is not null) parsed.Add((root, pbo, addon, prefix));
            }
            catch { /* skip unreadable PBO */ }
        }

        // 1b. Pull in likely dependency configs. Many compatibility/arsenal mods contain only a
        // forward declaration of the real base class; its ItemInfo.uniformModel lives in another
        // installed mod. requiredAddons names normally extend the dependency PBO stem, e.g.
        // OPTRE_UNSC_Units_Army -> OPTRE_UNSC_Units.pbo. Parse only those candidates rather than
        // every Workshop config (which can number in the thousands).
        if (installedMods is not null)
        {
            var selected = new HashSet<string>(mod.PboPaths, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            var requirements = RequiredAddons(parsed.Select(p => p.Root));
            diagnostic?.Invoke($"requiredAddons: {string.Join(", ", requirements)}");
            for (int pass = 0; pass < 3 && requirements.Count > 0; pass++)
            {
                var candidates = installedMods.SelectMany(m => m.PboPaths)
                    .Where(p => !seen.Contains(p) && MatchesRequiredAddon(Path.GetFileNameWithoutExtension(p), requirements))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                diagnostic?.Invoke($"dependency candidates pass {pass + 1}: {string.Join(", ", candidates)}");
                if (candidates.Count == 0) break;

                var addedRoots = new List<RapClass>();
                foreach (var pbo in candidates)
                {
                    seen.Add(pbo);
                    try
                    {
                        var configs = LoadAllConfigs(pbo);
                        if (configs.Count > 0)
                        {
                            foreach (var (root, addon, prefix) in configs)
                            {
                                parsed.Add((root, pbo, addon, prefix));
                                addedRoots.Add(root);
                            }
                            diagnostic?.Invoke($"loaded {configs.Count} dependency config(s): {pbo}");
                        }
                        else diagnostic?.Invoke($"dependency config unreadable: {pbo}");
                    }
                    catch (Exception ex) { diagnostic?.Invoke($"dependency config failed: {pbo}: {ex.Message}"); }
                }
                requirements.UnionWith(RequiredAddons(addedRoots));
            }
        }

        // 2. Build global indices per config container.
        var vIdx = AssetExtractor.BuildIndex(parsed.Select(p => p.Root.Class("CfgVehicles")));
        var wIdx = AssetExtractor.BuildIndex(parsed.Select(p => p.Root.Class("CfgWeapons")));
        var gIdx = AssetExtractor.BuildIndex(parsed.Select(p => p.Root.Class("CfgGlasses")));

        // 3. Collect each config's own classes, resolving against the global indices.
        var result = new List<AssetInfo>();
        var ownPbos = new HashSet<string>(mod.PboPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var (root, pbo, addon, prefix) in parsed.Where(p => ownPbos.Contains(p.Pbo)))
        {
            var local = new List<AssetInfo>();
            AssetExtractor.CollectFrom(root.Class("CfgVehicles"), vIdx, AssetExtractor.ClassifyVehicle, local);
            AssetExtractor.CollectFrom(root.Class("CfgWeapons"), wIdx, AssetExtractor.ClassifyWeapon, local);
            AssetExtractor.CollectFrom(root.Class("CfgGlasses"), gIdx, (_, _) => AssetCategory.Glasses, local);

            if (diagnostic is not null)
                foreach (var unresolved in local.Where(a => a.Model.Length == 0).Take(30))
                    diagnostic($"unresolved {unresolved.ClassName}: {DescribeChain(unresolved.SourceClassNode, wIdx)}");

            foreach (var a in local) { a.SourcePbo = pbo; a.SourceAddon = addon; a.SourcePrefix = prefix; }
            result.AddRange(local);
        }
        return result;
    }

    private static string DescribeChain(RapClass? start, Dictionary<string, RapClass> index)
    {
        var parts = new List<string>();
        for (var cur = start; cur is not null && parts.Count < 16;)
        {
            var model = cur.StringOr("model");
            var worn = cur.Class("ItemInfo")?.StringOr("uniformModel") ?? "";
            parts.Add($"{cur.Name}[parent={cur.Parent}, model={model}, uniformModel={worn}, weight={cur.Properties.Count + cur.Classes.Count}]");
            if (cur.Parent.Length == 0 || !index.TryGetValue(cur.Parent, out var parent) || ReferenceEquals(parent, cur)) break;
            cur = parent;
        }
        return string.Join(" -> ", parts);
    }

    private static HashSet<string> RequiredAddons(IEnumerable<RapClass> roots)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            foreach (var patch in root.Class("CfgPatches")?.Classes ?? Enumerable.Empty<RapClass>())
                if (patch.Value("requiredAddons") is { IsArray: true } value)
                    foreach (var addon in value.AsStringList().Where(s => s.Length > 0)) result.Add(addon);
        return result;
    }

    private static bool MatchesRequiredAddon(string pboStem, HashSet<string> requirements)
    {
        if (pboStem.Length < 4) return false;
        return requirements.Any(required =>
            required.Equals(pboStem, StringComparison.OrdinalIgnoreCase)
            || (required.StartsWith(pboStem, StringComparison.OrdinalIgnoreCase)
                && required.Length > pboStem.Length && required[pboStem.Length] == '_')
            || (pboStem.StartsWith(required, StringComparison.OrdinalIgnoreCase)
                && pboStem.Length > required.Length && pboStem[required.Length] == '_'));
    }

    // Prefer the root config (e.g. "config.bin") over any in a subfolder (e.g. "ACEAX\config.bin",
    // which is usually a compat sub-config with no retexturable classes). The subfolder fallback
    // matches on the basename so it works regardless of separator: some PBOs store entries with
    // forward slashes (e.g. "AV/config.cpp"), which a hardcoded "\config.cpp" check would miss.
    private static PboEntry? FindConfig(PboArchive arc, string fileName) =>
        arc.Entries.FirstOrDefault(e => e.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
        ?? arc.Entries.FirstOrDefault(e =>
            Path.GetFileName(e.FileName.Replace('\\', '/')).Equals(fileName, StringComparison.OrdinalIgnoreCase));
}
