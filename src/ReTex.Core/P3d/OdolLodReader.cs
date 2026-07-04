namespace ReTex.Core.P3d;

/// <summary>
/// Parses a single resolution LOD's geometry lump of an ODOL v4x .p3d.
///
/// The authoritative byte layout for every sub-structure referenced here (ODOLv4xLod,
/// VertexTable, LodFace, LodSection, LodNamedSelection, LodMaterial, the CompressedFill /
/// 1024-byte compression rules, and the top-level StartAddressOfLods[] table) is
/// transcribed verbatim from the BI wiki in <c>ODOL_FORMAT_SPEC.md</c> (same folder).
/// Read that first - it is the ground truth this parser is built against.
///
/// STATUS: the full LOD parses end-to-end, validated against LilardPack's Base_Gravis_Pack.p3d
/// (ODOL v73, LOD0 res 1.0). <see cref="ReadFromMinPos"/> decodes, with every value cross-checked
/// against Eliteness: MinPos/MaxPos, textures, materials (incl. all stage-texture paths), the
/// 8813 triangle faces (uint32 vertex indices), the 2 sections (face-range -> texture/material
/// mapping, the retexture-preview key), the 4 named selections, the 2 named properties, frames,
/// the trailer, AND the compressed VertexTable - yielding 7799 decoded vertex positions (valid
/// in-bbox coordinates), plus raw UV/normal arrays. Compressed arrays are LZO1x (<see cref="Lzo1x"/>,
/// validated); the exact byte layout is in ODOL_FORMAT_SPEC.md ("EMPIRICALLY-CONFIRMED v73
/// REFINEMENTS" + "VertexTable framing").
///
/// Remaining polish (not blocking a viewer, which now has points + faces + section->texture):
///   1. Reaching MinPos programmatically: the LOD head (Proxies/LodItems/BoneLinks) isn't decoded
///      yet, so ReadFromMinPos takes the MinPos offset as a parameter (found by the caller), or
///      seek via StartAddressOfLods[]. <see cref="ReadSkeleton"/> still assumes zero head-counts
///      and throws on rigged assets - a loud-failing probe, superseded by ReadFromMinPos.
///   2. UV dequantization (raw 2x uint16 -> float via UvScale) and 10-bit normal unpacking
///      (scale -1/511) - both straightforward from the raw arrays now captured.
///   3. Multiple UV sets (nUVs > 1) and the optional MinMax/VertProperties/neighbor tail.
///
/// Diagnostic Probe verbs for this work (tools/Probe/Program.cs): --p3d, --p3dlod,
/// --p3dstrings, --scanfloats, --scanoffsets, --hexdump, --findfloats, --findint.
/// </summary>
public static class OdolLodReader
{
    /// <summary>Diagnostic-only: when set, ReadMaterial writes per-field offsets to stderr.</summary>
    public static bool Trace;
    private static void T(string msg) { if (Trace) Console.Error.WriteLine(msg); }

    public sealed class LodSkeleton
    {
        public float[] MinPos = new float[3];
        public float[] MaxPos = new float[3];
        public float[] AutoCenter = new float[3];
        public float Sphere;
        public List<string> Textures { get; } = new();
        public List<string> Materials { get; } = new();
        public int FaceCount;
        public int SectionCount;
        public int NamedSelectionCount;
        public List<string> NamedSelectionNames { get; } = new();
        public int TokenCount;
        public int FrameCount;
        public int EndOffset;
    }

    /// <summary>
    /// Phase-1 probe: parses the LOD lump per <c>ODOL_FORMAT_SPEC.md</c> far enough to report
    /// the bbox, texture/material lists and face count, WITHOUT decoding the VertexTable yet.
    /// Throws a specific message at the first lump it can't yet skip (nonzero Proxies/LodItems/
    /// BoneLinks/Edges), so a failure points precisely at what to implement next. Rigged assets
    /// have nonzero LodItems/BoneLinks and will throw here until those are implemented.
    /// </summary>
    public static LodSkeleton ReadSkeleton(byte[] d, int offset, int version)
    {
        int p = offset;
        var s = new LodSkeleton();

        int nProxies = ReadI32(d, ref p);
        if (nProxies != 0) throw new NotSupportedException($"LOD has {nProxies} proxies - LodProxies not yet implemented (offset {p}); see ODOL_FORMAT_SPEC.md.");

        int nLodItems = ReadI32(d, ref p);
        if (nLodItems != 0) throw new NotSupportedException($"LOD has {nLodItems} LOD items - LodItems (potentially compressed) not yet implemented (offset {p}); see ODOL_FORMAT_SPEC.md.");

        int nBoneLinks = ReadI32(d, ref p);
        if (nBoneLinks != 0) throw new NotSupportedException($"LOD has {nBoneLinks} bone links - LodBoneLinks not yet implemented (offset {p}); see ODOL_FORMAT_SPEC.md.");

        ReadF32(d, ref p); // UnknownFloat1
        ReadF32(d, ref p); // UnknownFloat2

        s.MinPos = ReadVec3(d, ref p);
        s.MaxPos = ReadVec3(d, ref p);
        s.AutoCenter = ReadVec3(d, ref p);
        s.Sphere = ReadF32(d, ref p);

        int nTextures = ReadI32(d, ref p);
        for (int i = 0; i < nTextures; i++) s.Textures.Add(ReadAsciiZ(d, ref p));

        int nMaterials = ReadI32(d, ref p);
        for (int i = 0; i < nMaterials; i++) s.Materials.Add(ReadAsciiZ(d, ref p));

        // LodEdges: CompressedFill array (see spec). Only the zero-count case is handled here.
        int nEdges = BinaryUtil.ReadCompressedInt(d, ref p);
        if (nEdges != 0) throw new NotSupportedException($"LOD has {nEdges} edges - LodEdges (compressed) not yet implemented (offset {p}); see ODOL_FORMAT_SPEC.md.");

        s.FaceCount = ReadI32(d, ref p);
        _ = ReadU32(d, ref p); // OffsetToSectionsStruct (== faces memory AllocationSize)
        _ = ReadU16(d, ref p); // AlwaysZero

        // Faces / sections / named-selections / VertexTable decoding is the next step.
        s.EndOffset = p;
        return s;
    }

