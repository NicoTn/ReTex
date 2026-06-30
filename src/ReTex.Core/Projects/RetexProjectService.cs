using ReTex.Core.Assets;
using ReTex.Core.Pbo;
using ReTex.Core.Tools;

namespace ReTex.Core.Projects;

/// <summary>Creates/edits retexture projects: scaffolding, adding retextures (copying source textures), config generation, packing.</summary>
public static class RetexProjectService
{
    /// <summary>Creates a new project folder (addon structure + $PBOPREFIX$ + retex.json).</summary>
    public static RetexProject CreateProject(string parentDir, string name, string? prefix = null, string author = "")
    {
        var dir = Path.Combine(parentDir, name);
        var proj = new RetexProject
        {
            Name = name,
            Author = author,
            Prefix = prefix ?? $"z\\{Slug(name)}\\addons\\main",
            ProjectDir = dir,
        };
        Directory.CreateDirectory(proj.TexturesDir);
        File.WriteAllText(Path.Combine(proj.AddonDir, "$PBOPREFIX$"), proj.Prefix);
        proj.Save();
        return proj;
    }

    /// <summary>
    /// Adds a retexture for an asset. Copies the source .paa of each selection (found across
    /// <paramref name="modPboPaths"/>) into the project's textures folder as the editable starting
    /// point. <paramref name="indices"/> limits which selections to retexture (null = all).
    /// </summary>
    public static RetexEntry AddRetexture(RetexProject proj, AssetInfo asset, IReadOnlyList<string> modPboPaths, IReadOnlyCollection<int>? indices = null, bool copyValues = true)
    {
        Directory.CreateDirectory(proj.TexturesDir);

        var entry = new RetexEntry
        {
            SourceClass = asset.ClassName,
            Category = asset.Category,
            SourceModel = asset.Model,
            SourceAddon = asset.SourceAddon,
            DisplayName = asset.DisplayName.Length > 0 ? asset.DisplayName + " (ReTex)" : asset.ClassName + " (ReTex)",
            NewClassName = UniqueClassName(proj, $"{Slug(proj.Name)}_{asset.ClassName}"),
        };

        // Copy the source class's declared values (armor, weapon stats, ItemInfo, HitPoints, ...)
        // so the variant is a full, editable copy - not just an inheriting texture swap.
        if (copyValues && asset.SourceClassNode is not null)
        {
            // Keep hiddenSelectionsTextures in the body (top-level AND inside ItemInfo) so the
            // generator can repoint them at the project textures - worn gear renders from
            // ItemInfo's textures, not the top-level ones.
            // Drop baseWeapon: the source value points at the ORIGINAL weapon, which makes Arsenal
            // treat our class as a hidden sub-variant. The generator re-emits it as a self-reference.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scope", "displayName", "baseWeapon" };
            entry.CopiedBody = Rap.RapWriter.WriteBody(asset.SourceClassNode, indent: 8, skip);
        }

        int count = Math.Max(asset.HiddenSelections.Count, asset.HiddenSelectionsTextures.Count);
        for (int i = 0; i < count; i++)
        {
            var sel = new RetexSelection
            {
                Index = i,
                Name = i < asset.HiddenSelections.Count ? asset.HiddenSelections[i] : "",
                SourceTexture = i < asset.HiddenSelectionsTextures.Count ? asset.HiddenSelectionsTextures[i] : "",
            };

            bool retex = indices is null || indices.Contains(i);
            if (retex && sel.SourceTexture.Length > 0)
            {
                var bytes = VirtualFileService.Extract(modPboPaths, sel.SourceTexture);
                if (bytes is not null)
                {
                    // Source refs sometimes omit the extension; the copied file must still be a .paa.
                    var raw = Path.GetFileName(sel.SourceTexture.Replace('\\', '/'));
                    if (Path.GetExtension(raw).Length == 0) raw += ".paa";
                    var fileName = UniqueTextureName(proj, raw);
                    File.WriteAllBytes(Path.Combine(proj.TexturesDir, fileName), bytes);
                    sel.ProjectTexture = Path.Combine("textures", fileName);
                }
            }
            entry.Selections.Add(sel);
        }

        proj.Entries.Add(entry);
        return entry;
    }

    /// <summary>Writes config.cpp (and ensures $PBOPREFIX$) and saves retex.json.</summary>
    public static void GenerateConfig(RetexProject proj)
    {
        Directory.CreateDirectory(proj.AddonDir);
        File.WriteAllText(Path.Combine(proj.AddonDir, "$PBOPREFIX$"), proj.Prefix);
        File.WriteAllText(proj.ConfigPath, ConfigGenerator.Generate(proj));
        proj.Save();
    }

    /// <summary>Generates config then packs the addon to "&lt;outputDir&gt;\main.pbo" via pboc.</summary>
    public static async Task<PboResult> PackAsync(RetexProject proj, PboTool tool, string outputDir, CancellationToken ct = default)
    {
        GenerateConfig(proj);
        return await tool.PackAsync(proj.AddonDir, outputDir, ct);
    }

    /// <summary>The "@Name" mod folder under the project directory.</summary>
    public static string ModFolder(RetexProject proj) => Path.Combine(proj.ProjectDir, "@" + Slug(proj.Name));

    /// <summary>Builds a loadable @Mod: writes mod.cpp and packs the addon to @Mod\addons\main.pbo.</summary>
    public static async Task<PboResult> PackModAsync(RetexProject proj, PboTool tool, CancellationToken ct = default)
    {
        GenerateConfig(proj);
        var modDir = ModFolder(proj);
        var addonsOut = Path.Combine(modDir, "addons");
        Directory.CreateDirectory(addonsOut);
        File.WriteAllText(Path.Combine(modDir, "mod.cpp"),
            $"name = \"ReTex - {proj.Name}\";\nauthor = \"{proj.Author}\";\n");
        return await tool.PackAsync(proj.AddonDir, addonsOut, ct);
    }

    private static string UniqueClassName(RetexProject proj, string baseName)
    {
        var name = Sanitize(baseName);
        var taken = proj.Entries.Select(e => e.NewClassName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{name}_{i}")) return $"{name}_{i}";
    }

    private static string UniqueTextureName(RetexProject proj, string fileName)
    {
        var dest = Path.Combine(proj.TexturesDir, fileName);
        if (!File.Exists(dest)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem}_{i}{ext}";
            if (!File.Exists(Path.Combine(proj.TexturesDir, candidate))) return candidate;
        }
    }

    private static string Sanitize(string s)
    {
        var r = new string(s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return char.IsDigit(r.FirstOrDefault()) ? "_" + r : r;
    }

    private static string Slug(string s) => Sanitize(s).ToLowerInvariant();
}
