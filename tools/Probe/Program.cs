using ReTex.Core.Assets;
using ReTex.Core.Mods;
using ReTex.Core.P3d;
using ReTex.Core.Projects;

// Quick p3d header probe: Probe --p3d <file>
if (args.Length >= 2 && args[0] == "--p3d")
{
    var hdr = OdolReader.ReadHeader(File.ReadAllBytes(args[1]));
    Console.WriteLine($"ODOL v{hdr.Version}, {hdr.LodCount} LODs");
    for (int i = 0; i < hdr.LodCount; i++)
        Console.WriteLine($"  [{i}] resolution {hdr.Resolutions[i]}");
    Console.WriteLine($"Visual LODs: {string.Join(", ", hdr.VisualLodIndices)}");

    var d2 = File.ReadAllBytes(args[1]);
    var mi = OdolReader.ReadModelInfo(d2, hdr.HeaderEndOffset, hdr.Version);
    Console.WriteLine($"\nModelInfo: skeleton='{mi.SkeletonName}', bones={mi.Bones.Count}");
    Console.WriteLine($"  bbox: [{string.Join(",", mi.BBoxMin)}] .. [{string.Join(",", mi.BBoxMax)}]");
    foreach (var (b, par) in mi.Bones.Take(4)) Console.WriteLine($"    {b} -> {(par.Length == 0 ? "(root)" : par)}");
    Console.WriteLine($"  ModelInfo ends at offset {mi.EndOffset}");
    return 0;
}

// Find which installed mod defines a CfgPatches addon: Probe --findaddon <workshopRoot> <addonName>
if (args.Length >= 3 && args[0] == "--findaddon")
{
    var root = args[1];
    var want = args[2];
    int scanned = 0, hits = 0;
    foreach (var modDir in Directory.EnumerateDirectories(root))
    {
        var ad = Path.Combine(modDir, "addons");
        if (!Directory.Exists(ad)) continue;
        foreach (var pbo in Directory.EnumerateFiles(ad, "*.pbo"))
        {
            scanned++;
            try
            {
                using var arc = new ReTex.Core.Pbo.PboArchive(pbo);
                var cfg = arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.bin", StringComparison.OrdinalIgnoreCase))
                       ?? arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.cpp", StringComparison.OrdinalIgnoreCase));
                if (cfg is null) continue;
                var bytes = arc.Extract(cfg);
                var root2 = ReTex.Core.Rap.RapReader.IsRapified(bytes)
                    ? ReTex.Core.Rap.RapReader.Parse(bytes)
                    : ReTex.Core.Rap.CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                var patches = root2.Class("CfgPatches");
                if (patches is null) continue;
                if (patches.Classes.Any(c => c.Name.Equals(want, StringComparison.OrdinalIgnoreCase)))
                {
                    hits++;
                    Console.WriteLine($"DEFINES {want}:  {Path.GetFileName(modDir)}  ->  {Path.GetFileName(pbo)}");
                }
            }
            catch { /* skip unreadable */ }
        }
    }
    Console.WriteLine($"\nScanned {scanned} PBOs, {hits} define '{want}'.");
    return 0;
}

// Regenerate config.cpp from a project's retex.json: Probe --regen <retex.json>
if (args.Length >= 2 && args[0] == "--regen")
{
    var rp = RetexProject.Load(args[1]);
    RetexProjectService.GenerateConfig(rp);
    Console.WriteLine($"Regenerated {rp.ConfigPath}\n");
    Console.WriteLine(File.ReadAllText(rp.ConfigPath));
    return 0;
}

// Dump a parsed config class (recursively): Probe --classdump <pbo> <className> [depth]
if (args.Length >= 3 && args[0] == "--classdump")
{
    using var arc = new ReTex.Core.Pbo.PboArchive(args[1]);
    var cfg = arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.bin", StringComparison.OrdinalIgnoreCase))
           ?? arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.cpp", StringComparison.OrdinalIgnoreCase));
    var bytes = arc.Extract(cfg!);
    var root3 = ReTex.Core.Rap.RapReader.IsRapified(bytes)
        ? ReTex.Core.Rap.RapReader.Parse(bytes)
        : ReTex.Core.Rap.CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
    int depth = args.Length > 3 && int.TryParse(args[3], out var dd) ? dd : 4;
    var want3 = args[2];
    RapClassFinder.FindAndDump(root3, want3, depth);
    return 0;
}

// Dump a PBO text entry: Probe --catentry <pbo> <entrySubstring>
if (args.Length >= 3 && args[0] == "--catentry")
{
    using var arc = new ReTex.Core.Pbo.PboArchive(args[1]);
    var e = arc.Entries.First(x => x.FileName.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(arc.Extract(e)));
    return 0;
}