    /// <summary>Reads the closest/highest-detail visual LOD of an ODOL p3d end-to-end into a
    /// render-ready <see cref="OdolLodMesh"/> (points, unpacked normals, dequantized UVs, faces,
    /// sections with texture/material indices, texture+material paths). Locates the first LOD's
    /// MinPos by scanning (the LOD head - Proxies/LodItems/BoneLinks - isn't decoded), parses via
    /// <see cref="ReadFromMinPos"/>, then dequantizes/unpacks the vertex arrays.
    ///
    /// Verified on modern ODOL v73 (the format real Arma 3 retexture-target mods use, e.g.
    /// LilardPack). Older ODOL (e.g. v59) has layout differences (texture/material encoding not in
    /// the same place) and is not yet supported - this returns null for it, so callers fall back
    /// to the flat 2D texture preview. Returns null on any decode failure (never throws).</summary>
    public static OdolLodMesh? ReadVisualLod(byte[] d)
    {
        LodMeshInfo m;
        try
        {
            var hdr = OdolReader.ReadHeader(d);
            var mi = OdolReader.ReadModelInfo(d, hdr.HeaderEndOffset, hdr.Version);
            // Scan the file for the best usable LOD. FindLodMinPos returns anchors that pass bbox-
            // containment + a valid texture trailer. LODs are NOT stored in resolution order, and the
            // visual LODs can sit anywhere (often AFTER the geometry/shadow LODs, which carry a
            // degenerate placeholder UV set - all UVs collapse to one texel, so they render
            // untextured). So we walk ALL anchors and pick the highest-detail (most faces) LOD that
            // decodes cleanly WITH non-degenerate UVs; if none qualifies we fall back to the first that
            // decodes at all (which the degenerate-UV guard below then turns into a 2D-preview fallback).
            LodMeshInfo? firstDecodable = null, bestVisual = null;
            int searchFrom = mi.EndOffset;
            for (int guard = 0; guard < 64; guard++)
            {
                int minPos = FindLodMinPos(d, searchFrom, d.Length, mi.BBoxMin, mi.BBoxMax);
                if (minPos < 0) break;
                LodMeshInfo cand;
                try { cand = ReadFromMinPos(d, minPos, hdr.Version); }
                catch { searchFrom = minPos + 1; continue; }
                // Advance past this LOD's whole vertex table so the next scan finds the NEXT LOD.
                int lodEnd = cand.VertexTableStartOffset > 0 ? cand.VertexTableStartOffset - 4 + cand.SizeOfVertexTable : minPos;
                searchFrom = Math.Max(lodEnd, minPos) + 1;
                if (cand.VertexTableError != null || cand.Points == null) continue;
                firstDecodable ??= cand;
                // Non-degenerate UVs: the UV bounds (UvScale = [minU,minV,maxU,maxV]) must span a real
                // range, not collapse to ~0 (the placeholder case). Among those, keep the most detailed.
                if (cand.UvScale is { Length: 4 } us && Math.Max(Math.Abs(us[2]), Math.Abs(us[3])) > 0.01f
                    && (bestVisual is null || cand.FaceCount > bestVisual.FaceCount))
                    bestVisual = cand;
            }
            m = bestVisual ?? firstDecodable!;
            if (m is null || m.VertexTableError != null || m.Points is null) return null;
        }
        catch { return null; }
        // Degenerate placeholder UVs (all collapse to ~one texel) can't be textured usefully - some
        // ODOL v75 models store the real UVs in a not-yet-decoded layout and leave a placeholder here.
        // Returning null makes the app fall back to the flat 2D texture preview instead of rendering a
        // gray/garbled model.
        if (m.UvScale is not { Length: 4 } uvScale
            || Math.Max(Math.Abs(uvScale[2]), Math.Abs(uvScale[3])) <= 0.01f) return null;

        int n = m.Points!.Length;
        var uv = DequantizeUvs(m.UvRaw, m.UvScale, n);
        var uv2 = DequantizeUvs(m.UvRaw2, m.UvScale2, n);
        var normals = UnpackNormals(m.NormalRaw, n);

        var sections = m.Sections.Select(s => new OdolSection
        {
            FaceIndexFrom = s.FaceMemFrom, FaceIndexTo = s.FaceMemTo,
            MaterialIndex = s.MaterialIndex, CommonTextureIndex = s.CommonTextureIndex,
        }).ToList();

        // Resolve each section's effective texture PATH, then build a de-duplicated texture list and
        // a per-face index into it. A section names its texture either directly (CommonTextureIndex
        // into the LOD's Textures[] - the Gravis-pack case) or, typical for vehicles/props with
        // multi-stage materials, only via its material, whose diffuse (_co) stage texture we use.
        var matDiffuse = m.Materials.Select(mat => PickDiffuseTexture(mat.StageTextures)).ToList();
        var texList = new List<string>();
        var texIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int SectionTex(OdolSection s)
        {
            string path = s.CommonTextureIndex >= 0 && s.CommonTextureIndex < m.Textures.Count
                ? m.Textures[s.CommonTextureIndex]
                : (s.MaterialIndex >= 0 && s.MaterialIndex < matDiffuse.Count ? matDiffuse[s.MaterialIndex] : "");
            if (path.Length == 0) return -1;
            if (!texIds.TryGetValue(path, out int id)) { id = texList.Count; texList.Add(path); texIds[path] = id; }
            return id;
        }
        var sectionTex = sections.Select(SectionTex).ToList();

        var faceTex = FaceTextureIndicesFromSections(m.Faces, sections, sectionTex, m.FaceIndexWidth);
        // Drop proxy triangles (weapon/pistol/launcher/... attachment markers) + degenerate faces, which
        // are stored in the LOD's face list and otherwise render as huge stray triangles across the
        // model. They span implausibly far vs the median edge; a robust multiple separates them from
        // real geometry. Fail-safe: if too many would drop, keep everything (never nuke real geometry).
        var (faces, faceTexFiltered, keptIndices) = DropSpikeFaces(m.Faces, faceTex, m.Points);

        // Named-selection face membership (hiddenSelections[] -> faces), remapped through the spike
        // filter so ordinals stay aligned with the kept face list. Enables texture-path-independent
        // retexture mapping in the preview (OdolMeshPreview).
        var namedSelections = BuildNamedSelections(m, keptIndices, faces.Count);

        return new OdolLodMesh
        {
            Points = m.Points,
            Normals = normals,
            Uv = uv,
            Uv2 = uv2,
            Faces = faces,
            Sections = sections,
            Textures = texList,
            Materials = m.Materials.Select(x => x.RvMatName).ToList(),
            NamedSelections = namedSelections,
            FaceIndexWidth = m.FaceIndexWidth,
            FaceTextureIndex = faceTexFiltered,
        };
    }

