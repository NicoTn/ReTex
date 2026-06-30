using System.Text.RegularExpressions;

namespace ReTex.Core.Mods;

/// <summary>Discovers installed Arma 3 mods by scanning a folder of @-mod directories.</summary>
public static partial class ModScanner
{
    /// <summary>
    /// Scans a root folder (e.g. "...\Arma 3\!Workshop") for @-mod subfolders. Directory
    /// symlinks/junctions (how the launcher links workshop mods) are followed. Folders
    /// whose name starts with '!' (e.g. "!DO_NOT_CHANGE_FILES...") are skipped.
    /// </summary>
    public static List<ArmaMod> ScanFolder(string root)
    {
        var result = new List<ArmaMod>();
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('!')) continue;

            var mod = new ArmaMod { Name = name, Path = dir };

            var addons = Path.Combine(dir, "addons");
            if (Directory.Exists(addons))
            {
                try
                {
                    mod.PboPaths.AddRange(
                        Directory.EnumerateFiles(addons, "*.pbo", SearchOption.TopDirectoryOnly));
                }
                catch (IOException) { /* unreadable mod; still list it */ }
                catch (UnauthorizedAccessException) { }
            }

            // Only surface things that look like mods (have an addons folder or a mod.cpp).
            if (mod.PboCount == 0 && !File.Exists(Path.Combine(dir, "mod.cpp")))
                continue;

            mod.DisplayName = TryReadModName(dir) ?? name;
            result.Add(mod);
        }

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static string? TryReadModName(string modDir)
    {
        var modCpp = Path.Combine(modDir, "mod.cpp");
        if (!File.Exists(modCpp)) return null;
        try
        {
            var text = File.ReadAllText(modCpp);
            var m = NamePropertyRegex().Match(text);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    [GeneratedRegex("""name\s*=\s*"([^"]*)";""", RegexOptions.IgnoreCase)]
    private static partial Regex NamePropertyRegex();
}
