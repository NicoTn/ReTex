using System.Text;

namespace ReTex.Core.P3d;

/// <summary>
/// Reads the header of an Arma ODOL .p3d: version, LOD count and LOD resolutions.
/// (Full geometry extraction requires parsing the version-specific ModelInfo + LOD
/// lumps and is built on top of this incrementally.)
/// </summary>
public sealed class OdolHeader
{
    public required int Version { get; init; }
    public required int LodCount { get; init; }
    public required float[] Resolutions { get; init; }
    public int HeaderEndOffset { get; init; }

    /// <summary>Indices of the visual ("graphical") LODs - resolutions in [0, 1000).</summary>
    public IEnumerable<int> VisualLodIndices =>
        Enumerable.Range(0, LodCount).Where(i => Resolutions[i] >= 0 && Resolutions[i] < 1000f);
}

/// <summary>The skeleton + bounding info parsed out of ODOL ModelInfo (enough to traverse to the LODs).</summary>
public sealed class OdolModelInfo
{
    public string SkeletonName { get; set; } = "";
    public List<(string Bone, string Parent)> Bones { get; } = new();
    public float[] BBoxMin { get; set; } = new float[3];
    public float[] BBoxMax { get; set; } = new float[3];

    /// <summary>File offset immediately after ModelInfo (start of LOD data) - filled by the reader.</summary>
    public int EndOffset { get; set; }
}

public static class OdolReader
{
    public static bool IsOdol(byte[] d) =>
        d.Length >= 8 && d[0] == 'O' && d[1] == 'D' && d[2] == 'O' && d[3] == 'L';

    public static OdolHeader ReadHeader(byte[] d)
    {
        if (!IsOdol(d))
            throw new NotSupportedException("Not an ODOL p3d (MLOD/unbinarized not supported).");

        int pos = 4;
        int version = ReadI32(d, ref pos);
        _ = ReadU32(d, ref pos);          // appID
        _ = ReadAsciiZ(d, ref pos);       // muzzleFlash / p3d prefix
        int lodCount = ReadI32(d, ref pos);

        if (lodCount is < 0 or > 1000)
            throw new InvalidDataException($"Implausible LOD count {lodCount}; header layout mismatch for v{version}.");

        var res = new float[lodCount];
        for (int i = 0; i < lodCount; i++) res[i] = ReadF32(d, ref pos);

        return new OdolHeader { Version = version, LodCount = lodCount, Resolutions = res, HeaderEndOffset = pos };
    }

    /// <summary>
    /// Parses ModelInfo for ODOL v70+ up to and including the skeleton. The numeric prefix layout
    /// (validated: bytes sum exactly to the skeleton offset on v73) precedes a Skeleton block:
    /// name, isDiscrete, nBones, and (bone, parent) pairs.
    /// </summary>
    public static OdolModelInfo ReadModelInfo(byte[] d, int startPos, int version)
    {
        int p = startPos;
        var mi = new OdolModelInfo();

        ReadI32(d, ref p);                            // index
        ReadF32(d, ref p);                            // memLodSphere
        ReadF32(d, ref p);                            // geoLodSphere
        ReadU32(d, ref p);                            // remarks
        ReadU32(d, ref p);                            // andHints
        ReadU32(d, ref p);                            // orHints
        Skip(ref p, 12);                              // aimingCenter (3f)
        ReadU32(d, ref p);                            // mapIconColor
        ReadU32(d, ref p);                            // mapSelectedColor
        ReadF32(d, ref p);                            // viewDensity
        mi.BBoxMin = ReadVec3(d, ref p);              // bboxMin
        mi.BBoxMax = ReadVec3(d, ref p);              // bboxMax
        if (version >= 70) ReadF32(d, ref p);         // lodDensityCoef
        if (version >= 71) ReadF32(d, ref p);         // drawImportance
        if (version >= 52) { Skip(ref p, 12); Skip(ref p, 12); } // bboxVisual min/max
        Skip(ref p, 12);                              // boundingCenter
        Skip(ref p, 12);                              // geometryCenter
        Skip(ref p, 12);                              // centreOfMass
        Skip(ref p, 36);                              // invInertia (3x3)
        Skip(ref p, 4);                               // auto/lock/canOcclude/canBeOccluded (4 bool)
        if (version >= 73) Skip(ref p, 1);            // aiCovers
        if (version >= 42) Skip(ref p, 16);           // htMin,htMax,afMax,mfMax
        if (version >= 43) Skip(ref p, 8);            // mfAct, tBody
        if (version >= 33) Skip(ref p, 1);            // forceNotAlpha
        if (version >= 37) { ReadU32(d, ref p); Skip(ref p, 1); } // sbSource, prefersShadowVolume
        if (version >= 48) ReadF32(d, ref p);         // shadowOffset
        Skip(ref p, 1);                               // animated

        // --- Skeleton ---
        mi.SkeletonName = ReadAsciiZ(d, ref p);
        if (mi.SkeletonName.Length > 0)
        {
            if (version >= 23) Skip(ref p, 1);        // isDiscrete
            int nBones = ReadI32(d, ref p);
            if (nBones is < 0 or > 100000)
                throw new InvalidDataException($"Implausible bone count {nBones}; ModelInfo layout mismatch.");
            for (int i = 0; i < nBones; i++)
            {
                string bone = ReadAsciiZ(d, ref p);
                string parent = ReadAsciiZ(d, ref p);
                mi.Bones.Add((bone, parent));
            }
        }

        mi.EndOffset = p;
        return mi;
    }

    private static void Skip(ref int p, int n) => p += n;
    private static float[] ReadVec3(byte[] d, ref int p) => new[] { ReadF32(d, ref p), ReadF32(d, ref p), ReadF32(d, ref p) };

    private static int ReadI32(byte[] d, ref int p) { int v = BitConverter.ToInt32(d, p); p += 4; return v; }
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