    /// <summary>Removes "spike" faces - proxy triangles and degenerate geometry whose longest edge is a
    /// large multiple (30x) of the median face edge - which otherwise render as stray triangles. Returns
    /// the filtered faces + their parallel texture indices. No-op if the filter would remove more than 3%
    /// of faces (guards against models with legitimately varied geometry).</summary>
    private static (List<OdolFace> Faces, List<int> FaceTex, int[] KeptIndices) DropSpikeFaces(List<OdolFace> faces, List<int> faceTex, float[][] pts)
    {
        int[] Identity() { var a = new int[faces.Count]; for (int i = 0; i < a.Length; i++) a[i] = i; return a; }
        if (faces.Count == 0) return (faces, faceTex, Identity());
        float LongestEdge(OdolFace f)
        {
            float max = 0; var vi = f.VertexTableIndex;
            for (int i = 0; i < vi.Length; i++)
            {
                int a = vi[i], b = vi[(i + 1) % vi.Length];
                if (a >= pts.Length || b >= pts.Length) continue;
                float dx = pts[a][0] - pts[b][0], dy = pts[a][1] - pts[b][1], dz = pts[a][2] - pts[b][2];
                max = Math.Max(max, dx * dx + dy * dy + dz * dz); // squared; compare against squared threshold
            }
            return max;
        }
        var lens = new float[faces.Count];
        for (int i = 0; i < faces.Count; i++) lens[i] = LongestEdge(faces[i]);
        var sorted = (float[])lens.Clone();
        Array.Sort(sorted);
        float median = sorted[sorted.Length / 2];
        if (median <= 0) return (faces, faceTex, Identity());
        float threshold = median * (30f * 30f); // 30x on edge length == 900x on squared length

        // Distinguish proxy triangles from legitimately large panels by FRACTION. A character/gear LOD
        // has only a handful of proxy markers (weapon/pistol/... ~0.03% of faces); a vehicle has many
        // genuinely large hull plates (~0.5%+). So drop the over-long faces only when they are a tiny
        // fraction of the mesh - otherwise (vehicles/props) leave every face intact.
        int overLong = 0;
        for (int i = 0; i < faces.Count; i++) if (lens[i] > threshold) overLong++;
        if (overLong == 0 || overLong > faces.Count * 0.003) return (faces, faceTex, Identity());

        var keptFaces = new List<OdolFace>(faces.Count);
        var keptTex = new List<int>(faces.Count);
        var kept = new List<int>(faces.Count);
        for (int i = 0; i < faces.Count; i++)
            if (lens[i] <= threshold) { keptFaces.Add(faces[i]); if (i < faceTex.Count) keptTex.Add(faceTex[i]); kept.Add(i); }
        return (keptFaces, keptTex, kept.ToArray());
    }

    /// <summary>Builds <see cref="OdolNamedSelection"/>s (name + per-face membership) from the raw
    /// face-ordinal lists parsed off the LOD, remapping ordinals through the spike filter's
    /// <paramref name="keptIndices"/> so membership aligns with the kept face list. Point weights are
    /// not decoded (unused by the preview), so <see cref="OdolNamedSelection.PointWeights"/> is empty.</summary>
    private static List<OdolNamedSelection> BuildNamedSelections(LodMeshInfo m, int[] keptIndices, int keptFaceCount)
    {
        var result = new List<OdolNamedSelection>();
        if (m.NamedSelections.Count == 0) return result;
        // Map old face ordinal -> new (post-filter) ordinal; -1 for dropped faces.
        var remap = new int[m.Faces.Count];
        Array.Fill(remap, -1);
        for (int ni = 0; ni < keptIndices.Length; ni++) remap[keptIndices[ni]] = ni;

        // Face-ordinal [from,to) range of each LodSection (sections partition the face block in order;
        // FaceMemFrom/To are cumulative MEMORY offsets, stride = width*(1+FaceType)). A sectional
        // selection's membership is the union of its sections' face ranges.
        var (secFrom, secTo) = SectionFaceRanges(m.Faces, m.Sections, m.FaceIndexWidth);

        void Mark(byte[] membership, int oldOrdinal)
        {
            if (oldOrdinal < 0 || oldOrdinal >= remap.Length) return;
            int nw = remap[oldOrdinal];
            if (nw >= 0) membership[nw] = 1;
        }

        for (int i = 0; i < m.NamedSelections.Count; i++)
        {
            var faceIdx = i < m.NamedSelectionFaceIndexes.Count ? m.NamedSelectionFaceIndexes[i] : Array.Empty<int>();
            var secIdx = i < m.NamedSelectionSectionIndexes.Count ? m.NamedSelectionSectionIndexes[i] : Array.Empty<int>();
            var membership = new byte[keptFaceCount];
            if (faceIdx.Length > 0)
                foreach (int old in faceIdx) Mark(membership, old);
            else
                foreach (int s in secIdx)
                    if (s >= 0 && s < secFrom.Length)
                        for (int f = secFrom[s]; f < secTo[s]; f++) Mark(membership, f);
            result.Add(new OdolNamedSelection
            {
                Name = m.NamedSelections[i],
                PointWeights = Array.Empty<byte>(),
                FaceMembership = membership,
            });
        }
        return result;
    }

    /// <summary>Computes each LodSection's face-ordinal [from,to) range by walking the face block and
    /// tracking the cumulative memory cursor (stride = width*(1+FaceType)) against the sections'
    /// memory-offset bounds - the same accounting <see cref="FaceTextureIndicesFromSections"/> uses.</summary>
    private static (int[] From, int[] To) SectionFaceRanges(List<OdolFace> faces, List<SectionInfo> sections, int width)
    {
        int n = sections.Count;
        var from = new int[n];
        var to = new int[n];
        for (int s = 0; s < n; s++) { from[s] = -1; to[s] = -1; }
        if (n == 0) return (from, to);
        int memOffset = 0, si = 0;
        for (int fi = 0; fi < faces.Count; fi++)
        {
            while (si < n - 1 && memOffset >= sections[si].FaceMemTo) si++;
            if (from[si] < 0) from[si] = fi;
            to[si] = fi + 1;
            memOffset += width * (1 + faces[fi].VertexTableIndex.Length);
        }
        for (int s = 0; s < n; s++) if (from[s] < 0) { from[s] = 0; to[s] = 0; }
        return (from, to);
    }

