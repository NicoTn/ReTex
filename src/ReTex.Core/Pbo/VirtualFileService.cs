namespace ReTex.Core.Pbo;

/// <summary>Resolves a virtual file path (e.g. "MyMod\textures\x.paa") to bytes by searching a mod's PBOs.</summary>
public static class VirtualFileService
{
    /// <summary>Extracts the bytes of a virtual path from whichever PBO (by prefix) contains it; null if not found.</summary>
    public static byte[]? Extract(IEnumerable<string> pboPaths, string virtualPath)
    {
        var norm = virtualPath.Replace('/', '\\').TrimStart('\\');

        // Config texture references frequently omit the extension (e.g. vehicle mods write
        // "...\Fellblade_CO" while the PBO entry is "...\Fellblade_CO.paa"). Try the .paa form too.
        var candidates = new List<string> { norm };
        if (!norm.EndsWith(".paa", StringComparison.OrdinalIgnoreCase)
            && !norm.EndsWith(".pac", StringComparison.OrdinalIgnoreCase))
            candidates.Add(norm + ".paa");

        foreach (var pbo in pboPaths)
        {
            PboArchive arc;
            try { arc = new PboArchive(pbo); }
            catch { continue; }
            using (arc)
            {
                foreach (var cand in candidates)
                {
                    var entry = FindEntry(arc, pbo, cand);
                    if (entry is null) continue;
                    try { return arc.Extract(entry); }
                    catch (NotSupportedException) { return null; } // compressed - would need pboc
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the entry for a virtual path. Tries the path as-is, and stripped by each candidate
    /// prefix: the PBO's $PBOPREFIX$ AND its file name (Arma falls back to the file name when a PBO
    /// has no explicit prefix - many mods rely on this).
    /// </summary>
    private static PboEntry? FindEntry(PboArchive arc, string pboPath, string norm)
    {
        // Direct match (entry stored with its full path / no prefix in the reference).
        var hit = arc.Find(norm);
        if (hit is not null) return hit;

        foreach (var p in new[] { arc.Prefix, System.IO.Path.GetFileNameWithoutExtension(pboPath) })
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var prefix = p.Replace('/', '\\').Trim('\\');
            if (norm.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                hit = arc.Find(norm[(prefix.Length + 1)..]);
                if (hit is not null) return hit;
            }
        }
        return null;
    }
}
