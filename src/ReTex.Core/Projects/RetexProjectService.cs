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

        // Build a lookup of source textures already copied into this project so that multiple
        // entries/selections referencing the same source .paa share one file instead of duplicating
        // it. Paths are normalized (lowercase, forward slashes, no leading slash) so variant forms
        // like "\foo\bar.paa" and "foo/bar.paa" map to the same key.
        var copiedTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in proj.Entries)
            foreach (var s in e.Selections)
                if (s.ProjectTexture.Length > 0 && s.SourceTexture.Length > 0)
                    copiedTextures.TryAdd(NormTex(s.SourceTexture), s.ProjectTexture);

        var entry = new RetexEntry
        {
            SourceClass = asset.ClassName,
            Category = asset.Category,
            SourceModel = asset.Model,
            SourceAddon = asset.SourceAddon,
            DisplayName = RetexDisplayName(asset.DisplayName, asset.ClassName),
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
            // Drop scopeArsenal/scopeCurator too - the generator always emits its own copies of these
            // (same as scope), so leaving the source's values in would duplicate the member and fail
            // to compile ("Member already defined").
            // Drop model too: some source classes explicitly redeclare their own model= (instead of
            // relying on inheritance), and re-declaring an unchanged model path in a derived class is
            // a known way to break hiddenSelections/hiddenSelectionsTextures binding in Arma - the new
            // class already gets the exact same model via inheritance, so copying it adds nothing.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "scope", "scopeArsenal", "scopeCurator", "displayName", "baseWeapon", "model" };
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
            if (retex)
            {
                var key = NormTex(sel.SourceTexture);
                if (copiedTextures.TryGetValue(key, out var existing))
                    sel.ProjectTexture = existing;
                else
                {
                    sel.ProjectTexture = CopySourceTexture(proj, modPboPaths, sel.SourceTexture);
                    if (sel.ProjectTexture.Length > 0)
                        copiedTextures[key] = sel.ProjectTexture;
                }
            }
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
                var unitEntry = BuildUniformUnit(proj, entry, unitAsset, modPboPaths, copiedTextures);
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

    /// <summary>
    /// Builds the "(ReTex)" display name, falling back to the class name when the source has none.
    /// A source displayName that is a stringtable macro (e.g. "$STR_foo") must be used as-is: Arma
    /// only resolves $STR_ lookups when the whole value is the macro, so appending " (ReTex)" breaks
    /// the lookup entirely and the item ends up with no resolvable name (invisible in the Arsenal).
    /// </summary>
    private static string RetexDisplayName(string sourceDisplayName, string className)
    {
        if (sourceDisplayName.StartsWith('$')) return sourceDisplayName;
        var baseName = sourceDisplayName.Length > 0 ? sourceDisplayName : className;
        return baseName + " (ReTex)";
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
    private static RetexEntry BuildUniformUnit(RetexProject proj, RetexEntry uniformEntry, AssetInfo unitAsset, IReadOnlyList<string> modPboPaths, Dictionary<string, string> copiedTextures)
    {
        var unitEntry = new RetexEntry
        {
            SourceClass = unitAsset.ClassName,
            Category = AssetCategory.Unit,
            SourceModel = unitAsset.Model,
            SourceAddon = unitAsset.SourceAddon,
            DisplayName = RetexDisplayName(unitAsset.DisplayName, unitAsset.ClassName),
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
            // Reuse the texture already copied for the item or any prior entry; copy extras.
            var key = NormTex(src);
            if (copiedTextures.TryGetValue(key, out var existing))
                sel.ProjectTexture = existing;
            else
            {
                sel.ProjectTexture = CopySourceTexture(proj, modPboPaths, src);
                if (sel.ProjectTexture.Length > 0)
                    copiedTextures[key] = sel.ProjectTexture;
            }
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

    /// <summary>
    /// Folds manual edits from the current config text back into the project model so a later
    /// regeneration preserves them instead of clobbering. For each entry (matched by
    /// <see cref="RetexEntry.NewClassName"/>) it captures the edited <c>displayName</c> and, for
    /// entries that carry a copied source body, re-captures that body (armor / ItemInfo / stats / …)
    /// - with the project texture paths mapped back to their source form so the normal
    /// repoint-on-generate still applies. Generator-owned keys (the scope trio, baseWeapon, model)
    /// are never captured. Best-effort: a malformed edit must never block regeneration, so parse/IO
    /// failures are swallowed and the model is left untouched. Caller regenerates afterwards.
    /// </summary>
    public static void PreserveManualEdits(RetexProject proj, string? configText)
    {
        if (string.IsNullOrWhiteSpace(configText)) return;
        try
        {
            var root = Rap.CppConfigParser.Parse(configText);
            // Keys the generator always emits itself - never fold these into the copied body.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "scope", "scopeArsenal", "scopeCurator", "displayName", "baseWeapon", "model" };

            bool changed = false;
            foreach (var e in proj.Entries)
            {
                var node = root.FindDescendant(e.NewClassName);
                if (node is null) continue;

                // Edited display name (applies to every entry, uniforms included).
                var dn = node.StringOr("displayName");
                if (dn.Length > 0 && dn != e.DisplayName) { e.DisplayName = dn; changed = true; }

                // Copied stat body: only entries that already carry one (uniforms are generated, not copied).
                if (e.CopiedBody.Length > 0 && !e.IsUniform && !e.IsUniformUnit)
                {
                    var body = Rap.RapWriter.WriteBody(node, indent: 8, skip).TrimEnd('\r', '\n');
                    // Map project texture paths back to source paths (reverse of generate's repoint) so
                    // the stored body keeps the source-path contract and re-points cleanly next generate.
                    foreach (var s in e.Selections)
                        if (s.ProjectTexture.Length > 0 && s.SourceTexture.Length > 0)
                            body = body.Replace(ConfigGenerator.ProjectVirtual(proj, s.ProjectTexture),
                                                s.SourceTexture, StringComparison.OrdinalIgnoreCase);
                    if (body.Length > 0 && body != e.CopiedBody) { e.CopiedBody = body; changed = true; }
                }
            }
            if (changed) proj.Save();
        }
        catch { /* best-effort: never let a stray edit block regeneration */ }
    }

    /// <summary>Writes config.cpp (and ensures $PBOPREFIX$) and saves retex.json. Consolidates any
    /// duplicate copied textures first (see <see cref="ConsolidateTextures"/>) so the generated
    /// config references only the shared files.</summary>
    public static void GenerateConfig(RetexProject proj)
    {
        Directory.CreateDirectory(proj.AddonDir);
        ConsolidateTextures(proj);
        File.WriteAllText(Path.Combine(proj.AddonDir, "$PBOPREFIX$"), proj.Prefix);
        File.WriteAllText(proj.ConfigPath, ConfigGenerator.Generate(proj));
        proj.Save();
    }

    /// <summary>
    /// Consolidates duplicate copied textures: when several selections were copied from the SAME
    /// source texture into separate project .paa files (e.g. <c>foo.paa</c> + <c>foo_2.paa</c> - as
    /// happened in projects made before on-add de-duplication, or when the same texture is shared
    /// across many models), repoints them at one shared file and deletes the now-orphaned copies.
    /// Only BYTE-IDENTICAL duplicates are merged: if two copies of one source were edited differently
    /// they are left untouched, so no hand edit is ever lost. New retextures already share a file on
    /// add (see <see cref="AddRetexture"/>); this cleans up older/imported projects. Returns the
    /// number of duplicate files removed. Caller saves.
    /// </summary>
    public static int ConsolidateTextures(RetexProject proj)
    {
        string Abs(string projRel) => Path.Combine(proj.AddonDir, projRel);
        IEnumerable<RetexSelection> AllSelections() => proj.Entries.SelectMany(e => e.Selections);

        // Canonical project texture per normalized source (prefer one whose file exists on disk).
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in AllSelections())
        {
            if (s.SourceTexture.Length == 0 || s.ProjectTexture.Length == 0) continue;
            var key = NormTex(s.SourceTexture);
            if (!canonical.TryGetValue(key, out var cur)) canonical[key] = s.ProjectTexture;
            else if (!File.Exists(Abs(cur)) && File.Exists(Abs(s.ProjectTexture))) canonical[key] = s.ProjectTexture;
        }

        // Repoint selections whose file is a byte-identical duplicate of the canonical; track the
        // files we stopped referencing so only those can be deleted.
        var freed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in AllSelections())
        {
            if (s.SourceTexture.Length == 0 || s.ProjectTexture.Length == 0) continue;
            var canon = canonical[NormTex(s.SourceTexture)];
            if (canon.Equals(s.ProjectTexture, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Abs(canon)) && File.Exists(Abs(s.ProjectTexture)) && FilesEqual(Abs(canon), Abs(s.ProjectTexture)))
            {
                freed.Add(s.ProjectTexture);
                s.ProjectTexture = canon;
            }
        }

        // Delete freed files that no selection references any more.
        var referenced = new HashSet<string>(AllSelections().Select(s => s.ProjectTexture)
            .Where(t => t.Length > 0), StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (var projRel in freed)
        {
            if (referenced.Contains(projRel)) continue;
            try { if (File.Exists(Abs(projRel))) { File.Delete(Abs(projRel)); removed++; } } catch { /* leave it */ }
        }
        return removed;
    }

    /// <summary>Byte-for-byte file comparison (length first, then streamed content).</summary>
    private static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        var ba = new byte[65536];
        var bb = new byte[65536];
        int n;
        while ((n = sa.Read(ba, 0, ba.Length)) > 0)
        {
            int off = 0;
            while (off < n) { int k = sb.Read(bb, off, n - off); if (k <= 0) return false; off += k; }
            for (int i = 0; i < n; i++) if (ba[i] != bb[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Finds entries whose ProjectTexture points at a file that no longer exists on disk (e.g. a
    /// stale reference left behind after re-retexturing the same asset created a differently-named
    /// copy, or a manual file cleanup). The config would still reference these paths and pack
    /// "successfully" - the PBO just silently ends up without that texture, so the retexture never
    /// actually applies in-game. Call after opening/regenerating a project so the gap is visible
    /// immediately instead of requiring manual investigation.
    /// </summary>
    public static List<string> FindMissingTextures(RetexProject proj)
    {
        var missing = new List<string>();
        foreach (var e in proj.Entries)
            foreach (var s in e.Selections)
            {
                if (s.ProjectTexture.Length == 0) continue;
                if (!File.Exists(Path.Combine(proj.AddonDir, s.ProjectTexture)))
                    missing.Add($"{e.NewClassName}: {s.ProjectTexture}");
            }
        return missing;
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

    /// <summary>Normalizes a source texture path for dictionary keying: lowercase, forward slashes,
    /// no leading slash. Ensures "\foo\bar.paa" and "foo/bar.paa" resolve to the same key.</summary>
    private static string NormTex(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    /// <summary>Expands a $PBOPREFIX$ template for a project: replaces "{slug}" with the sanitized
    /// project name. An empty/whitespace template falls back to the built-in "z\{slug}\addons\main".</summary>
    public static string ExpandPrefix(string? template, string projectName)
    {
        var t = string.IsNullOrWhiteSpace(template) ? @"z\{slug}\addons\main" : template;
        return t.Replace("{slug}", Slug(projectName));
    }

    /// <summary>Public form of the internal unique-class-name generator, for the app's rename/duplicate flows.</summary>
    public static string MakeUniqueClassName(RetexProject proj, string baseName) => UniqueClassName(proj, baseName);

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
