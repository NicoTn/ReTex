using System.Text;

namespace ReTex.Core.P3d;

/// <summary>
/// Reads an MLOD (unbinarized/"P3DM") .p3d - the editable source format O2/Object Builder produces.
/// Some Workshop mods ship models as MLOD rather than binarized ODOL; those need this path.
///
/// MLOD is much simpler than ODOL: no compression, and each face carries its own vertex point
/// indices, per-vertex UVs, and its texture/material name directly (BI wiki "P3D File Format -
/// MLOD" + "P3D Lod Faces"). Because UVs are per-face-vertex (UV seams), this builds an EXPANDED
/// vertex buffer - one mesh vertex per face-vertex - which is exactly what WPF MeshGeometry3D wants.
/// Faces are grouped by texture via <see cref="OdolLodMesh.FaceTextureIndex"/>, mirroring ODOL.
/// </summary>
public static class MlodReader
{
    public static bool IsMlod(byte[] d) =>
        d.Length >= 4 && d[0] == 'M' && d[1] == 'L' && d[2] == 'O' && d[3] == 'D';

    /// <summary>Reads the highest-detail visual LOD (lowest resolution &lt; 1000) into a mesh, or
    /// null if the file isn't P3DM-MLOD or has no usable geometry. Never throws.</summary>
    public static OdolLodMesh? ReadVisualLod(byte[] d)
    {
        try
        {
            if (!IsMlod(d)) return null;
            int p = 4;
            _ = ReadU32(d, ref p);                 // version
            int nLods = (int)ReadU32(d, ref p);
            if (nLods is < 1 or > 1000) return null;

            OdolLodMesh? best = null;
            float bestRes = float.MaxValue;
            for (int i = 0; i < nLods; i++)
            {
                var (mesh, res) = ReadLod(d, ref p);
                if (mesh != null && res >= 0 && res < 1000 && res < bestRes)
                {
                    best = mesh;
                    bestRes = res;
                }
            }
            return best;
        }
        catch { return null; }
    }

    private static (OdolLodMesh? mesh, float resolution) ReadLod(byte[] d, ref int p)
    {
        string sig = Encoding.ASCII.GetString(d, p, 4); p += 4;
        if (sig != "P3DM")
            throw new NotSupportedException($"MLOD LOD signature '{sig}' (only P3DM/Arma supported).");

        _ = ReadU32(d, ref p);                     // MajorVersion (28)
        _ = ReadU32(d, ref p);                     // MinorVersion
        int nPoints = (int)ReadU32(d, ref p);
        int nNormals = (int)ReadU32(d, ref p);
        int nFaces = (int)ReadU32(d, ref p);
        _ = ReadU32(d, ref p);                     // UnknownFlagBits

        var points = new float[nPoints][];
        for (int i = 0; i < nPoints; i++)
        {
            points[i] = new[] { ReadF32(d, ref p), ReadF32(d, ref p), ReadF32(d, ref p) };
            p += 4;                                 // PointFlags
        }

        var normals = new float[nNormals][];
        for (int i = 0; i < nNormals; i++)
            normals[i] = new[] { ReadF32(d, ref p), ReadF32(d, ref p), ReadF32(d, ref p) };

        // Expanded per-face-vertex buffers.
        var exPoints = new List<float[]>(nFaces * 3);
        var exUv = new List<float[]>(nFaces * 3);
        var exNormals = new List<float[]>(nFaces * 3);
        var faces = new List<OdolFace>(nFaces);
        var faceTexIndex = new List<int>(nFaces);
        var textures = new List<string>();
        var texIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int f = 0; f < nFaces; f++)
        {
            int faceType = (int)ReadU32(d, ref p); // 3 or 4
            // Always 4 PseudoVertexTables: {PointsIndex, NormalsIndex, U, V}
            var vPoint = new int[4]; var vNormal = new int[4]; var vU = new float[4]; var vV = new float[4];
            for (int k = 0; k < 4; k++)
            {
                vPoint[k] = (int)ReadU32(d, ref p);
                vNormal[k] = (int)ReadU32(d, ref p);
                vU[k] = ReadF32(d, ref p);
                vV[k] = ReadF32(d, ref p);
            }
            _ = ReadU32(d, ref p);                 // FaceFlags
            string texName = ReadAsciiZ(d, ref p);
            _ = ReadAsciiZ(d, ref p);              // MaterialName

            if (faceType is not (3 or 4)) throw new InvalidDataException($"Bad MLOD FaceType {faceType} at face {f}.");

            int texIdx = -1;
            if (texName.Length > 0)
            {
                if (!texIndexByName.TryGetValue(texName, out texIdx))
                {
                    texIdx = textures.Count;
                    textures.Add(texName);
                    texIndexByName[texName] = texIdx;
                }
            }

            var idx = new int[faceType];
            for (int k = 0; k < faceType; k++)
            {
                int pi = vPoint[k];
                idx[k] = exPoints.Count;
                exPoints.Add(pi >= 0 && pi < nPoints ? points[pi] : new float[] { 0, 0, 0 });
                exUv.Add(new[] { vU[k], vV[k] });
                int ni = vNormal[k];
                exNormals.Add(ni >= 0 && ni < nNormals ? normals[ni] : new float[] { 0, 0, 1 });
            }
            faces.Add(new OdolFace { VertexTableIndex = idx });
            faceTexIndex.Add(texIdx);
        }

        // Taggs: walk to #EndOfFile#, then read the trailing resolution float.
        _ = Encoding.ASCII.GetString(d, p, 4); p += 4; // "TAGG"
        while (true)
        {
            p += 1;                                 // Active (TinyBool)
            string tagName = ReadAsciiZ(d, ref p);
            int nBytes = (int)ReadU32(d, ref p);
            p += nBytes;                            // skip tag data
            if (tagName == "#EndOfFile#") break;
        }
        float resolution = ReadF32(d, ref p);

        var mesh = new OdolLodMesh
        {
            Points = exPoints.ToArray(),
            Normals = exNormals.ToArray(),
            Uv = exUv.ToArray(),
            Faces = faces,
            Sections = new List<OdolSection>(),
            Textures = textures,
            Materials = new List<string>(),
            NamedSelections = new List<OdolNamedSelection>(),
            FaceTextureIndex = faceTexIndex,
        };
        return (mesh, resolution);
    }

    private static uint ReadU32(byte[] d, ref int p) { uint v = BitConverter.ToUInt32(d, p); p += 4; return v; }
    private static float ReadF32(byte[] d, ref int p) { float v = BitConverter.ToSingle(d, p); p += 4; return v; }

    private static string ReadAsciiZ(byte[] d, ref int p)
    {
        int start = p;
        while (d[p] != 0) p++;
        string s = Encoding.ASCII.GetString(d, start, p - start);
        p++;
        return s;
    }
}
