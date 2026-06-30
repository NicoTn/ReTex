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
    public static List<AssetInfo> LoadForMod(ArmaMod mod)
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

        // 2. Build global indices per config container.
        var vIdx = AssetExtractor.BuildIndex(parsed.Select(p => p.Root.Class("CfgVehicles")));
        var wIdx = AssetExtractor.BuildIndex(parsed.Select(p => p.Root.Class("CfgWeapons")));
        var gIdx = AssetExtractor.BuildIndex(parsed.Select(p => p.Root.Class("CfgGlasses")));

        // 3. Collect each config's own classes, resolving against the global indices.
        var result = new List<AssetInfo>();
        foreach (var (root, pbo, addon, prefix) in parsed)
        {
            var local = new List<AssetInfo>();
            AssetExtractor.CollectFrom(root.Class("CfgVehicles"), vIdx, AssetExtractor.ClassifyVehicle, local);
            AssetExtractor.CollectFrom(root.Class("CfgWeapons"), wIdx, AssetExtractor.ClassifyWeapon, local);
            AssetExtractor.CollectFrom(root.Class("CfgGlasses"), gIdx, (_, _) => AssetCategory.Glasses, local);

            foreach (var a in local) { a.SourcePbo = pbo; a.SourceAddon = addon; a.SourcePrefix = prefix; }
            result.AddRange(local);
        }
        return result;
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