    /// <summary>Picks a material's diffuse texture from its stage textures: prefer the colour map
    /// (path ending "_co.paa"), else the first non-empty stage. Empty when the material has none.</summary>
    private static string PickDiffuseTexture(IReadOnlyList<string> stageTextures)
    {
        foreach (var t in stageTextures)
            if (t.EndsWith("_co.paa", StringComparison.OrdinalIgnoreCase)) return t;
        return stageTextures.FirstOrDefault(t => t.Length > 0) ?? "";
    }

    /// <summary>Assigns each face its owning section's resolved texture index (into the mesh's texture
    /// list). Sections cover consecutive face blocks; their FaceIndexFrom/To are cumulative memory
    /// offsets (stride = width*(1+FaceType)), so we walk faces tracking the memory cursor and advance
    /// the section as it is crossed.</summary>
    private static List<int> FaceTextureIndicesFromSections(List<OdolFace> faces, List<OdolSection> sections, List<int> sectionTex, int width)
    {
        var result = new List<int>(faces.Count);
        if (sections.Count == 0) { for (int i = 0; i < faces.Count; i++) result.Add(-1); return result; }
        int memOffset = 0, si = 0;
        foreach (var f in faces)
        {
            while (si < sections.Count - 1 && memOffset >= sections[si].FaceIndexTo) si++;
            result.Add(sectionTex[si]);
            memOffset += width * (1 + f.VertexTableIndex.Length);
        }
        return result;
    }

    /// <summary>Dispatches to the ODOL or MLOD reader by magic. The single entry point the app
    /// should call for any .p3d - returns null (never throws) for unsupported formats.</summary>
    public static OdolLodMesh? ReadAnyVisualLod(byte[] d)
    {
        if (OdolReader.IsOdol(d)) return ReadVisualLod(d);
        if (MlodReader.IsMlod(d)) return MlodReader.ReadVisualLod(d);
        return null;
    }

