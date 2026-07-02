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

        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            int texIdx = haveFaceTex ? mesh.FaceTextureIndex[fi] : -1;
            string def = texIdx >= 0 && texIdx < mesh.Textures.Count ? mesh.Textures[texIdx] : "";
            var tex = Resolve(def, selections, projectAddonDir);

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