// LoadForMod per-PBO asset counts: Probe --modpbos <modFolder>
if (args.Length >= 2 && args[0] == "--modpbos")
{
    var modFolder2 = args[1];
    var m = new ReTex.Core.Mods.ArmaMod { Name = Path.GetFileName(modFolder2), Path = modFolder2, DisplayName = Path.GetFileName(modFolder2) };
    var ad = Path.Combine(modFolder2, "addons");
    if (Directory.Exists(ad)) m.PboPaths.AddRange(Directory.GetFiles(ad, "*.pbo"));
    var all = AssetService.LoadForMod(m);
    Console.WriteLine($"Total assets: {all.Count} across {all.Select(a => a.SourcePbo).Distinct().Count()} PBOs");
    foreach (var g in all.GroupBy(a => Path.GetFileName(a.SourcePbo)).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Key,-40} {g.Count()}");
    return 0;
}

// PBO listing: Probe --pbols <pbo> [filter]
if (args.Length >= 2 && args[0] == "--pbols")
{
    using var arc = new ReTex.Core.Pbo.PboArchive(args[1]);
    Console.WriteLine($"prefix = '{arc.Prefix}'");
    var filter = args.Length > 2 ? args[2] : "";
    foreach (var e in arc.Entries.Where(e => filter.Length == 0 || e.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(40))
        Console.WriteLine($"  {e.FileName}  (mime=0x{e.PackingMethod:X8}, size={e.DataSize})");
    return 0;
}

// Per-PBO diagnostic: Probe --moddiag <modFolder>
if (args.Length >= 2 && args[0] == "--moddiag")
{
    var diagAddons = Path.Combine(args[1], "addons");
    var diagLog = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "moddiag.log");
    File.WriteAllText(diagLog, "");
    void Log(string s) { Console.WriteLine(s); File.AppendAllText(diagLog, s + Environment.NewLine); }
    int totalAssets = 0;
    foreach (var pbo in Directory.GetFiles(diagAddons, "*.pbo"))
    {
        Log($"-> {Path.GetFileName(pbo)} ...");
        string note;
        try
        {
            using var arc = new ReTex.Core.Pbo.PboArchive(pbo);
            var cfg = arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.bin", StringComparison.OrdinalIgnoreCase))
                   ?? arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.cpp", StringComparison.OrdinalIgnoreCase));
            if (cfg is null) note = "no config";
            else
            {
                var bytes = arc.Extract(cfg);
                var rapified = ReTex.Core.Rap.RapReader.IsRapified(bytes);
                var root = rapified ? ReTex.Core.Rap.RapReader.Parse(bytes)
                                    : ReTex.Core.Rap.CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                int classes = (root.Class("CfgVehicles")?.Classes.Count ?? 0)
                            + (root.Class("CfgWeapons")?.Classes.Count ?? 0)
                            + (root.Class("CfgGlasses")?.Classes.Count ?? 0);
                int a = AssetExtractor.Extract(root).Count;
                totalAssets += a;
                note = $"{(rapified ? "bin" : "cpp")}  classes={classes}  assets={a}";
            }
        }
        catch (Exception ex) { note = $"ERR: {ex.GetType().Name}: {ex.Message}"; }
        Log($"{Path.GetFileName(pbo),-26} {note}");
    }
    Log($"TOTAL assets (per-pbo resolution): {totalAssets}");
    return 0;
}

// Text config parse: Probe --cpp <config.cpp>
if (args.Length >= 2 && args[0] == "--cpp")
{
    var cppRoot = ReTex.Core.Rap.CppConfigParser.Parse(File.ReadAllText(args[1]));
    var cppAssets = AssetExtractor.Extract(cppRoot);
    Console.WriteLine($"Parsed text config -> {cppAssets.Count} assets:");
    foreach (var a in cppAssets)
        Console.WriteLine($"  [{a.Category}] {a.Label}  hs=[{string.Join(",", a.HiddenSelections)}]  hst=[{string.Join(",", a.HiddenSelectionsTextures)}]  model={a.Model}");
    return 0;
}

// ASCII anchor scan: Probe --p3dstrings <file> [startOffset]
if (args.Length >= 2 && args[0] == "--p3dstrings")
{
    var d = File.ReadAllBytes(args[1]);
    int start = args.Length > 2 ? int.Parse(args[2]) : 0;
    int run = 0, runStart = 0, printed = 0;
    for (int i = start; i < d.Length && printed < 60; i++)
    {
        byte b = d[i];
        bool printable = b >= 0x20 && b < 0x7F;
        if (printable) { if (run == 0) runStart = i; run++; }
        else
        {
            if (run >= 4)
            {
                Console.WriteLine($"  @{runStart,-8} (+{runStart - start,-6}) \"{System.Text.Encoding.ASCII.GetString(d, runStart, run)}\"");
                printed++;
            }
            run = 0;
        }
    }
    return 0;
}