    /// <summary>Scans for the first LOD's MinPos field: 6 floats forming a box that sits inside
    /// the model bbox (from ModelInfo), immediately followed by AutoCenter(3f)+Sphere(f)+
    /// NoOfTextures(int in range) and a valid ASCII texture path. The bbox-containment plus the
    /// texture-path trailer make false positives essentially impossible. Returns the MinPos file
    /// offset, or -1.</summary>
    public static int FindLodMinPos(byte[] d, int from, int to, float[] modelMin, float[] modelMax)
    {
        const float eps = 0.02f;
        to = Math.Min(to, d.Length - 64);
        for (int p = from; p < to; p++)
        {
            var min = new[] { BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), BitConverter.ToSingle(d, p + 8) };
            var max = new[] { BitConverter.ToSingle(d, p + 12), BitConverter.ToSingle(d, p + 16), BitConverter.ToSingle(d, p + 20) };
            bool box = true;
            for (int i = 0; i < 3 && box; i++)
            {
                if (!IsFinite(min[i]) || !IsFinite(max[i]) || min[i] > max[i]) box = false;
                // LOD bbox must sit within the model bbox (they derive from the same geometry)
                else if (min[i] < modelMin[i] - eps || max[i] > modelMax[i] + eps) box = false;
            }
            if (!box) continue;
            // non-degenerate box (some extent on at least one axis)
            if (max[0] - min[0] < 1e-4 && max[1] - min[1] < 1e-4 && max[2] - min[2] < 1e-4) continue;

            int q = p + 24;                                   // AutoCenter(3f)+Sphere(f)
            for (int i = 0; i < 4; i++) if (!IsFinite(BitConverter.ToSingle(d, q + i * 4))) { q = -1; break; }
            if (q < 0) continue;
            int nTex = BitConverter.ToInt32(d, p + 24 + 16);  // NoOfTextures
            if (nTex is < 1 or > 64) continue;                // a visual LOD has >=1 texture
            if (!LooksLikeTexturePath(d, p + 24 + 20)) continue;
            return p;
        }
        return -1;
    }

    private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

    private static bool LooksLikeTexturePath(byte[] d, int p)
    {
        int start = p, max = Math.Min(d.Length, p + 260);
        while (p < max && d[p] != 0)
        {
            byte b = d[p];
            if (b < 0x20 || b > 0x7E) return false;           // printable ASCII only
            p++;
        }
        int len = p - start;
        if (len < 5 || p >= max) return false;
        string sPath = System.Text.Encoding.ASCII.GetString(d, start, len).ToLowerInvariant();
        return sPath.EndsWith(".paa") || sPath.EndsWith(".pac") || sPath.EndsWith(".tga");
    }

    /// <summary>Dequantizes packed UVs (2x uint16 per vertex) to floats using the UVScale
    /// bounds [minU, minV, maxU, maxV]. Returns empty if the raw array is absent/mismatched.</summary>
    private static float[][] DequantizeUvs(byte[]? raw, float[]? scale, int n)
    {
        if (raw == null || scale == null || scale.Length < 4 || raw.Length < n * 4) return Array.Empty<float[]>();
        float minU = scale[0], minV = scale[1], maxU = scale[2], maxV = scale[3];
        var uv = new float[n][];
        for (int i = 0; i < n; i++)
        {
            ushort ru = BitConverter.ToUInt16(raw, i * 4);
            ushort rv = BitConverter.ToUInt16(raw, i * 4 + 2);
            uv[i] = new[] { minU + (ru / 65535f) * (maxU - minU), minV + (rv / 65535f) * (maxV - minV) };
        }
        return uv;
    }

    /// <summary>Unpacks CompressedXYZTriplet normals (3x 10-bit signed fields in a uint32,
    /// scale -1/511). Returns empty if the raw array is absent/mismatched.</summary>
    private static float[][] UnpackNormals(byte[]? raw, int n)
    {
        if (raw == null || raw.Length < n * 4) return Array.Empty<float[]>();
        const float scale = -1.0f / 511f;
        var normals = new float[n][];
        for (int i = 0; i < n; i++)
        {
            uint c = BitConverter.ToUInt32(raw, i * 4);
            int x = (int)(c & 0x3FF), y = (int)((c >> 10) & 0x3FF), z = (int)((c >> 20) & 0x3FF);
            if (x > 511) x -= 1024;
            if (y > 511) y -= 1024;
            if (z > 511) z -= 1024;
            normals[i] = new[] { x * scale, y * scale, z * scale };
        }
        return normals;
    }

    /// <summary>Richer forward parse starting at the LOD's MinPos field (bypasses the
    /// not-yet-decoded Proxies/LodItems/BoneLinks head by anchoring on the located MinPos).
    /// Parses MinPos..Sphere, textures, materials (full stage-texture paths), edges, face
    /// count and sections. Does NOT decode the VertexTable yet. Used to validate the
    /// material/faces/sections layout against known-good values before wiring in LOD seeking.</summary>
    public static LodMeshInfo ReadFromMinPos(byte[] d, int minPosOffset, int version)
    {
        int p = minPosOffset;
        var m = new LodMeshInfo();
        m.MinPos = ReadVec3(d, ref p);
        m.MaxPos = ReadVec3(d, ref p);
        m.AutoCenter = ReadVec3(d, ref p);
        m.Sphere = ReadF32(d, ref p);

        int nTextures = ReadI32(d, ref p);
        if (nTextures is < 0 or > 4096) throw new InvalidDataException($"Implausible NoOfTextures {nTextures} at {minPosOffset} - not a MinPos anchor.");
        for (int i = 0; i < nTextures; i++) m.Textures.Add(ReadAsciiZ(d, ref p));

        int nMaterials = ReadI32(d, ref p);
        if (nMaterials is < 0 or > 4096) throw new InvalidDataException($"Implausible NoOfMaterials {nMaterials} at offset {p}.");
        for (int i = 0; i < nMaterials; i++) m.Materials.Add(ReadMaterial(d, ref p, version));
        m.MaterialsEndOffset = p;

        // LodEdges: two "potentially compressed" edge arrays (mlod-style + odol-style). On this
        // asset both counts are 0 (8 zero bytes). Nonzero/compressed edges aren't decoded yet.
        uint nEdges1 = ReadU32(d, ref p);
        uint nEdges2 = ReadU32(d, ref p);
        if (nEdges1 != 0 || nEdges2 != 0)
            throw new NotSupportedException($"LOD has edges ({nEdges1},{nEdges2}) at {p - 8} - compressed LodEdges not yet implemented; see ODOL_FORMAT_SPEC.md.");

        m.FaceCount = ReadI32(d, ref p);
        if (m.FaceCount is < 0 or > 20_000_000) throw new InvalidDataException($"Implausible NoOfFaces {m.FaceCount} at {p - 4}.");
        m.FacesAllocationSize = (int)ReadU32(d, ref p);   // OffsetToSectionsStruct == memory AllocationSize
        _ = ReadU16(d, ref p);                            // AlwaysZero

        // Faces: each = byte FaceType (3|4) + VertexTableIndex[FaceType]. The index width is
        // version-dependent (older ODOL = ushort, newer e.g. v73 = uint32). Detect it from the
        // memory AllocationSize: mem/face is 4+4*FaceType (uint32) vs 2+2*FaceType (ushort), i.e.
        // 16-20 vs 8-10 per face - exactly 2x apart with no overlap, so a /12 threshold is robust.
        int indexWidth = m.FaceCount > 0 && m.FacesAllocationSize / (double)m.FaceCount >= 12 ? 4 : 2;
        m.FaceIndexWidth = indexWidth;
        m.FacesStartOffset = p;
        for (int i = 0; i < m.FaceCount; i++)
        {
            byte faceType = d[p++];
            if (faceType != 3 && faceType != 4)
                throw new InvalidDataException($"Bad FaceType {faceType} at face {i} (offset {p - 1}); indexWidth={indexWidth}.");
            var idx = new int[faceType];
            for (int k = 0; k < faceType; k++) idx[k] = indexWidth == 4 ? (int)ReadU32(d, ref p) : ReadU16(d, ref p);
            if (faceType == 3) m.TriCount++; else m.QuadCount++;
            m.Faces.Add(new OdolFace { VertexTableIndex = idx });
        }
        m.FacesEndOffset = p;

        // Sections: face-range -> material/texture mapping (the retexture-preview key).
        int nSections = ReadI32(d, ref p);
        if (nSections is < 0 or > 100000) throw new InvalidDataException($"Implausible nSections {nSections} at {p - 4}.");
        for (int i = 0; i < nSections; i++)
        {
            uint faceFrom = ReadU32(d, ref p);       // FaceIndexOffsets[0] (memory offset: 16/tri, 20/quad)
            uint faceTo = ReadU32(d, ref p);         // FaceIndexOffsets[1]
            ReadU32(d, ref p); ReadU32(d, ref p);    // MaterialIndexOffsets[2]
            ReadU32(d, ref p);                       // CommonPointsUserValue
            short commonTexIndex = (short)ReadU16(d, ref p);
            ReadU32(d, ref p);                       // CommonFaceFlags
            int materialIndex = ReadI32(d, ref p);
            if (materialIndex == -1) Skip(ref p, 1); // ExtraByte
            ReadU32(d, ref p);                       // UnknowLong (generally 2)
            ReadF32(d, ref p); ReadF32(d, ref p);    // UnknownResolution 1/2 (2nd generally +/-1000)
            ReadU32(d, ref p);                       // trailing ulong (empirically present on v73, beyond the wiki spec)
            m.Sections.Add(new SectionInfo
            {
                FaceMemFrom = (int)faceFrom, FaceMemTo = (int)faceTo,
                CommonTextureIndex = commonTexIndex, MaterialIndex = materialIndex,
            });
        }
        m.SectionsEndOffset = p;

        // Named selections (hiddenSelections membership). Arrays with count>0 carry a leading
        // compression-flag byte (0 = raw, else LZO1x). NoOfUlongs is always present.
        int nSel = ReadI32(d, ref p);
        if (nSel is < 0 or > 100000) throw new InvalidDataException($"Implausible nNamedSelections {nSel} at {p - 4}.");
        // FaceIndexes and VertexTableIndexes use the LOD's vertex-index width (uint32 on v73,
        // ushort on older ODOL) - the same widening as the face records. Always0/SectionIndex are
        // ulong, weights are bytes. Larger arrays are LZO-packed (flag byte != 0) - vehicles and
        // characters have hundreds of named selections whose index arrays exceed the raw threshold.
        int w = m.FaceIndexWidth;
        for (int i = 0; i < nSel; i++)
        {
            string name = ReadAsciiZ(d, ref p);
            // FaceIndexes are ordinals into LodFaces (per the spec) - the faces belonging to this
            // named selection. hiddenSelections[] retexture works by named selection, so we keep
            // these to map a retexture onto exactly the right faces in the 3D preview (regardless of
            // the model's baked texture path). The rule-array framing/consumption is identical to
            // SkipRuleArray, so VertexTable alignment is unchanged; we merely read instead of skip.
            int nFaces = ReadI32(d, ref p);
            byte[] faceRaw = ReadRuleArray(d, ref p, nFaces, w);
            SkipRuleArray(d, ref p, ReadI32(d, ref p), 4);   // Always0 array (ulong)
            Skip(ref p, 1);                                  // IsSectional (tbool)
            // SectionIndex[] - for "sectional" selections (the norm for hiddenSelections camo groups on
            // characters) the face membership is expressed as the LodSections they cover, and FaceIndexes
            // above is empty. We read these to resolve the retexture onto the right faces via the sections.
            int nSecIdx = ReadI32(d, ref p);
            byte[] secRaw = ReadRuleArray(d, ref p, nSecIdx, 4);   // SectionIndex (ulong, indexes LodSections)
            SkipRuleArray(d, ref p, ReadI32(d, ref p), w);   // VertexTableIndexes (uint32/ushort)
            SkipRuleArray(d, ref p, ReadI32(d, ref p), 1);   // VerticesWeights (byte)
            var faceIdx = new int[nFaces];
            for (int k = 0; k < nFaces; k++)
                faceIdx[k] = w == 4 ? (int)BitConverter.ToUInt32(faceRaw, k * 4) : BitConverter.ToUInt16(faceRaw, k * 2);
            var secIdx = new int[nSecIdx];
            for (int k = 0; k < nSecIdx; k++) secIdx[k] = (int)BitConverter.ToUInt32(secRaw, k * 4);
            m.NamedSelections.Add(name);
            m.NamedSelectionFaceIndexes.Add(faceIdx);
            m.NamedSelectionSectionIndexes.Add(secIdx);
        }
        m.SelectionsEndOffset = p;

        // NamedProperties: nTokens * (asciiz Property, asciiz Value).
        int nTokens = ReadI32(d, ref p);
        if (nTokens is < 0 or > 100000) throw new InvalidDataException($"Implausible nTokens {nTokens} at {p - 4}.");
        for (int i = 0; i < nTokens; i++)
        {
            string prop = ReadAsciiZ(d, ref p);
            string val = ReadAsciiZ(d, ref p);
            m.NamedProperties.Add((prop, val));
        }
        m.TokensEndOffset = p;

        // Frames (animation keyframes): 0 on a static prop. Nonzero not yet decoded.
        int nFrames = ReadI32(d, ref p);
        if (nFrames != 0) throw new NotSupportedException($"LOD has {nFrames} frames - LodFrames not yet implemented (offset {p - 4}).");

        // Trailer before the vertex table.
        ReadU32(d, ref p);                           // IconColor
        ReadU32(d, ref p);                           // SelectedColor
        ReadU32(d, ref p);                           // special (IsAlpha|IsTransparent|IsAnimated|OnSurface)
        Skip(ref p, 1);                              // vertexBoneRefIsSimple
        m.SizeOfVertexTable = (int)ReadU32(d, ref p); // includes these 4 bytes
        m.VertexTableStartOffset = p;

        DecodeVertexTable(d, ref p, m, version);
        return m;
    }

    /// <summary>Decodes the VertexTable per the confirmed framing (see ODOL_FORMAT_SPEC.md):
    /// LodPointFlags, DefaultUVset (UVScale + compressed UVs), nUVs, NoOfPoints + compressed
    /// points, nNormals + compressed normals. Populates <see cref="LodMeshInfo.Points"/>
    /// (and raw UV/normal bytes). Compressed arrays are LZO1x (packing flag 0x02).</summary>
    private static void DecodeVertexTable(byte[] d, ref int p, LodMeshInfo m, int version)
    {
        try
        {
            int vtEnd = m.VertexTableStartOffset - 4 + m.SizeOfVertexTable;
            T($"[VT] start={p} sizeOfVT={m.SizeOfVertexTable} vtEnd={vtEnd}");
            if (version >= 50)
            {
                T($"[VT] LodPointFlags @ {p}");
                ReadArrayWithCount(d, ref p, 4, hasDefaultFill: true, out _); // LodPointFlags (skipped)
            }

            T($"[VT] UvScale @ {p}");
            m.UvScale = new[] { ReadF32(d, ref p), ReadF32(d, ref p), ReadF32(d, ref p), ReadF32(d, ref p) };
            T($"[VT]   UvScale={string.Join(",", m.UvScale)}");
            T($"[VT] DefaultUVset @ {p}");
            m.UvRaw = ReadArrayWithCount(d, ref p, 4, hasDefaultFill: true, out int uvCount); // DefaultUVset LodUV

            T($"[VT] nUVs @ {p}");
            int nUVs = ReadI32(d, ref p);
            T($"[VT]   nUVs={nUVs}");
            if (nUVs is < 1 or > 16) throw new InvalidDataException($"Implausible nUVs={nUVs} at {p - 4}.");
            // Additional UV sets (detail/macro-map coordinates) follow UV set 0. We only texture from
            // set 0, so read past each extra UvSet (UVScale[4] = 16 bytes + a CompressedFill UV array
            // in the same 4-byte packed format) to reach the point data. Many Arma-3 character/gear
            // LODs ship 2 UV sets; the earlier hard throw on nUVs>1 made those LODs undecodable and
            // forced a fallback to the model's non-visual (geometry) LOD.
            for (int extra = 1; extra < nUVs; extra++)
            {
                var sc = new[] { ReadF32(d, ref p), ReadF32(d, ref p), ReadF32(d, ref p), ReadF32(d, ref p) };
                var raw = ReadArrayWithCount(d, ref p, 4, hasDefaultFill: true, out _);
                if (extra == 1) { m.UvScale2 = sc; m.UvRaw2 = raw; } // keep UV set 1 for decal-mapping diagnostics
                T($"[VT]   read extra UV set {extra} -> {p}");
            }

            T($"[VT] NoOfPoints @ {p}");
            int noOfPoints = ReadI32(d, ref p);
            T($"[VT]   NoOfPoints={noOfPoints}, points @ {p}");
            byte[] ptBytes = ReadArrayData(d, ref p, noOfPoints, 12, hasDefaultFill: false); // LodPoints (plain)
            var pts = new float[noOfPoints][];
            for (int i = 0; i < noOfPoints; i++)
                pts[i] = new[]
                {
                    BitConverter.ToSingle(ptBytes, i * 12),
                    BitConverter.ToSingle(ptBytes, i * 12 + 4),
                    BitConverter.ToSingle(ptBytes, i * 12 + 8),
                };
            m.Points = pts;

            T($"[VT] nNormals @ {p}");
            int nNormals = ReadI32(d, ref p);
            T($"[VT]   nNormals={nNormals}, normals @ {p}");
            m.NormalRaw = ReadArrayData(d, ref p, nNormals, 4, hasDefaultFill: true); // LodNormals (10-bit packed)
            m.VertexTableEndOffset = p;
            T($"[VT] after normals @ {p} (vtEnd={m.VertexTableStartOffset - 4 + m.SizeOfVertexTable})");
        }
        catch (Exception ex)
        {
            m.VertexTableError = ex.Message;
        }
    }

    /// <summary>Reads a length-prefixed array (Count u32 first). See <see cref="ReadArrayData"/>.</summary>
    private static byte[] ReadArrayWithCount(byte[] d, ref int p, int elemSize, bool hasDefaultFill, out int count)
    {
        count = ReadI32(d, ref p);
        return ReadArrayData(d, ref p, count, elemSize, hasDefaultFill);
    }

    /// <summary>Reads an array of <paramref name="count"/> elements whose count was already read.
    /// CompressedFill arrays (<paramref name="hasDefaultFill"/>) start with a DefaultFill byte:
    /// 1 => one inline value fills all; 0 => a real array. A real array >= 1024 bytes is
    /// [packingFlag=0x02][LZO1x block]; smaller arrays are stored raw.</summary>
    private static byte[] ReadArrayData(byte[] d, ref int p, int count, int elemSize, bool hasDefaultFill)
    {
        if (count is < 0 or > 20_000_000) throw new InvalidDataException($"Implausible array count {count} at {p}.");
        long expected = (long)count * elemSize;
        var outp = new byte[expected];

        if (hasDefaultFill)
        {
            byte fill = d[p++];
            if (fill != 0)
            {
                for (int i = 0; i < count; i++) Array.Copy(d, p, outp, i * elemSize, elemSize);
                p += elemSize;
                return outp;
            }
        }

        if (expected < 1024)
        {
            Array.Copy(d, p, outp, 0, (int)expected);
            p += (int)expected;
            return outp;
        }

        // Arrays >= 1024 bytes carry a packing-flag byte: 0x02 = LZO1x-compressed; 0x00 = stored raw
        // (uncompressed). Some models (e.g. ODOL v75 CTR Gravis) store the large vertex arrays raw,
        // so the flag is 0x00 and the `expected` bytes follow verbatim.
        byte flag = d[p++];
        if (flag == 0x00)
        {
            Array.Copy(d, p, outp, 0, (int)expected);
            p += (int)expected;
            return outp;
        }
        if (flag != 0x02) throw new NotSupportedException($"Unknown array packing flag 0x{flag:X2} at {p - 1} (expected 0x00 raw or 0x02 LZO).");
        var dec = Lzo1x.Decompress(d, p, (int)expected, out int consumed);
        p += consumed;
        return dec;
    }

    /// <summary>Skips a length-prefixed array subject to the ODOL compression rule. When count &gt; 0
    /// a 1-byte compression flag precedes the data: 0 = raw (expected bytes follow); nonzero = LZO1x
    /// packed (larger named-selection arrays on vehicles/characters hit this). We don't need the
    /// content - only to advance past it to reach the VertexTable - so a compressed array is decoded
    /// just to learn its consumed input length (LZO1x is self-terminating), and the output discarded.</summary>
    private static void SkipRuleArray(byte[] d, ref int p, int count, int elemSize)
    {
        if (count <= 0) return;
        long expected = (long)count * elemSize;
        byte flag = d[p++];                                  // compression flag (0 = raw, else LZO1x)
        if (flag == 0) { Skip(ref p, (int)expected); return; }
        Lzo1x.Decompress(d, p, (int)expected, out int consumed);
        p += consumed;
    }

    /// <summary>Reads a named-selection rule array, returning its <paramref name="count"/>*
    /// <paramref name="elemSize"/> decoded bytes. Framing/consumption is identical to
    /// <see cref="SkipRuleArray"/> (1-byte compression flag when count &gt; 0: 0 = raw, else LZO1x),
    /// so callers can read one array and skip the rest with alignment preserved.</summary>
    private static byte[] ReadRuleArray(byte[] d, ref int p, int count, int elemSize)
    {
        if (count <= 0) return Array.Empty<byte>();
        long expected = (long)count * elemSize;
        var outp = new byte[expected];
        byte flag = d[p++];                                  // compression flag (0 = raw, else LZO1x)
        if (flag == 0) { Array.Copy(d, p, outp, 0, (int)expected); p += (int)expected; return outp; }
        var dec = Lzo1x.Decompress(d, p, (int)expected, out int consumed);
        p += consumed;
        return dec;
    }

    /// <summary>One LodMaterial per ODOL_FORMAT_SPEC.md (ArmA Type 9 layout).</summary>
    private static LodMaterialInfo ReadMaterial(byte[] d, ref int p, int version)
    {
        int matStart = p;
        var mat = new LodMaterialInfo { RvMatName = ReadAsciiZ(d, ref p) };
        T($"  material @ {matStart}: RvMatName='{mat.RvMatName}' (Type at {p})");
        mat.Type = (int)ReadU32(d, ref p);
        Skip(ref p, 6 * 16);                 // Emissive/Ambient/Diffuse/forcedDiffuse/Specular/Specular2 (D3DCOLORVALUE = 4 floats)
        ReadF32(d, ref p);                   // SpecularPower
        mat.PixelShaderId = (int)ReadU32(d, ref p);
        mat.VertexShaderId = (int)ReadU32(d, ref p);
        ReadU32(d, ref p);                   // mainLight (LongBool)
        ReadU32(d, ref p);                   // ul_FogMode
        mat.BiSurfaceName = ReadAsciiZ(d, ref p);
        ReadU32(d, ref p);                   // Arma1Mostly1 (LongBool)
        ReadU32(d, ref p);                   // RenderFlags
        int nStageTex = (int)ReadU32(d, ref p);
        int nStageTransform = (int)ReadU32(d, ref p);
        T($"    Type={mat.Type} ps={mat.PixelShaderId} vs={mat.VertexShaderId} surf='{mat.BiSurfaceName}' nStageTex={nStageTex} nStageTransform={nStageTransform} (stages start at {p})");
        if (nStageTex is < 0 or > 64 || nStageTransform is < 0 or > 64)
            throw new InvalidDataException($"Implausible material stage counts tex={nStageTex} xf={nStageTransform} at {p} (material layout mismatch for v{version}).");
        for (int i = 0; i < nStageTex; i++)
        {
            uint filter = ReadU32(d, ref p);     // TextureFilter
            string tex = ReadAsciiZ(d, ref p);
            uint xfIdx = ReadU32(d, ref p);      // TransformIndex
            Skip(ref p, 1);                      // trailing byte (empirically present per stage on v73)
            T($"      stageTex[{i}] filter={filter} '{tex}' xfIdx={xfIdx} (now at {p})");
            if (tex.Length > 0) mat.StageTextures.Add(tex);
        }
        for (int i = 0; i < nStageTransform; i++)
        {
            uint uvSrc = ReadU32(d, ref p);      // UVSource
            Skip(ref p, 4 * 3 * 4);              // Transform[4][3] floats
            T($"      stageXf[{i}] uvSrc={uvSrc} (now at {p})");
        }
        // Trailing dummy StageTexture (same shape as a stage, incl. the +1 byte). Its PaaTexture is
        // usually empty - which made a fixed 10-byte skip appear correct on Base_Gravis_Pack (4+1+4+1)
        // - but some materials (e.g. vehicle thermal/TI: "...default_vehicle_ti_ca.paa") put a real
        // path here, so it MUST be parsed as an asciiz, not skipped as a fixed 10 bytes.
        uint dummyFilter = ReadU32(d, ref p);    // TextureFilter
        string dummyTex = ReadAsciiZ(d, ref p);  // PaaTexture (empty, or e.g. a TI texture path)
        uint dummyXf = ReadU32(d, ref p);        // TransformIndex
        Skip(ref p, 1);                          // trailing byte (as on the regular stages)
        T($"    trailing dummy stage: filter={dummyFilter} '{dummyTex}' xf={dummyXf}; material end at {p}");
        return mat;
    }

    public sealed class LodMeshInfo
    {
        public float[] MinPos = new float[3];
        public float[] MaxPos = new float[3];
        public float[] AutoCenter = new float[3];
        public float Sphere;
        public List<string> Textures { get; } = new();
        public List<LodMaterialInfo> Materials { get; } = new();
        public int MaterialsEndOffset;
        public int FaceCount;
        public int FacesAllocationSize;
        public int FaceIndexWidth;
        public int TriCount;
        public int QuadCount;
        public List<OdolFace> Faces { get; } = new();
        public List<SectionInfo> Sections { get; } = new();
        public List<string> NamedSelections { get; } = new();
        /// <summary>Parallel to <see cref="NamedSelections"/>: the face ordinals (into <see cref="Faces"/>)
        /// belonging to each named selection - the retexture-to-3D mapping key. Empty for "sectional"
        /// selections, whose membership is given by <see cref="NamedSelectionSectionIndexes"/> instead.</summary>
        public List<int[]> NamedSelectionFaceIndexes { get; } = new();
        /// <summary>Parallel to <see cref="NamedSelections"/>: the LodSection indices (into
        /// <see cref="Sections"/>) a sectional selection covers - the membership source when
        /// <see cref="NamedSelectionFaceIndexes"/> is empty.</summary>
        public List<int[]> NamedSelectionSectionIndexes { get; } = new();
        public List<(string Prop, string Value)> NamedProperties { get; } = new();
        public int FacesStartOffset;
        public int FacesEndOffset;
        public int SectionsEndOffset;
        public int SelectionsEndOffset;
        public int TokensEndOffset;
        public int SizeOfVertexTable;
        public int VertexTableStartOffset;
        public int VertexTableEndOffset;
        public float[]? UvScale;
        public byte[]? UvRaw;      // count*4 bytes (2x uint16 per vertex, dequantize via UvScale)
        public float[]? UvScale2;  // UV set 1 (when nUVs>1)
        public byte[]? UvRaw2;
        public byte[]? NormalRaw;  // count*4 bytes (CompressedXYZTriplet, 10-bit packed, scale -1/511)
        public float[][]? Points;  // decoded XYZ vertex positions
        public string? VertexTableError;
    }

    public sealed class SectionInfo
    {
        public int FaceMemFrom;    // cumulative memory offset into the face block (8/tri, 10/quad)
        public int FaceMemTo;
        public int CommonTextureIndex;
        public int MaterialIndex;
    }

    public sealed class LodMaterialInfo
    {
        public string RvMatName = "";
        public int Type;
        public int PixelShaderId;
        public int VertexShaderId;
        public string BiSurfaceName = "";
        public List<string> StageTextures { get; } = new(); // non-empty .paa stage paths
    }

    private static void Skip(ref int p, int n) => p += n;
    private static float[] ReadVec3(byte[] d, ref int p) => new[] { ReadF32(d, ref p), ReadF32(d, ref p), ReadF32(d, ref p) };
    private static int ReadI32(byte[] d, ref int p) { int v = BitConverter.ToInt32(d, p); p += 4; return v; }
    private static uint ReadU32(byte[] d, ref int p) { uint v = BitConverter.ToUInt32(d, p); p += 4; return v; }
    private static ushort ReadU16(byte[] d, ref int p) { ushort v = BitConverter.ToUInt16(d, p); p += 2; return v; }
    private static float ReadF32(byte[] d, ref int p) { float v = BitConverter.ToSingle(d, p); p += 4; return v; }

    private static string ReadAsciiZ(byte[] d, ref int p)
    {
        int start = p;
        while (d[p] != 0) p++;
        string sVal = System.Text.Encoding.ASCII.GetString(d, start, p - start);
        p++;
        return sVal;
    }
}
