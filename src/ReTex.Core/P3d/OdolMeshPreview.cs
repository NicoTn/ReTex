using ReTex.Core.Projects;

namespace ReTex.Core.P3d;

/// <summary>The effective texture a face group should render with. UI-agnostic: it names the
/// texture identity (a source virtual path to extract from PBOs, and/or a project .paa file on
/// disk), leaving the actual pixel loading to the app layer.</summary>
public sealed class PreviewTexture
{
    /// <summary>The LOD's own default texture for this section (virtual path, e.g.
    /// "lilardpack\textures\gravispack_co.paa"). Extract from the source PBOs. May be empty.</summary>
    public required string SourceVirtualPath { get; init; }

    /// <summary>Absolute path to the retexture's replacement .paa on disk, when this section was
    /// retextured. Null = not retextured (use <see cref="SourceVirtualPath"/>).</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>True when this section was retextured but its project .paa is missing/unset - the
    /// preview falls back to the source texture but should flag it (matches WarnIfMissingTextures).</summary>
    public bool Missing { get; init; }

    public bool IsRetextured => ProjectFilePath != null;
}

/// <summary>A run of faces sharing one resolved texture - one material/geometry pair to emit.</summary>
public sealed class OdolPreviewGroup
{
    public required PreviewTexture Texture { get; init; }
    public required List<int> FaceIndices { get; init; } // indices into OdolLodMesh.Faces
}

/// <summary>
/// Maps an <see cref="OdolLodMesh"/> to render-ready face groups, resolving each section's
/// effective texture against a retexture's <see cref="RetexSelection"/>s. UI-framework-agnostic
/// (Core) so it is testable from Probe; the app layer loads the actual .paa pixels per group.
///
/// v1 resolution is by TEXTURE PATH: a section's default texture is <c>mesh.Textures[section.
/// CommonTextureIndex]</c>; if a retexture selection's <c>SourceTexture</c> matches it (paths
/// normalized - lowercased, leading backslash trimmed), that selection's <c>ProjectTexture</c>
/// replaces it. This is right for the common case where the model's baked texture equals the
/// config's source texture; full selection-membership mapping (via named-selection SectionIndex)
/// is deferred, as is material (rvmat) swapping.
/// </summary>
public static class OdolMeshPreview
{
    public static List<OdolPreviewGroup> BuildGroups(OdolLodMesh mesh, IReadOnlyList<RetexSelection>? selections, string? projectAddonDir)
    {
        var groups = new Dictionary<string, OdolPreviewGroup>();
        bool haveFaceTex = mesh.FaceTextureIndex.Count == mesh.Faces.Count;

        // Per-face named-selection retexture: hiddenSelections[] replaces the texture on a *named
        // selection*, not on a texture path, so this is the accurate mapping. faceSel[fi] = the index
        // of the retexture selection whose named selection owns face fi, or -1. When -1 we fall back to
        // the path match below (covers models whose selection names don't line up with the config).
        int[] faceSel = BuildFaceSelectionMap(mesh, selections);

        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            int texIdx = haveFaceTex ? mesh.FaceTextureIndex[fi] : -1;
            string def = texIdx >= 0 && texIdx < mesh.Textures.Count ? mesh.Textures[texIdx] : "";
            var tex = faceSel[fi] >= 0
                ? ResolveSelection(def, selections![faceSel[fi]], projectAddonDir) // by named selection
                : Resolve(def, selections, projectAddonDir);                       // by texture path

            string key = tex.ProjectFilePath ?? ("src:" + Normalize(tex.SourceVirtualPath));
            if (!groups.TryGetValue(key, out var g))
            {
                g = new OdolPreviewGroup { Texture = tex, FaceIndices = new List<int>() };
                groups[key] = g;
            }
            g.FaceIndices.Add(fi);
        }

        return groups.Values.Where(g => g.FaceIndices.Count > 0).ToList();
    }

    /// <summary>Maps each face to the retexture selection (index into <paramref name="selections"/>)
    /// whose named selection contains it, or -1. First selection wins when a face is shared. Returns an
    /// all -1 array when the mesh carries no named-selection membership (older parse / MLOD).</summary>
    private static int[] BuildFaceSelectionMap(OdolLodMesh mesh, IReadOnlyList<RetexSelection>? selections)
    {
        var map = new int[mesh.Faces.Count];
        Array.Fill(map, -1);
        if (selections is null || mesh.NamedSelections.Count == 0) return map;
        for (int si = 0; si < selections.Count; si++)
        {
            var sel = selections[si];
            if (sel.Name.Length == 0) continue;
            var ns = mesh.NamedSelections.FirstOrDefault(x => x.Name.Equals(sel.Name, StringComparison.OrdinalIgnoreCase));
            if (ns is null) continue;
            int lim = Math.Min(map.Length, ns.FaceMembership.Length);
            for (int fi = 0; fi < lim; fi++)
                if (ns.FaceMembership[fi] != 0 && map[fi] < 0) map[fi] = si;
        }
        return map;
    }

    /// <summary>Resolves the effective texture for a face known (by named-selection membership) to
    /// belong to <paramref name="sel"/>. Keeps the face's baked <paramref name="faceSourcePath"/> as
    /// the source label / fallback; swaps in the project .paa when present (else flags Missing).</summary>
    private static PreviewTexture ResolveSelection(string faceSourcePath, RetexSelection sel, string? projectAddonDir)
    {
        if (sel.ProjectTexture.Length == 0)
            return new PreviewTexture { SourceVirtualPath = faceSourcePath, Missing = true };
        string proj = projectAddonDir != null ? Path.Combine(projectAddonDir, sel.ProjectTexture) : sel.ProjectTexture;
        return new PreviewTexture { SourceVirtualPath = faceSourcePath, ProjectFilePath = proj };
    }

    private static PreviewTexture Resolve(string defaultVirtualPath, IReadOnlyList<RetexSelection>? selections, string? projectAddonDir)
    {
        if (selections != null && defaultVirtualPath.Length > 0)
        {
            string norm = Normalize(defaultVirtualPath);
            foreach (var sel in selections)
            {
                if (sel.SourceTexture.Length == 0 || Normalize(sel.SourceTexture) != norm) continue;
                if (sel.ProjectTexture.Length == 0)
                    return new PreviewTexture { SourceVirtualPath = defaultVirtualPath, Missing = true };
                string proj = projectAddonDir != null ? Path.Combine(projectAddonDir, sel.ProjectTexture) : sel.ProjectTexture;
                return new PreviewTexture { SourceVirtualPath = defaultVirtualPath, ProjectFilePath = proj };
            }
        }
        return new PreviewTexture { SourceVirtualPath = defaultVirtualPath };
    }

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
}
