namespace ReTex.Core.P3d;

/// <summary>One face (triangle or quad) as vertex-table indices.</summary>
public sealed class OdolFace
{
    public required int[] VertexTableIndex { get; init; } // length 3 or 4
}

/// <summary>
/// A contiguous run of faces sharing one material. <see cref="MaterialIndex"/> indexes
/// <see cref="OdolLodMesh.Materials"/> (-1 = no material/texture).
/// </summary>
public sealed class OdolSection
{
    public required int FaceIndexFrom { get; init; }
    public required int FaceIndexTo { get; init; } // exclusive
    public required int MaterialIndex { get; init; }
    public required int CommonTextureIndex { get; init; } // indexes OdolLodMesh.Textures (-1 = none)
}

/// <summary>
/// A named selection (the same names referenced by hiddenSelections[]): per-point weights
/// (0 = not a member, 1..255 -> ~0..100% per the documented decode) and member face indices.
/// </summary>
public sealed class OdolNamedSelection
{
    public required string Name { get; init; }
    public required byte[] PointWeights { get; init; } // length == mesh point count
    public required byte[] FaceMembership { get; init; } // length == mesh face count (nonzero = member)
}

/// <summary>One resolution LOD's full geometry: points, normals, one UV set, faces, sections, selections.</summary>
public sealed class OdolLodMesh
{
    public required float[][] Points { get; init; } // [i] = {x,y,z}
    public required float[][] Normals { get; init; } // [i] = {x,y,z}, same length as Points or empty
    public required float[][] Uv { get; init; } // [i] = {u,v}, same length as Points or empty
    public required List<OdolFace> Faces { get; init; }
    public required List<OdolSection> Sections { get; init; }
    public required List<string> Textures { get; init; } // raw .paa virtual paths
    public required List<string> Materials { get; init; } // raw .rvmat virtual paths
    public required List<OdolNamedSelection> NamedSelections { get; init; }

    /// <summary>On-disk face vertex-index width in bytes (2 = ushort, 4 = uint32). Needed to map a
    /// section's memory-unit FaceIndexFrom/To back to actual faces.</summary>
    public int FaceIndexWidth { get; init; } = 4;

    /// <summary>Per-face texture index into <see cref="Textures"/> (-1 = none). The unified
    /// grouping key used by <c>OdolMeshPreview</c> for both ODOL (derived from sections) and MLOD
    /// (from each face's texture name). Same length as <see cref="Faces"/> when populated.</summary>
    public List<int> FaceTextureIndex { get; init; } = new();
}