// Validates the Core retexture workflow end-to-end.
// Usage: Probe <modFolder> <projectParentDir> [classNameSubstring]

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Probe <modFolder> <projectParentDir> [classNameSubstring]");
    return 1;
}

string modFolder = args[0];
string projParent = args[1];
string pick = args.Length > 2 ? args[2] : "Helm";

var mod = new ArmaMod { Name = Path.GetFileName(modFolder), Path = modFolder, DisplayName = Path.GetFileName(modFolder) };
var addons = Path.Combine(modFolder, "addons");
if (Directory.Exists(addons))
    mod.PboPaths.AddRange(Directory.GetFiles(addons, "*.pbo"));
Console.WriteLine($"Mod: {mod.DisplayName} ({mod.PboCount} PBOs)");

var assets = AssetService.LoadForMod(mod);
Console.WriteLine($"Assets with hidden selections: {assets.Count}");
foreach (var g in assets.GroupBy(a => a.Category).OrderBy(g => g.Key.ToString()))
    Console.WriteLine($"  {g.Key}: {g.Count()}");

var asset = assets.FirstOrDefault(a => a.ClassName.Contains(pick, StringComparison.OrdinalIgnoreCase));
if (asset is null) { Console.Error.WriteLine($"No asset matching '{pick}'."); return 2; }

Console.WriteLine($"\nPicked: [{asset.Category}] {asset.Label}");
Console.WriteLine($"  model: {asset.Model}");
Console.WriteLine($"  hiddenSelections:        {string.Join(", ", asset.HiddenSelections)}");
Console.WriteLine($"  hiddenSelectionsTextures: {string.Join(", ", asset.HiddenSelectionsTextures)}");

// Create a project and add the retexture (copies the source .paa).
Directory.CreateDirectory(projParent);
var proj = RetexProjectService.CreateProject(projParent, "PrimarisTest");
var entry = RetexProjectService.AddRetexture(proj, asset, mod.PboPaths, modAssets: assets);
RetexProjectService.GenerateConfig(proj);

Console.WriteLine($"\nProject: {proj.ProjectDir}");
Console.WriteLine("Copied textures:");
foreach (var f in Directory.GetFiles(proj.TexturesDir))
    Console.WriteLine($"  {Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)");

Console.WriteLine($"\n=== {proj.ConfigPath} ===");
Console.WriteLine(File.ReadAllText(proj.ConfigPath));

// Validate @Mod packing.
var pboc = ReTex.Core.Tools.PboTool.FindDefault();
if (pboc is not null)
{
    var res = await RetexProjectService.PackModAsync(proj, new ReTex.Core.Tools.PboTool(pboc));
    Console.WriteLine($"\nPack: exit={res.ExitCode}, output={res.OutputPath}");
    var modDir = RetexProjectService.ModFolder(proj);
    foreach (var f in Directory.GetFiles(modDir, "*", SearchOption.AllDirectories))
        Console.WriteLine($"  {f.Substring(proj.ProjectDir.Length + 1)} ({new FileInfo(f).Length} bytes)");
}
return 0;

static class RapClassFinder
{
    public static void FindAndDump(ReTex.Core.Rap.RapClass root, string name, int depth)
    {
        var found = Find(root, name);
        if (found is null) { Console.WriteLine($"(class '{name}' not found)"); return; }
        Dump(found, 0, depth);
    }

    static ReTex.Core.Rap.RapClass? Find(ReTex.Core.Rap.RapClass c, string name)
    {
        foreach (var ch in c.Classes)
        {
            if (ch.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return ch;
            var r = Find(ch, name);
            if (r is not null) return r;
        }
        return null;
    }

    static void Dump(ReTex.Core.Rap.RapClass c, int d, int maxDepth)
    {
        var ind = new string(' ', d * 2);
        var baseName = string.IsNullOrEmpty(c.Parent) ? "" : $": {c.Parent}";
        Console.WriteLine($"{ind}class {c.Name}{baseName} {{");
        foreach (var kv in c.Properties)
            Console.WriteLine($"{ind}  {kv.Key} = {kv.Value};");
        if (d < maxDepth)
            foreach (var ch in c.Classes) Dump(ch, d + 1, maxDepth);
        Console.WriteLine($"{ind}}}");
    }
}
