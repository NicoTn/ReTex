using ReTex.Core.Assets;
using ReTex.Core.Mods;
using ReTex.Core.P3d;
using ReTex.Core.Projects;

System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

// Quick p3d header probe: Probe --p3d <file>
if (args.Length >= 2 && args[0] == "--p3d")
{
    var hdr = OdolReader.ReadHeader(File.ReadAllBytes(args[1]));
    Console.WriteLine($"ODOL v{hdr.Version}, {hdr.LodCount} LODs");
    for (int i = 0; i < hdr.LodCount; i++)
        Console.WriteLine($"  [{i}] resolution {hdr.Resolutions[i]}");
    Console.WriteLine($"Visual LODs: {string.Join(", ", hdr.VisualLodIndices)}");

    var d2 = File.ReadAllBytes(args[1]);
    var mi = OdolReader.ReadModelInfo(d2, hdr.HeaderEndOffset, hdr.Version);
    Console.WriteLine($"\nModelInfo: skeleton='{mi.SkeletonName}', bones={mi.Bones.Count}");
    Console.WriteLine($"  bbox: [{string.Join(",", mi.BBoxMin)}] .. [{string.Join(",", mi.BBoxMax)}]");
    foreach (var (b, par) in mi.Bones.Take(4)) Console.WriteLine($"    {b} -> {(par.Length == 0 ? "(root)" : par)}");
    Console.WriteLine($"  ModelInfo ends at offset {mi.EndOffset}");
    return 0;
}

// Search for a raw int32 value anywhere in a byte range: Probe --findint <file> <from> <to> <value>
if (args.Length >= 5 && args[0] == "--findint")
{
    var fid = File.ReadAllBytes(args[1]);
    int fiFrom = int.Parse(args[2]), fiTo = int.Parse(args[3]), fiVal = int.Parse(args[4]);
    int fiHits = 0;
    for (int p = fiFrom; p + 4 <= fiTo && p + 4 <= fid.Length; p++)
    {
        if (BitConverter.ToInt32(fid, p) == fiVal) { Console.WriteLine($"MATCH at offset {p} (0x{p:X6})"); fiHits++; }
    }
    Console.WriteLine($"{fiHits} match(es) for int value {fiVal} (byte-unaligned scan)");
    return 0;
}

// Search for a run of N floats matching given approximate values anywhere in the file:
// Probe --findfloats <file> <tolerance> <v1> <v2> ...
if (args.Length >= 4 && args[0] == "--findfloats")
{
    var ffd = File.ReadAllBytes(args[1]);
    float tol = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    var targets = args.Skip(3).Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    int hits = 0;
    for (int p = 0; p + targets.Length * 4 <= ffd.Length; p += 4)
    {
        bool ok = true;
        for (int i = 0; i < targets.Length && ok; i++)
        {
            float f = BitConverter.ToSingle(ffd, p + i * 4);
            if (float.IsNaN(f) || float.IsInfinity(f) || Math.Abs(f - targets[i]) > tol) ok = false;
        }
        if (ok) { Console.WriteLine($"MATCH at offset {p} (0x{p:X6})"); hits++; }
    }
    Console.WriteLine($"{hits} match(es) for tolerance {tol}");
    return 0;
}

// Scan raw floats/ints from an offset to visually spot plausible bbox/count values:
// Probe --scanfloats <file> <offset> <count>
if (args.Length >= 4 && args[0] == "--scanfloats")
{
    var sfd = File.ReadAllBytes(args[1]);
    int sfOff = int.Parse(args[2]), sfCount = int.Parse(args[3]);
    for (int i = 0; i < sfCount; i++)
    {
        int p = sfOff + i * 4;
        float f = BitConverter.ToSingle(sfd, p);
        int iv = BitConverter.ToInt32(sfd, p);
        Console.WriteLine($"+{i * 4,3} (abs {p:X6}): int={iv,12}  float={f}");
    }
    return 0;
}

// Build render-ready texture groups from a mesh (Phase 2). Optional 4th arg = source texture to
// simulate retexturing (mapped to a fake project path). Probe --p3dgroups <file> [srcTexToRetex]
if (args.Length >= 2 && args[0] == "--p3dgroups")
{
    var d = File.ReadAllBytes(args[1]);
    var mesh = ReTex.Core.P3d.OdolLodReader.ReadAnyVisualLod(d);
    if (mesh == null) { Console.WriteLine("could not decode a visual LOD."); return 0; }
    List<ReTex.Core.Projects.RetexSelection>? sels = null;
    if (args.Length >= 3)
        sels = new() { new ReTex.Core.Projects.RetexSelection { Name = "test", SourceTexture = args[2], ProjectTexture = "textures\\new_co.paa" } };
    var groups = ReTex.Core.P3d.OdolMeshPreview.BuildGroups(mesh, sels, @"C:\proj\addons\main");
    Console.WriteLine($"{groups.Count} texture group(s) from {mesh.Faces.Count} faces:");
    foreach (var g in groups)
        Console.WriteLine($"  {g.FaceIndices.Count} faces -> src='{g.Texture.SourceVirtualPath}' retex={g.Texture.IsRetextured} proj='{g.Texture.ProjectFilePath}' missing={g.Texture.Missing}");
    Console.WriteLine($"total grouped faces: {groups.Sum(g => g.FaceIndices.Count)} (expect {mesh.Faces.Count})");
    return 0;
}

// Fully-automatic: locate + decode the highest-detail visual LOD into a render-ready mesh.
// Probe --p3dmesh <file>
if (args.Length >= 2 && args[0] == "--p3dmesh")
{
    var d = File.ReadAllBytes(args[1]);
    var mesh = ReTex.Core.P3d.OdolLodReader.ReadAnyVisualLod(d);
    if (mesh == null) { Console.WriteLine("could not decode a visual LOD."); return 0; }
    Console.WriteLine($"points={mesh.Points.Length}, normals={mesh.Normals.Length}, uv={mesh.Uv.Length}, uv2={mesh.Uv2.Length}, faces={mesh.Faces.Count}, sections={mesh.Sections.Count}");
    if (mesh.Uv2.Length == mesh.Uv.Length && mesh.Uv.Length > 0)
    {
        int diff = 0; for (int i = 0; i < mesh.Uv.Length; i++) if (Math.Abs(mesh.Uv[i][0]-mesh.Uv2[i][0])>1e-4 || Math.Abs(mesh.Uv[i][1]-mesh.Uv2[i][1])>1e-4) diff++;
        Console.WriteLine($"  uv vs uv2: {diff}/{mesh.Uv.Length} vertices differ");
        for (int i = 0; i < mesh.Uv.Length && diff>0; i++) if (Math.Abs(mesh.Uv[i][0]-mesh.Uv2[i][0])>1e-4 || Math.Abs(mesh.Uv[i][1]-mesh.Uv2[i][1])>1e-4) { Console.WriteLine($"    v[{i}] uv=({mesh.Uv[i][0]:f3},{mesh.Uv[i][1]:f3}) uv2=({mesh.Uv2[i][0]:f3},{mesh.Uv2[i][1]:f3})"); if(--diff<=0||i>20000)break; }
    }
    Console.WriteLine($"textures: {string.Join(" | ", mesh.Textures)}");
    Console.WriteLine($"materials: {string.Join(" | ", mesh.Materials)}");
    foreach (var s in mesh.Sections)
        Console.WriteLine($"  section faces[{s.FaceIndexFrom}..{s.FaceIndexTo}] tex={s.CommonTextureIndex} mat={s.MaterialIndex}");
    for (int i = 0; i < Math.Min(3, mesh.Points.Length); i++)
        Console.WriteLine($"  pt[{i}]=({string.Join(",", mesh.Points[i])}) uv=({(mesh.Uv.Length > i ? string.Join(",", mesh.Uv[i]) : "-")}) n=({(mesh.Normals.Length > i ? string.Join(",", mesh.Normals[i]) : "-")})");
    return 0;
}

