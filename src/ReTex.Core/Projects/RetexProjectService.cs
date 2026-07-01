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
    public static RetexEntry AddRetexture(RetexProject proj, AssetInfo asset, IReadOnlyList<string> modPboPaths, IReadOnlyCollection<int>? indices = null, bool copyValues = true, IReadOnlyList<AssetInfo>? modAssets = null)
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

        // Is this a uniform? A uniform is identified by its ItemInfo.uniformClass (it classifies as
        // Equipment/Weapon, not by category). Uniforms get a dedicated, minimal generation path
        // (see ConfigGenerator) and must NOT copy the source body - a copied `class ItemInfo {...}`
        // severs inheritance from the base uniform's ItemInfo (type=801/containerClass/mass), which
        // is exactly what hides the retexture from Arsenal.
        var uniformUnitName = asset.SourceClassNode is not null ? UniformClassOf(asset.SourceClassNode) : "";
        bool isUniform = uniformUnitName.Length > 0;
        entry.IsUniform = isUniform;

        // Copy the source class's declared values (armor, weapon stats, ItemInfo, HitPoints, ...)
        // so the variant is a full, editable copy - not just an inheriting texture swap.
        // Uniforms are the exception: they inherit everything via ": source" and only override
        // textures + uniformClass, so no body copy.
        if (copyValues && !isUniform && asset.SourceClassNode is not null)
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
            if (retex) sel.ProjectTexture = CopySourceTexture(proj, modPboPaths, sel.SourceTexture);
            entry.Selections.Add(sel);
        }

        // Uniforms are a pair: the CfgWeapons item we just built, plus a CfgVehicles "clothing"
        // unit its ItemInfo.uniformClass points at. The worn model comes from the unit; the worn
        // textures come from the item's top-level hiddenSelectionsTextures. We clone the unit too
        // and cross-link them (item.uniformClass -> new unit, unit.uniformClass -> new item) so the
        // pair is self-contained and Arsenal lists the retexture. See ConfigGenerator.
        if (isUniform)
        {
            var unitAsset = modAssets?.FirstOrDefault(a =>
                a.ClassName.Equals(uniformUnitName, StringComparison.OrdinalIgnoreCase));
            if (unitAsset is not null)
            {
                // Cloned unit carries the same textures and points back at our item.
                var unitEntry = BuildUniformUnit(proj, entry, unitAsset, modPboPaths);
                unitEntry.IsUniformUnit = true;
                unitEntry.PartnerClass = entry.NewClassName;
                entry.PartnerClass = unitEntry.NewClassName;
                proj.Entries.Add(unitEntry);
            }
            else
            {
                // Unit not found in the index (e.g. private/no hidden selections): keep the original
                // unit. The item still retextures - worn textures come from the item itself.
                entry.PartnerClass = uniformUnitName;
            }
        }

        proj.Entries.Add(entry);
        return entry;
    }

    /// <summary>Reads a uniform item's clothing-unit class from its ItemInfo (or top-level) uniformClass.</summary>
    private static string UniformClassOf(Rap.RapClass node)
    {
        var u = node.Class("ItemInfo")?.StringOr("uniformClass") ?? "";
        return u.Length > 0 ? u : node.StringOr("uniformClass");
    }

    /// <summary>
    /// Builds the paired CfgVehicles clothing unit for a uniform. The unit wears the same textures as
    /// the item (reusing the .paa already copied where the source paths match, copying any extras).
    /// The reciprocal uniformClass link and the class structure are emitted by ConfigGenerator from
    /// the flags/PartnerClass the caller sets - this method only carries selections + textures.
    /// </summary>
    private static RetexEntry BuildUniformUnit(RetexProject proj, RetexEntry uniformEntry, AssetInfo unitAsset, IReadOnlyList<string> modPboPaths)
    {
        var unitEntry = new RetexEntry
        {
            SourceClass = unitAsset.ClassName,
            Category = AssetCategory.Unit,
            SourceModel = unitAsset.Model,
            SourceAddon = unitAsset.SourceAddon,
            DisplayName = (unitAsset.DisplayName.Length > 0 ? unitAsset.DisplayName : unitAsset.ClassName) + " (ReTex)",
            NewClassName = UniqueClassName(proj, $"{Slug(proj.Name)}_{unitAsset.ClassName}"),
        };

        int count = Math.Max(unitAsset.HiddenSelections.Count, unitAsset.HiddenSelectionsTextures.Count);
        for (int i = 0; i < count; i++)
        {
            var src = i < unitAsset.HiddenSelectionsTextures.Count ? unitAsset.HiddenSelectionsTextures[i] : "";
            var sel = new RetexSelection
            {
                Index = i,
                Name = i < unitAsset.HiddenSelections.Count ? unitAsset.HiddenSelections[i] : "",
                SourceTexture = src,
            };
            // Reuse the texture already copied for the item where the source path matches; copy extras.
            var reuse = uniformEntry.Selections.FirstOrDefault(s => s.ProjectTexture.Length > 0
                && s.SourceTexture.Equals(src, StringComparison.OrdinalIgnoreCase));
            sel.ProjectTexture = reuse?.ProjectTexture ?? CopySourceTexture(proj, modPboPaths, src);
            unitEntry.Selections.Add(sel);
        }

        return unitEntry;
    }

    /// <summary>Copies a source selection's .paa into the project's textures folder; returns the
    /// project-relative path (e.g. "textures\foo.paa") or "" if the source can't be found.</summary>
    private static string CopySourceTexture(RetexProject proj, IReadOnlyList<string> modPboPaths, string sourceTexture)
    {
        if (sourceTexture.Length == 0) return "";
        var bytes = VirtualFileService.Extract(modPboPaths, sourceTexture);
        if (bytes is null) return "";
        // Source refs sometimes omit the extension; the copied file must still be a .paa.
        var raw = Path.GetFileName(sourceTexture.Replace('\\', '/'));
        if (Path.GetExtension(raw).Length == 0) raw += ".paa";
        var fileName = UniqueTextureName(proj, raw);
        File.WriteAllBytes(Path.Combine(proj.TexturesDir, fileName), bytes);
        return Path.Combine("textures", fileName);
    }

    /// <summary>Writes config.cpp (and ensures $PBOPREFIX$) and saves retex.json.</summary>
    public static void GenerateConfig(RetexProject proj)
    {
        Directory.CreateDirectory(proj.AddonDir);
        File.WriteAllText(Path.Combine(proj.AddonDir, "$PBOPREFIX$"), proj.Prefix);
        File.WriteAllText(proj.ConfigPath, ConfigGenerator.Generate(proj));
        proj.Save();
    }

    /// <summary>Packs the addon's CURRENT config.cpp to "&lt;outputDir&gt;\main.pbo" via pboc (does NOT regenerate; manual edits are preserved).</summary>
    public static async Task<PboResult> PackAsync(RetexProject proj, PboTool tool, string outputDir, CancellationToken ct = default)
    {
        EnsurePackable(proj);
        return await tool.PackAsync(proj.AddonDir, outputDir, ct);
    }

    /// <summary>
    /// Ensures the addon is packable WITHOUT clobbering the config: writes $PBOPREFIX$ and only
    /// generates config.cpp if it's missing. Packing must use the config currently on disk (the
    /// last Save / manual edit), not a fresh regeneration.
    /// </summary>
    private static void EnsurePackable(RetexProject proj)
    {
        Directory.CreateDirectory(proj.AddonDir);
        File.WriteAllText(Path.Combine(proj.AddonDir, "$PBOPREFIX$"), proj.Prefix);
        if (!File.Exists(proj.ConfigPath))
            File.WriteAllText(proj.ConfigPath, ConfigGenerator.Generate(proj));
        proj.Save();
    }

    /// <summary>The "@Name" mod folder under the project directory.</summary>
    public static string ModFolder(RetexProject proj) => Path.Combine(proj.ProjectDir, "@" + Slug(proj.Name));

    /// <summary>Builds a loadable @Mod: writes mod.cpp and packs the addon to @Mod\addons\main.pbo.</summary>
    public static async Task<PboResult> PackModAsync(RetexProject proj, PboTool tool, CancellationToken ct = default)
    {
        EnsurePackable(proj);
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
