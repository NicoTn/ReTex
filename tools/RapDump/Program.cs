using ReTex.Core.Rap;

// Dev utility: dump the top-level structure of a rapified config.bin to validate the parser.
// Usage: RapDump <config.bin> [maxDepth]

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: RapDump <config.bin> [maxDepth]");
    return 1;
}

int maxDepth = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 2;

var data = File.ReadAllBytes(args[0]);
Console.WriteLine($"Rapified: {RapReader.IsRapified(data)}  ({data.Length} bytes)");
if (!RapReader.IsRapified(data)) return 2;

var root = RapReader.Parse(data);

var assets = ReTex.Core.Assets.AssetExtractor.Extract(root);
Console.WriteLine($"\n=== Retexturable assets: {assets.Count} ===");
foreach (var a in assets.Take(40))
    Console.WriteLine($"  [{a.Category}] {a.Label}  hs={a.HiddenSelections.Count} model={a.Model}");

if (maxDepth > 0)
{
    Console.WriteLine("\n=== Config tree ===");
    Dump(root, 0, maxDepth);
}
return 0;

static void Dump(RapClass c, int depth, int maxDepth)
{
    var indent = new string(' ', depth * 2);
    if (depth > 0)
        Console.WriteLine($"{indent}{c}  [props:{c.Properties.Count} classes:{c.Classes.Count}]");

    if (depth >= maxDepth) return;

    // Show a couple of interesting props if present.
    foreach (var key in new[] { "displayName", "scope", "model", "hiddenSelections", "hiddenSelectionsTextures" })
        if (c.Properties.TryGetValue(key, out var v))
            Console.WriteLine($"{indent}  .{key} = {v}");

    foreach (var child in c.Classes)
        Dump(child, depth + 1, maxDepth);
}