// Resolve a mod's assets and print ClassName/Model/first-texture for matching classes:
// Probe --assetinfo <modFolder> <classSubstring>
if (args.Length >= 3 && args[0] == "--assetinfo")
{
    var m = new ReTex.Core.Mods.ArmaMod { Name = Path.GetFileName(args[1]), Path = args[1], DisplayName = Path.GetFileName(args[1]) };
    var ad = Path.Combine(args[1], "addons");
    if (Directory.Exists(ad)) m.PboPaths.AddRange(Directory.GetFiles(ad, "*.pbo"));
    var all = ReTex.Core.Assets.AssetService.LoadForMod(m);
    foreach (var a in all.Where(x => x.ClassName.Contains(args[2], StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"  {a.ClassName}  [{a.Category}]  model='{a.Model}'  tex0='{(a.HiddenSelectionsTextures.Count > 0 ? a.HiddenSelectionsTextures[0] : "-")}'");
    return 0;
}

// Dump named selections + which faces/textures they cover, and simulate a name-based retexture.
// Probe --p3dsel <pboOrP3d> [virtualPath] [selNameToRetex]
if (args.Length >= 2 && args[0] == "--p3dsel")
{
    byte[] d;
    if (args[1].EndsWith(".pbo", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 3) { Console.WriteLine("need a virtual path for a pbo"); return 1; }
        var ex = ReTex.Core.Pbo.VirtualFileService.Extract(new[] { args[1] }, args[2]);
        if (ex == null) { Console.WriteLine($"could not extract {args[2]} from pbo"); return 1; }
        d = ex;
    }
    else d = File.ReadAllBytes(args[1]);

    var mesh = ReTex.Core.P3d.OdolLodReader.ReadAnyVisualLod(d);
    if (mesh == null) { Console.WriteLine("could not decode a visual LOD."); return 0; }
    Console.WriteLine($"faces={mesh.Faces.Count}, textures=[{string.Join(" | ", mesh.Textures)}]");
    Console.WriteLine($"named selections ({mesh.NamedSelections.Count}):");
    foreach (var ns in mesh.NamedSelections)
    {
        int cnt = ns.FaceMembership.Count(b => b != 0);
        // which texture indices do this selection's faces use?
        var texs = new SortedSet<int>();
        bool haveFt = mesh.FaceTextureIndex.Count == mesh.Faces.Count;
        for (int fi = 0; fi < ns.FaceMembership.Length; fi++)
            if (ns.FaceMembership[fi] != 0 && haveFt) texs.Add(mesh.FaceTextureIndex[fi]);
        var texNames = texs.Select(t => t >= 0 && t < mesh.Textures.Count ? mesh.Textures[t] : "(none)");
        // Centroid of this selection's member faces (world space) - reveals which side each side-named
        // selection sits on (e.g. is "leftleg" at +X or -X), for detecting a mirror in the preview.
        double cx = 0, cy = 0, cz = 0; int vc = 0;
        for (int fi = 0; fi < ns.FaceMembership.Length && fi < mesh.Faces.Count; fi++)
            if (ns.FaceMembership[fi] != 0)
                foreach (var vi in mesh.Faces[fi].VertexTableIndex)
                    if (vi >= 0 && vi < mesh.Points.Length) { cx += mesh.Points[vi][0]; cy += mesh.Points[vi][1]; cz += mesh.Points[vi][2]; vc++; }
        string cen = vc > 0 ? $"centroid=({cx / vc:f2},{cy / vc:f2},{cz / vc:f2})" : "";
        Console.WriteLine($"  '{ns.Name}': {cnt} faces {cen} -> textures: {string.Join(", ", texNames)}");
    }
    string? selName = args.Length >= 4 ? args[3] : (args.Length >= 3 && !args[1].EndsWith(".pbo", StringComparison.OrdinalIgnoreCase) ? args[2] : null);
    if (selName != null)
    {
        var sels = new List<ReTex.Core.Projects.RetexSelection> {
            new() { Name = selName, SourceTexture = "", ProjectTexture = "textures\\NEW_co.paa" } };
        var groups = ReTex.Core.P3d.OdolMeshPreview.BuildGroups(mesh, sels, @"C:\proj\addons\main");
        Console.WriteLine($"\nSimulated retexture of selection '{selName}':");
        foreach (var g in groups)
            Console.WriteLine($"  {g.FaceIndices.Count} faces retex={g.Texture.IsRetextured} proj='{g.Texture.ProjectFilePath}' src='{g.Texture.SourceVirtualPath}'");
    }
    return 0;
}

// Enumerate every LOD anchor FindLodMinPos finds across the whole file, decoding each and reporting
// its offset, point/face counts, UVScale (degenerate vs real), and first texture: Probe --p3dlods <file>
if (args.Length >= 2 && args[0] == "--p3dlods")
{
    var d = File.ReadAllBytes(args[1]);
    var hdr = ReTex.Core.P3d.OdolReader.ReadHeader(d);
    var mi = ReTex.Core.P3d.OdolReader.ReadModelInfo(d, hdr.HeaderEndOffset, hdr.Version);
    Console.WriteLine($"ODOL v{hdr.Version}, {hdr.LodCount} LODs, resolutions=[{string.Join(", ", hdr.Resolutions.Select(r => r.ToString("g4")))}]");
    int from = mi.EndOffset, idx = 0;
    for (int guard = 0; guard < 64; guard++)
    {
        int mp = ReTex.Core.P3d.OdolLodReader.FindLodMinPos(d, from, d.Length, mi.BBoxMin, mi.BBoxMax);
        if (mp < 0) break;
        try
        {
            var m = ReTex.Core.P3d.OdolLodReader.ReadFromMinPos(d, mp, hdr.Version);
            int lodEnd = m.VertexTableStartOffset > 0 ? m.VertexTableStartOffset - 4 + m.SizeOfVertexTable : mp;
            string uvs = m.UvScale is { Length: 4 } u ? $"[{u[0]:g3},{u[1]:g3},{u[2]:g3},{u[3]:g3}]" : "(none)";
            float span = m.UvScale is { Length: 4 } uu ? Math.Max(Math.Abs(uu[2]), Math.Abs(uu[3])) : 0;
            Console.WriteLine($"  anchor[{idx}] @{mp}: pts={m.Points?.Length ?? -1} faces={m.FaceCount} mats={m.Materials.Count} tex={m.Textures.Count} UVScale={uvs} {(span > 0.01f ? "VALID-UV" : "degenerate")} err={m.VertexTableError ?? "none"}  firstTex={(m.Textures.Count > 0 ? m.Textures[0] : "-")}");
            from = Math.Max(lodEnd, mp) + 1;
        }
        catch (Exception ex) { Console.WriteLine($"  anchor[{idx}] @{mp}: THREW {ex.Message}"); from = mp + 1; }
        idx++;
    }
    Console.WriteLine($"total anchors: {idx}");
    return 0;
}

// Find "spike" faces (huge edges) that show as stray triangles, and which section/texture they're in:
// Probe --p3doutliers <file>
if (args.Length >= 2 && args[0] == "--p3doutliers")
{
    var d = File.ReadAllBytes(args[1]);
    var mesh = ReTex.Core.P3d.OdolLodReader.ReadAnyVisualLod(d);
    if (mesh == null) { Console.WriteLine("could not decode."); return 0; }
    var pts = mesh.Points;
    float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue, maxx = -float.MaxValue, maxy = -float.MaxValue, maxz = -float.MaxValue;
    foreach (var p in pts) { minx = Math.Min(minx, p[0]); maxx = Math.Max(maxx, p[0]); miny = Math.Min(miny, p[1]); maxy = Math.Max(maxy, p[1]); minz = Math.Min(minz, p[2]); maxz = Math.Max(maxz, p[2]); }
    Console.WriteLine($"point bbox: [{minx:g3},{miny:g3},{minz:g3}]..[{maxx:g3},{maxy:g3},{maxz:g3}]  ({pts.Length} pts, {mesh.Faces.Count} faces)");
    float diag = (float)Math.Sqrt((maxx-minx)*(maxx-minx)+(maxy-miny)*(maxy-miny)+(maxz-minz)*(maxz-minz));
    // Per-face longest edge; flag faces whose longest edge is a big fraction of the model diagonal.
    var edges = new List<float>();
    float Edge(float[] a, float[] b) => (float)Math.Sqrt((a[0]-b[0])*(a[0]-b[0])+(a[1]-b[1])*(a[1]-b[1])+(a[2]-b[2])*(a[2]-b[2]));
    int spikes = 0; int shown = 0;
    for (int fi = 0; fi < mesh.Faces.Count; fi++)
    {
        var f = mesh.Faces[fi];
        float maxEdge = 0;
        for (int i = 0; i < f.VertexTableIndex.Length; i++)
        {
            int a = f.VertexTableIndex[i], b = f.VertexTableIndex[(i + 1) % f.VertexTableIndex.Length];
            if (a < pts.Length && b < pts.Length) maxEdge = Math.Max(maxEdge, Edge(pts[a], pts[b]));
        }
        edges.Add(maxEdge);
        if (maxEdge > diag * 0.25f)
        {
            spikes++;
            int tex = fi < mesh.FaceTextureIndex.Count ? mesh.FaceTextureIndex[fi] : -99;
            if (shown++ < 15) Console.WriteLine($"  spike face {fi}: maxEdge={maxEdge:g3} (diag={diag:g3}) texIdx={tex} verts=[{string.Join(",", f.VertexTableIndex)}]");
        }
    }
    edges.Sort();
    Console.WriteLine($"median edge={edges[edges.Count/2]:g3}, spikes(>0.25*diag)={spikes}");
    return 0;
}

// Diagnose WHY a p3d fails to decode: Probe --p3ddiag <file>
if (args.Length >= 2 && args[0] == "--p3ddiag")
{
    var d = File.ReadAllBytes(args[1]);
    if (!ReTex.Core.P3d.OdolReader.IsOdol(d))
    {
        Console.WriteLine(ReTex.Core.P3d.MlodReader.IsMlod(d) ? "MLOD file" : "not ODOL/MLOD");
        return 0;
    }
    var hdr = ReTex.Core.P3d.OdolReader.ReadHeader(d);
    var mi = ReTex.Core.P3d.OdolReader.ReadModelInfo(d, hdr.HeaderEndOffset, hdr.Version);
    Console.WriteLine($"ODOL v{hdr.Version}  bbox min=({string.Join(",", mi.BBoxMin)}) max=({string.Join(",", mi.BBoxMax)})  modelInfoEnd={mi.EndOffset}");
    Console.WriteLine($"LODs={hdr.LodCount}  resolutions=[{string.Join(", ", hdr.Resolutions.Select(r => r.ToString("g4")))}]  visualIdx=[{string.Join(",", hdr.VisualLodIndices)}]");
    int minPos = ReTex.Core.P3d.OdolLodReader.FindLodMinPos(d, mi.EndOffset, Math.Min(d.Length, mi.EndOffset + 200_000), mi.BBoxMin, mi.BBoxMax);
    Console.WriteLine($"FindLodMinPos (200KB window) -> {minPos}");
    if (minPos < 0)
    {
        int wide = ReTex.Core.P3d.OdolLodReader.FindLodMinPos(d, mi.EndOffset, d.Length, mi.BBoxMin, mi.BBoxMax);
        Console.WriteLine($"FindLodMinPos (whole file) -> {wide}");
        if (wide >= 0) minPos = wide; else return 0;
    }
    try
    {
        var m = ReTex.Core.P3d.OdolLodReader.ReadFromMinPos(d, minPos, hdr.Version);
        Console.WriteLine($"ReadFromMinPos OK: faces={m.FaceCount} sections={m.Sections.Count} nUV-note check VertexTableError below");
        Console.WriteLine($"VertexTableError: {m.VertexTableError ?? "(none)"}  points={(m.Points?.Length.ToString() ?? "null")}");
    }
    catch (Exception ex) { Console.WriteLine($"ReadFromMinPos THREW: {ex.GetType().Name}: {ex.Message}"); }
    return 0;
}

// Forward-parse a LOD from a known MinPos offset (textures + materials), report where it
// lands (should be NoOfFaces): Probe --p3dminpos <file> <minPosOffset>
if (args.Length >= 3 && args[0] == "--p3dminpos")
{
    var d = File.ReadAllBytes(args[1]);
    var hdr = OdolReader.ReadHeader(d);
    int mp = int.Parse(args[2]);
    ReTex.Core.P3d.OdolLodReader.Trace = args.Contains("--trace");
    var m = ReTex.Core.P3d.OdolLodReader.ReadFromMinPos(d, mp, hdr.Version);
    Console.WriteLine($"MinPos [{string.Join(",", m.MinPos)}] Max [{string.Join(",", m.MaxPos)}] sphere {m.Sphere}");
    Console.WriteLine($"textures ({m.Textures.Count}): {string.Join(" | ", m.Textures)}");
    foreach (var mat in m.Materials)
        Console.WriteLine($"  material '{mat.RvMatName}' type={mat.Type} ps={mat.PixelShaderId} vs={mat.VertexShaderId} surf='{mat.BiSurfaceName}' stageTex=[{string.Join(", ", mat.StageTextures)}]");
    Console.WriteLine($"materials end at offset {m.MaterialsEndOffset} (0x{m.MaterialsEndOffset:X})");
    Console.WriteLine($"faces: {m.FaceCount} (tri={m.TriCount} quad={m.QuadCount}), allocSize={m.FacesAllocationSize} (expect 8*tri+10*quad={8 * m.TriCount + 10 * m.QuadCount})");
    Console.WriteLine($"  faces span {m.FacesStartOffset}..{m.FacesEndOffset} ({m.FacesEndOffset - m.FacesStartOffset} bytes; expect 7*tri+9*quad={7 * m.TriCount + 9 * m.QuadCount})");
    Console.WriteLine($"sections: {m.Sections.Count}");
    foreach (var sec in m.Sections)
        Console.WriteLine($"  section faceMem[{sec.FaceMemFrom}..{sec.FaceMemTo}] texIndex={sec.CommonTextureIndex} matIndex={sec.MaterialIndex}");
    Console.WriteLine($"sections end at {m.SectionsEndOffset} (0x{m.SectionsEndOffset:X})");
    Console.WriteLine($"named selections ({m.NamedSelections.Count}): {string.Join(", ", m.NamedSelections)}");
    Console.WriteLine($"  selections end at {m.SelectionsEndOffset} (0x{m.SelectionsEndOffset:X})");
    Console.WriteLine($"named properties ({m.NamedProperties.Count}): {string.Join(", ", m.NamedProperties.Select(t => $"{t.Prop}={t.Value}"))}");
    Console.WriteLine($"  tokens end at {m.TokensEndOffset} (0x{m.TokensEndOffset:X})");
    int maxVi = m.Faces.SelectMany(f => f.VertexTableIndex).DefaultIfEmpty(0).Max();
    Console.WriteLine($"max vertex-table index referenced by faces: {maxVi} (=> expect NoOfPoints {maxVi + 1})");
    Console.WriteLine($"sizeOfVertexTable={m.SizeOfVertexTable}, VertexTable starts at {m.VertexTableStartOffset} (0x{m.VertexTableStartOffset:X})");
    if (m.VertexTableError != null)
        Console.WriteLine($"  VertexTable decode ERROR: {m.VertexTableError}");
    else
    {
        Console.WriteLine($"  decoded {m.Points?.Length ?? 0} points, uvScale=[{string.Join(",", m.UvScale ?? new float[0])}], VertexTable ends at {m.VertexTableEndOffset} (expected ~{m.VertexTableStartOffset - 4 + m.SizeOfVertexTable})");
        for (int i = 0; i < Math.Min(4, m.Points?.Length ?? 0); i++)
            Console.WriteLine($"    pt[{i}] ({string.Join(", ", m.Points![i])})");
    }
    return 0;
}

// Try LZO-decompressing an array at an offset and interpret the output as float triplets:
// Probe --lzotest <file> <offset> <count> <elemBytes>
if (args.Length >= 5 && args[0] == "--lzotest")
{
    var d = File.ReadAllBytes(args[1]);
    int off = int.Parse(args[2]), count = int.Parse(args[3]), elem = int.Parse(args[4]);
    int outLen = count * elem;
    var outBuf = ReTex.Core.P3d.LzoUtil.DecompressAll(d, off, Math.Max(outLen, 200000), out int consumed);
    Console.WriteLine($"natural output={outBuf.Length} bytes, consumed={consumed} input bytes (next at {off + consumed}); asked {outLen}");
    int show = Math.Min(outBuf.Length / Math.Max(elem, 1), 6);
    for (int i = 0; i < show; i++)
    {
        if (elem == 12)
            Console.WriteLine($"  [{i}] ({BitConverter.ToSingle(outBuf, i * 12)}, {BitConverter.ToSingle(outBuf, i * 12 + 4)}, {BitConverter.ToSingle(outBuf, i * 12 + 8)})");
        else if (elem == 4)
            Console.WriteLine($"  [{i}] u32=0x{BitConverter.ToUInt32(outBuf, i * 4):X8} f={BitConverter.ToSingle(outBuf, i * 4)}");
    }
    return 0;
}

// Diagnostic: LZO1X decode to natural end marker, report output size + consumed + first floats:
// Probe --lzonat <file> <offset>
if (args.Length >= 3 && args[0] == "--lzonat")
{
    var d = File.ReadAllBytes(args[1]);
    int off = int.Parse(args[2]);
    bool ok = ReTex.Core.P3d.Lzo1x.TryDecodeNatural(d, off, 400000, out int outSize, out int consumed, out var buf);
    if (ok)
        Console.WriteLine($"natural block: outSize={outSize}, consumed={consumed} (next at {off + consumed}); /12={outSize / 12.0:F2} /4={outSize / 4.0:F2}");
    else
        Console.WriteLine("ran off buffer before an end marker.");
    Console.WriteLine($"  first 16 output bytes: {string.Join(" ", buf.Take(16).Select(b => b.ToString("X2")))}");
    return 0;
}

// Faithful LZO1X decode (returns consumed bytes) of an array at an offset:
// Probe --lzo1x <file> <offset> <count> <elemBytes>
if (args.Length >= 5 && args[0] == "--lzo1x")
{
    var d = File.ReadAllBytes(args[1]);
    int off = int.Parse(args[2]), count = int.Parse(args[3]), elem = int.Parse(args[4]);
    int outLen = count * elem;
    try
    {
        var outBuf = ReTex.Core.P3d.Lzo1x.Decompress(d, off, outLen, out int consumed);
        Console.WriteLine($"OK: filled {outLen} bytes, consumed {consumed} input bytes (next at {off + consumed}).");
        int show = Math.Min(count, 6);
        for (int i = 0; i < show; i++)
        {
            if (elem == 12)
                Console.WriteLine($"  [{i}] ({BitConverter.ToSingle(outBuf, i * 12)}, {BitConverter.ToSingle(outBuf, i * 12 + 4)}, {BitConverter.ToSingle(outBuf, i * 12 + 8)})");
            else if (elem == 4)
                Console.WriteLine($"  [{i}] u32=0x{BitConverter.ToUInt32(outBuf, i * 4):X8}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"FAILED: {ex.Message}"); }
    return 0;
}

// Scan for a run of N consecutive ascending uint32 values that all look like plausible
// file offsets (in [minVal, fileSize]) - used to locate StartAddressOfLods/EndAddressOfLods.
// Probe --scanoffsets <file> <from> <to> <runLen> [minVal]
if (args.Length >= 5 && args[0] == "--scanoffsets")
{
    var od = File.ReadAllBytes(args[1]);
    int oFrom = int.Parse(args[2]), oTo = Math.Min(int.Parse(args[3]), od.Length);
    int runLen = int.Parse(args[4]);
    long minVal = args.Length >= 6 ? long.Parse(args[5]) : 1;
    long fsize = od.Length;
    int oHits = 0;
    for (int p = oFrom; p + runLen * 4 <= oTo; p++)
    {
        bool ok = true;
        uint prev = 0;
        for (int i = 0; i < runLen && ok; i++)
        {
            uint v = BitConverter.ToUInt32(od, p + i * 4);
            if (v < minVal || v > fsize || (i > 0 && v < prev)) ok = false;
            prev = v;
        }
        if (ok)
        {
            var vals = Enumerable.Range(0, runLen).Select(i => BitConverter.ToUInt32(od, p + i * 4));
            Console.WriteLine($"RUN at {p} (0x{p:X6}): {string.Join(", ", vals)}");
            oHits++;
        }
    }
    Console.WriteLine($"{oHits} ascending-offset run(s) of length {runLen} in [{minVal},{fsize}]");
    return 0;
}

// Raw hex dump: Probe --hexdump <file> <offset> <len>
if (args.Length >= 4 && args[0] == "--hexdump")
{
    var hd = File.ReadAllBytes(args[1]);
    int off = int.Parse(args[2]), len = int.Parse(args[3]);
    for (int i = 0; i < len; i += 16)
    {
        var chunk = hd.Skip(off + i).Take(Math.Min(16, len - i)).ToArray();
        var hex = string.Join(" ", chunk.Select(b => b.ToString("X2")));
        var ascii = new string(chunk.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
        Console.WriteLine($"{off + i:X6}: {hex,-48} {ascii}");
    }
    return 0;
}

// Probe LOD skeleton counts (proxies/items/bonelinks/textures/materials/faces) without
// decoding vertex data yet: Probe --p3dlod <file> [lodIndex]
if (args.Length >= 2 && args[0] == "--p3dlod")
{
    var d = File.ReadAllBytes(args[1]);
    var hdr = OdolReader.ReadHeader(d);
    var mi = OdolReader.ReadModelInfo(d, hdr.HeaderEndOffset, hdr.Version);
    Console.WriteLine($"ODOL v{hdr.Version}, {hdr.LodCount} LODs, visual: {string.Join(",", hdr.VisualLodIndices)}");
    Console.WriteLine($"ModelInfo ends at {mi.EndOffset}, bbox [{string.Join(",", mi.BBoxMin)}] .. [{string.Join(",", mi.BBoxMax)}]");

    int want = args.Length >= 3 ? int.Parse(args[2]) : -1;
    int pos = mi.EndOffset;
    for (int i = 0; i < hdr.LodCount; i++)
    {
        if (want >= 0 && i != want) { Console.WriteLine($"[{i}] resolution {hdr.Resolutions[i]} - skipped (use lodIndex to target)"); continue; }
        try
        {
            var sk = ReTex.Core.P3d.OdolLodReader.ReadSkeleton(d, pos, hdr.Version);
            Console.WriteLine($"[{i}] resolution {hdr.Resolutions[i]}:");
            Console.WriteLine($"  bbox [{string.Join(",", sk.MinPos)}] .. [{string.Join(",", sk.MaxPos)}], sphere {sk.Sphere}");
            Console.WriteLine($"  textures ({sk.Textures.Count}): {string.Join(" | ", sk.Textures)}");
            Console.WriteLine($"  materials ({sk.Materials.Count}): {string.Join(" | ", sk.Materials)}");
            Console.WriteLine($"  faces: {sk.FaceCount}");
            Console.WriteLine($"  skeleton ends at offset {sk.EndOffset}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{i}] resolution {hdr.Resolutions[i]}: FAILED - {ex.Message}");
        }
        break; // offset-chaining to the next LOD isn't implemented yet - only LOD 0 (or --lodIndex) is reachable
    }
    return 0;
}

// Find which mod/PBO (across the whole workshop root) defines a class with an exact name
// anywhere in its config tree: Probe --findclass <workshopRoot> <className>
if (args.Length >= 3 && args[0] == "--findclass")
{
    var fcRoot = args[1];
    var fcWant = args[2];
    int fcScanned = 0;
    foreach (var modDir in Directory.EnumerateDirectories(fcRoot))
    {
        var ad = Path.Combine(modDir, "addons");
        if (!Directory.Exists(ad)) continue;
        foreach (var pbo in Directory.EnumerateFiles(ad, "*.pbo"))
        {
            fcScanned++;
            try
            {
                using var arc = new ReTex.Core.Pbo.PboArchive(pbo);
                var cfg = arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.bin", StringComparison.OrdinalIgnoreCase))
                       ?? arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.cpp", StringComparison.OrdinalIgnoreCase));
                if (cfg is null) continue;
                var bytes = arc.Extract(cfg);
                var root = ReTex.Core.Rap.RapReader.IsRapified(bytes)
                    ? ReTex.Core.Rap.RapReader.Parse(bytes)
                    : ReTex.Core.Rap.CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                bool Walk(ReTex.Core.Rap.RapClass c, string path)
                {
                    foreach (var ch in c.Classes)
                    {
                        var here = path.Length == 0 ? ch.Name : path + "/" + ch.Name;
                        if (ch.Name.Equals(fcWant, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"FOUND in {Path.GetFileName(modDir)} -> {Path.GetFileName(pbo)}: {here} : {ch.Parent} ({ch.Properties.Count} props, {ch.Classes.Count} subclasses)");
                            return true;
                        }
                        if (Walk(ch, here)) return true;
                    }
                    return false;
                }
                Walk(root, "");
            }
            catch { /* skip unreadable */ }
        }
    }
    Console.WriteLine($"Scanned {fcScanned} PBOs.");
    return 0;
}

// Find classes anywhere in a mod folder's PBOs that inherit (directly) from a given base class:
// Probe --findderived <modFolder> <baseClassName>
if (args.Length >= 3 && args[0] == "--findderived")
{
    var fdRoot = Path.Combine(args[1], "addons");
    var fdWant = args[2];
    foreach (var pbo in Directory.EnumerateFiles(fdRoot, "*.pbo"))
    {
        try
        {
            using var arc = new ReTex.Core.Pbo.PboArchive(pbo);
            var cfg = arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.bin", StringComparison.OrdinalIgnoreCase))
                   ?? arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.cpp", StringComparison.OrdinalIgnoreCase));
            if (cfg is null) continue;
            var bytes = arc.Extract(cfg);
            var root = ReTex.Core.Rap.RapReader.IsRapified(bytes)
                ? ReTex.Core.Rap.RapReader.Parse(bytes)
                : ReTex.Core.Rap.CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            void Walk(ReTex.Core.Rap.RapClass c, string path)
            {
                foreach (var ch in c.Classes)
                {
                    if (ch.Parent.Equals(fdWant, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"{Path.GetFileName(pbo)}: class {ch.Name}: {ch.Parent}  (in {path})");
                    Walk(ch, path.Length == 0 ? ch.Name : path + "/" + ch.Name);
                }
            }
            Walk(root, "");
        }
        catch { /* skip unreadable */ }
    }
    return 0;
}

// Find which installed mod defines a CfgPatches addon: Probe --findaddon <workshopRoot> <addonName>
if (args.Length >= 3 && args[0] == "--findaddon")
{
    var root = args[1];
    var want = args[2];
    int scanned = 0, hits = 0;
    foreach (var modDir in Directory.EnumerateDirectories(root))
    {
        var ad = Path.Combine(modDir, "addons");
        if (!Directory.Exists(ad)) continue;
        foreach (var pbo in Directory.EnumerateFiles(ad, "*.pbo"))
        {
            scanned++;
            try
            {
                using var arc = new ReTex.Core.Pbo.PboArchive(pbo);
                var cfg = arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.bin", StringComparison.OrdinalIgnoreCase))
                       ?? arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.cpp", StringComparison.OrdinalIgnoreCase));
                if (cfg is null) continue;
                var bytes = arc.Extract(cfg);
                var root2 = ReTex.Core.Rap.RapReader.IsRapified(bytes)
                    ? ReTex.Core.Rap.RapReader.Parse(bytes)
                    : ReTex.Core.Rap.CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                var patches = root2.Class("CfgPatches");
                if (patches is null) continue;
                if (patches.Classes.Any(c => c.Name.Equals(want, StringComparison.OrdinalIgnoreCase)))
                {
                    hits++;
                    Console.WriteLine($"DEFINES {want}:  {Path.GetFileName(modDir)}  ->  {Path.GetFileName(pbo)}");
                }
            }
            catch { /* skip unreadable */ }
        }
    }
    Console.WriteLine($"\nScanned {scanned} PBOs, {hits} define '{want}'.");
    return 0;
}

// Regenerate config.cpp from a project's retex.json: Probe --regen <retex.json>
if (args.Length >= 2 && args[0] == "--regen")
{
    var rp = RetexProject.Load(args[1]);
    RetexProjectService.GenerateConfig(rp);
    Console.WriteLine($"Regenerated {rp.ConfigPath}\n");
    Console.WriteLine(File.ReadAllText(rp.ConfigPath));
    return 0;
}

// Find entries whose ProjectTexture is missing on disk: Probe --missing <retex.json>
if (args.Length >= 2 && args[0] == "--missing")
{
    var rpm = RetexProject.Load(args[1]);
    var missing = RetexProjectService.FindMissingTextures(rpm);
    Console.WriteLine(missing.Count == 0 ? "OK: no missing textures" : string.Join(Environment.NewLine, missing));
    return missing.Count == 0 ? 0 : 1;
}

// Pack an existing project's CURRENT config.cpp to a loadable @Mod: Probe --pack <retex.json>
if (args.Length >= 2 && args[0] == "--pack")
{
    var rpp = RetexProject.Load(args[1]);
    var pbocPack = ReTex.Core.Tools.PboTool.FindDefault();
    if (pbocPack is null) { Console.WriteLine("pboc.exe not found."); return 1; }
    var res = await RetexProjectService.PackModAsync(rpp, new ReTex.Core.Tools.PboTool(pbocPack));
    Console.WriteLine($"exit={res.ExitCode}  output={res.OutputPath}");
    if (res.StdOut.Length > 0) Console.WriteLine("stdout: " + res.StdOut);
    if (res.StdErr.Length > 0) Console.WriteLine("stderr: " + res.StdErr);
    return res.Success ? 0 : 1;
}

// Dump a parsed config class (recursively): Probe --classdump <pbo> <className> [depth]
if (args.Length >= 3 && args[0] == "--classdump")
{
    using var arc = new ReTex.Core.Pbo.PboArchive(args[1]);
    var cfg = arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.bin", StringComparison.OrdinalIgnoreCase))
           ?? arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.cpp", StringComparison.OrdinalIgnoreCase));
    var bytes = arc.Extract(cfg!);
    var root3 = ReTex.Core.Rap.RapReader.IsRapified(bytes)
        ? ReTex.Core.Rap.RapReader.Parse(bytes)
        : ReTex.Core.Rap.CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
    int depth = args.Length > 3 && int.TryParse(args[3], out var dd) ? dd : 4;
    var want3 = args[2];
    RapClassFinder.FindAndDump(root3, want3, depth);
    return 0;
}

// Dump a rapified/text entry's root tree (e.g. an .rvmat): Probe --dumproot <pbo> <entrySubstring> [depth]
if (args.Length >= 3 && args[0] == "--dumproot")
{
    using var arcr = new ReTex.Core.Pbo.PboArchive(args[1]);
    var er = arcr.Entries.First(x => x.FileName.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var br = arcr.Extract(er);
    var rr = ReTex.Core.Rap.RapReader.IsRapified(br)
        ? ReTex.Core.Rap.RapReader.Parse(br)
        : ReTex.Core.Rap.CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(br));
    int depthR = args.Length > 3 && int.TryParse(args[3], out var ddr) ? ddr : 6;
    RapClassFinder.Dump(rr, 0, depthR);
    return 0;
}

// Dump a PBO text entry: Probe --catentry <pbo> <entrySubstring>
if (args.Length >= 3 && args[0] == "--catentry")
{
    using var arc = new ReTex.Core.Pbo.PboArchive(args[1]);
    var e = arc.Entries.First(x => x.FileName.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(arc.Extract(e)));
    return 0;
}

// Extract a raw PBO entry to a file: Probe --extract <pbo> <entrySubstring> <outFile>
if (args.Length >= 4 && args[0] == "--extract")
{
    using var arcx = new ReTex.Core.Pbo.PboArchive(args[1]);
    var ex = arcx.Entries.First(x => x.FileName.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    File.WriteAllBytes(args[3], arcx.Extract(ex));
    Console.WriteLine($"Wrote {ex.FileName} ({ex.DataSize} bytes) -> {args[3]}");
    return 0;
}

// LoadForMod per-PBO asset counts: Probe --modpbos <modFolder>
if (args.Length >= 2 && args[0] == "--modpbos")
{
    var modFolder2 = args[1];
    var m = new ReTex.Core.Mods.ArmaMod { Name = Path.GetFileName(modFolder2), Path = modFolder2, DisplayName = Path.GetFileName(modFolder2) };
    var ad = Path.Combine(modFolder2, "addons");
    if (Directory.Exists(ad)) m.PboPaths.AddRange(Directory.GetFiles(ad, "*.pbo"));
    var all = AssetService.LoadForMod(m);
    Console.WriteLine($"Total assets: {all.Count} across {all.Select(a => a.SourcePbo).Distinct().Count()} PBOs");
    foreach (var g in all.GroupBy(a => Path.GetFileName(a.SourcePbo)).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Key,-40} {g.Count()}");
    return 0;
}

// Test texture de-duplication in AddRetexture: Probe --deduptest <pbo> <srcTexA> <srcTexB-sharedWithA>
if (args.Length >= 3 && args[0] == "--deduptest")
{
    var pbo = args[1];
    var texShared = args[2];                 // used by selections 0 AND 2
    var texOther = args.Length > 3 ? args[3] : args[2];
    var tmp = Path.Combine(Path.GetTempPath(), "retex_deduptest_" + Guid.NewGuid().ToString("N")[..8]);
    var dproj = ReTex.Core.Projects.RetexProjectService.CreateProject(tmp, "DedupTest");
    var dasset = new ReTex.Core.Assets.AssetInfo
    {
        ClassName = "TestHelmet",
        Category = ReTex.Core.Assets.AssetCategory.Equipment,
        HiddenSelections = new[] { "camo", "camo2", "eyes" },
        HiddenSelectionsTextures = new[] { texShared, texOther, texShared },
    };
    var e = ReTex.Core.Projects.RetexProjectService.AddRetexture(dproj, dasset, new[] { pbo }, copyValues: false);
    Console.WriteLine("model 1:");
    foreach (var s in e.Selections)
        Console.WriteLine($"  sel[{s.Index}] {s.Name,-6} src={Path.GetFileName(s.SourceTexture.Replace('\\','/'))} -> project='{s.ProjectTexture}'");
    // Second "model" sharing the same source texture (cross-model dedup).
    var dasset2 = new ReTex.Core.Assets.AssetInfo
    {
        ClassName = "TestHelmet2",
        Category = ReTex.Core.Assets.AssetCategory.Equipment,
        HiddenSelections = new[] { "camo" },
        HiddenSelectionsTextures = new[] { texShared },
    };
    var e2 = ReTex.Core.Projects.RetexProjectService.AddRetexture(dproj, dasset2, new[] { pbo }, copyValues: false);
    Console.WriteLine($"model 2: sel[0] camo -> project='{e2.Selections[0].ProjectTexture}'");
    var files = Directory.Exists(dproj.TexturesDir) ? Directory.GetFiles(dproj.TexturesDir).Select(Path.GetFileName).ToList() : new List<string?>();
    Console.WriteLine($"files on disk ({files.Count}): {string.Join(", ", files)}");
    bool ok = e.Selections[0].ProjectTexture == e.Selections[2].ProjectTexture
              && e.Selections[0].ProjectTexture == e2.Selections[0].ProjectTexture
              && e.Selections[0].ProjectTexture.Length > 0;
    Console.WriteLine(ok ? "DEDUP OK (within-model AND cross-model share one file)" : "DEDUP FAILED");
    try { Directory.Delete(tmp, true); } catch { }
    return 0;
}

// Test texture consolidation of an existing project with duplicate files: Probe --consolidatetest
if (args.Length >= 1 && args[0] == "--consolidatetest")
{
    var tmp = Path.Combine(Path.GetTempPath(), "retex_constest_" + Guid.NewGuid().ToString("N")[..8]);
    var cp = ReTex.Core.Projects.RetexProjectService.CreateProject(tmp, "ConsTest");
    // Two identical copies of one source (should merge) + one edited-differently copy (should stay).
    File.WriteAllBytes(Path.Combine(cp.TexturesDir, "foo_co.paa"), new byte[] { 1, 2, 3, 4 });
    File.WriteAllBytes(Path.Combine(cp.TexturesDir, "foo_co_2.paa"), new byte[] { 1, 2, 3, 4 }); // identical -> merge
    File.WriteAllBytes(Path.Combine(cp.TexturesDir, "foo_co_3.paa"), new byte[] { 9, 9, 9, 9 }); // different -> keep
    ReTex.Core.Projects.RetexSelection Sel(string src, string proj) => new() { Name = "camo", SourceTexture = src, ProjectTexture = proj };
    cp.Entries.Add(new ReTex.Core.Projects.RetexEntry { NewClassName = "A", Selections = { Sel("mod\\foo_co.paa", "textures\\foo_co.paa") } });
    cp.Entries.Add(new ReTex.Core.Projects.RetexEntry { NewClassName = "B", Selections = { Sel("mod\\foo_co.paa", "textures\\foo_co_2.paa") } });
    cp.Entries.Add(new ReTex.Core.Projects.RetexEntry { NewClassName = "C", Selections = { Sel("mod\\foo_co.paa", "textures\\foo_co_3.paa") } });
    int removed = ReTex.Core.Projects.RetexProjectService.ConsolidateTextures(cp);
    foreach (var e in cp.Entries)
        Console.WriteLine($"  {e.NewClassName} -> {e.Selections[0].ProjectTexture}");
    var left = Directory.GetFiles(cp.TexturesDir).Select(Path.GetFileName).OrderBy(x => x).ToList();
    Console.WriteLine($"removed={removed}; files left: {string.Join(", ", left)}");
    bool ok = cp.Entries[0].Selections[0].ProjectTexture == "textures\\foo_co.paa"
              && cp.Entries[1].Selections[0].ProjectTexture == "textures\\foo_co.paa"   // merged
              && cp.Entries[2].Selections[0].ProjectTexture == "textures\\foo_co_3.paa" // kept (different)
              && removed == 1 && !left.Contains("foo_co_2.paa") && left.Contains("foo_co_3.paa");
    Console.WriteLine(ok ? "CONSOLIDATE OK" : "CONSOLIDATE FAILED");
    try { Directory.Delete(tmp, true); } catch { }
    return 0;
}

// Headless textured render of a p3d's visual LOD: Probe --p3drender <p3d> <paa> <out.png> [axis=z] [flipv]
if (args.Length >= 4 && args[0] == "--p3drender")
{
    var meshBytes = File.ReadAllBytes(args[1]);
    var mesh = OdolLodReader.ReadAnyVisualLod(meshBytes);
    if (mesh is null) { Console.WriteLine("mesh decode returned null"); return 1; }
    var tex = ReTex.Core.Paa.PaaImage.LoadFile(args[2]);
    char axis = args.Length > 4 && args[4].Length > 0 ? char.ToLower(args[4][0]) : 'z';
    bool flipV = args.Any(a => a.Equals("flipv", StringComparison.OrdinalIgnoreCase));
    int W = 700, H = 700;
    var rgba = Probe.SoftRender.Render(mesh, tex, W, H, axis, flipV);
    Probe.Png.Write(args[3], W, H, rgba);
    Console.WriteLine($"Rendered {mesh.Points.Length} pts / {mesh.Faces.Count} faces, tex {tex.Width}x{tex.Height}, axis={axis}, flipV={flipV} -> {args[3]}");
    return 0;
}

// Resolved category for classes: Probe --catof <modFolder> <classNameSubstr> [more...]
if (args.Length >= 3 && args[0] == "--catof")
{
    var modFolder3 = args[1];
    var m3 = new ReTex.Core.Mods.ArmaMod { Name = Path.GetFileName(modFolder3), Path = modFolder3, DisplayName = Path.GetFileName(modFolder3) };
    var ad3 = Path.Combine(modFolder3, "addons");
    if (Directory.Exists(ad3)) m3.PboPaths.AddRange(Directory.GetFiles(ad3, "*.pbo"));
    var all3 = AssetService.LoadForMod(m3);
    foreach (var want in args.Skip(2))
        foreach (var a in all3.Where(a => a.ClassName.Contains(want, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"{a.Category,-10} {a.ClassName}   sel=[{string.Join(",", a.HiddenSelections)}]  ({Path.GetFileName(a.SourcePbo)})");
    return 0;
}

// PBO listing: Probe --pbols <pbo> [filter]
if (args.Length >= 2 && args[0] == "--pbols")
{
    using var arc = new ReTex.Core.Pbo.PboArchive(args[1]);
    Console.WriteLine($"prefix = '{arc.Prefix}'");
    var filter = args.Length > 2 ? args[2] : "";
    foreach (var e in arc.Entries.Where(e => filter.Length == 0 || e.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(40))
        Console.WriteLine($"  {e.FileName}  (mime=0x{e.PackingMethod:X8}, size={e.DataSize})");
    return 0;
}

// Per-PBO diagnostic: Probe --moddiag <modFolder>
if (args.Length >= 2 && args[0] == "--moddiag")
{
    var diagAddons = Path.Combine(args[1], "addons");
    var diagLog = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "moddiag.log");
    File.WriteAllText(diagLog, "");
    void Log(string s) { Console.WriteLine(s); File.AppendAllText(diagLog, s + Environment.NewLine); }
    int totalAssets = 0;
    foreach (var pbo in Directory.GetFiles(diagAddons, "*.pbo"))
    {
        Log($"-> {Path.GetFileName(pbo)} ...");
        string note;
        try
        {
            using var arc = new ReTex.Core.Pbo.PboArchive(pbo);
            var cfg = arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.bin", StringComparison.OrdinalIgnoreCase))
                   ?? arc.Entries.FirstOrDefault(e => e.FileName.EndsWith("config.cpp", StringComparison.OrdinalIgnoreCase));
            if (cfg is null) note = "no config";
            else
            {
                var bytes = arc.Extract(cfg);
                var rapified = ReTex.Core.Rap.RapReader.IsRapified(bytes);
                var root = rapified ? ReTex.Core.Rap.RapReader.Parse(bytes)
                                    : ReTex.Core.Rap.CppConfigParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                int classes = (root.Class("CfgVehicles")?.Classes.Count ?? 0)
                            + (root.Class("CfgWeapons")?.Classes.Count ?? 0)
                            + (root.Class("CfgGlasses")?.Classes.Count ?? 0);
                int a = AssetExtractor.Extract(root).Count;
                totalAssets += a;
                note = $"{(rapified ? "bin" : "cpp")}  classes={classes}  assets={a}";
            }
        }
        catch (Exception ex) { note = $"ERR: {ex.GetType().Name}: {ex.Message}"; }
        Log($"{Path.GetFileName(pbo),-26} {note}");
    }
    Log($"TOTAL assets (per-pbo resolution): {totalAssets}");
    return 0;
}

// Text config parse: Probe --cpp <config.cpp>
if (args.Length >= 2 && args[0] == "--cpp")
{
    var cppRoot = ReTex.Core.Rap.CppConfigParser.Parse(File.ReadAllText(args[1]));
    var cppAssets = AssetExtractor.Extract(cppRoot);
    Console.WriteLine($"Parsed text config -> {cppAssets.Count} assets:");
    foreach (var a in cppAssets)
        Console.WriteLine($"  [{a.Category}] {a.Label}  hs=[{string.Join(",", a.HiddenSelections)}]  hst=[{string.Join(",", a.HiddenSelectionsTextures)}]  model={a.Model}");
    return 0;
}

// ASCII anchor scan: Probe --p3dstrings <file> [startOffset]
if (args.Length >= 2 && args[0] == "--p3dstrings")
{
    var d = File.ReadAllBytes(args[1]);
    int start = args.Length > 2 ? int.Parse(args[2]) : 0;
    int run = 0, runStart = 0, printed = 0;
    for (int i = start; i < d.Length && printed < 60; i++)
    {
        byte b = d[i];
        bool printable = b >= 0x20 && b < 0x7F;
        if (printable) { if (run == 0) runStart = i; run++; }
        else
        {
            if (run >= 4)
            {
                Console.WriteLine($"  @{runStart,-8} (+{runStart - start,-6}) \"{System.Text.Encoding.ASCII.GetString(d, runStart, run)}\"");
                printed++;
            }
            run = 0;
        }
    }
    return 0;
}

// Validates the Core retexture workflow end-to-end.
// Usage: Probe <modFolder> <projectParentDir> [classNameSubstring]

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Probe <modFolder> <projectParentDir> [classNameSubstring]");
    return 1;
}

string modFolder = args[0];
string projParent = args[1];
string pick = args.Length > 2 ? args[2] : "Helm";

var mod = new ArmaMod { Name = Path.GetFileName(modFolder), Path = modFolder, DisplayName = Path.GetFileName(modFolder) };
var addons = Path.Combine(modFolder, "addons");
if (Directory.Exists(addons))
    mod.PboPaths.AddRange(Directory.GetFiles(addons, "*.pbo"));
Console.WriteLine($"Mod: {mod.DisplayName} ({mod.PboCount} PBOs)");

var assets = AssetService.LoadForMod(mod);
Console.WriteLine($"Assets with hidden selections: {assets.Count}");
foreach (var g in assets.GroupBy(a => a.Category).OrderBy(g => g.Key.ToString()))
    Console.WriteLine($"  {g.Key}: {g.Count()}");

var asset = assets.FirstOrDefault(a => a.ClassName.Contains(pick, StringComparison.OrdinalIgnoreCase));
if (asset is null) { Console.Error.WriteLine($"No asset matching '{pick}'."); return 2; }

Console.WriteLine($"\nPicked: [{asset.Category}] {asset.Label}");
Console.WriteLine($"  model: {asset.Model}");
Console.WriteLine($"  hiddenSelections:        {string.Join(", ", asset.HiddenSelections)}");
Console.WriteLine($"  hiddenSelectionsTextures: {string.Join(", ", asset.HiddenSelectionsTextures)}");

// Create a project and add the retexture (copies the source .paa).
Directory.CreateDirectory(projParent);
var proj = RetexProjectService.CreateProject(projParent, "PrimarisTest");
var entry = RetexProjectService.AddRetexture(proj, asset, mod.PboPaths, modAssets: assets);
RetexProjectService.GenerateConfig(proj);

Console.WriteLine($"\nProject: {proj.ProjectDir}");
Console.WriteLine("Copied textures:");
foreach (var f in Directory.GetFiles(proj.TexturesDir))
    Console.WriteLine($"  {Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)");

Console.WriteLine($"\n=== {proj.ConfigPath} ===");
Console.WriteLine(File.ReadAllText(proj.ConfigPath));

// Validate @Mod packing.
var pboc = ReTex.Core.Tools.PboTool.FindDefault();
if (pboc is not null)
{
    var res = await RetexProjectService.PackModAsync(proj, new ReTex.Core.Tools.PboTool(pboc));
    Console.WriteLine($"\nPack: exit={res.ExitCode}, output={res.OutputPath}");
    var modDir = RetexProjectService.ModFolder(proj);
    foreach (var f in Directory.GetFiles(modDir, "*", SearchOption.AllDirectories))
        Console.WriteLine($"  {f.Substring(proj.ProjectDir.Length + 1)} ({new FileInfo(f).Length} bytes)");
}
return 0;

static class RapClassFinder
{
    public static void FindAndDump(ReTex.Core.Rap.RapClass root, string name, int depth)
    {
        var found = Find(root, name);
        if (found is null) { Console.WriteLine($"(class '{name}' not found)"); return; }
        Dump(found, 0, depth);
    }

    static ReTex.Core.Rap.RapClass? Find(ReTex.Core.Rap.RapClass c, string name)
    {
        foreach (var ch in c.Classes)
        {
            if (ch.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return ch;
            var r = Find(ch, name);
            if (r is not null) return r;
        }
        return null;
    }

    public static void Dump(ReTex.Core.Rap.RapClass c, int d, int maxDepth)
    {
        var ind = new string(' ', d * 2);
        var baseName = string.IsNullOrEmpty(c.Parent) ? "" : $": {c.Parent}";
        Console.WriteLine($"{ind}class {c.Name}{baseName} {{");
        foreach (var kv in c.Properties)
            Console.WriteLine($"{ind}  {kv.Key} = {kv.Value};");
        if (d < maxDepth)
            foreach (var ch in c.Classes) Dump(ch, d + 1, maxDepth);
        Console.WriteLine($"{ind}}}");
    }
}
