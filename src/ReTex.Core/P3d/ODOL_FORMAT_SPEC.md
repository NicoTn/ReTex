# ODOL v4x (.p3d) binary format — authoritative structure reference

Source: BI Community wiki, transcribed **verbatim** on 2026-07-01 by reading the real
rendered pages through the Chrome browser extension (see note at bottom — automated
`WebFetch` gets HTTP 403 from Cloudflare AND, via mirrors, hallucinates wrong struct
fields; the in-browser read is the only reliable channel found so far).

Pages captured:
- https://community.bistudio.com/wiki/P3D_File_Format_-_ODOLV4x
- https://community.bistudio.com/wiki/P3D_Model_Info
- https://community.bistudio.com/wiki/P3D_Lod_Faces
- https://community.bistudio.com/wiki/P3D_Lod_Sections
- https://community.bistudio.com/wiki/P3D_Named_Selections

Type legend (from "Generic FileFormat Data Types"): `ulong`/`long` = 4 bytes,
`ushort`/`short` = 2 bytes, `float` = 4 bytes, `byte`/`tbool`/`TinyBool` = 1 byte,
`asciiz` = null-terminated string, `XYZTriplet` = 3 floats (12 bytes),
`D3DCOLORVALUE`/`rgba` = 4 floats resp. 4 bytes.

## Top-level file layout

```
ODOLv4x
{
  StandardP3DHeader Header;
  struct ModelInfo;
  Animations Animations;
  ulong StartAddressOfLods[Header.NoOfLods];   // absolute file offsets
  ulong EndAddressOfLods [Header.NoOfLods];
  LODFaceDefaults LODFaceDefaults;
  ODOLv40Lod ODOLv40Lods[Header.NoOfLods];
}
```

`StartAddressOfLods[i]` is the absolute file offset of the LOD whose resolution is
`resolutions[i]` — a direct index, no need to parse LOD sizes. **This is the clean way to
seek to any LOD.** (Empirically the address table sits after the Animations block, which
for a rigged/animated model is non-trivial in size — so it is NOT at ModelInfo.EndOffset.)

### StandardP3DHeader
```
char[4] Filetype;   // "ODOL"
ulong   Version;    // p3d_type
>=ODOLV58: ulong appID
ODOLV58 : Asciiz A3HeaderPrefix;   // proxie prefix, usually empty
ODOLV75 : bytes  encryption
ulong   NoOfLods;   // alias NoOfResolutions
```
Note: `float resolutions[NoOfLods]` is listed as ModelInfo's first field on the wiki, but
this codebase reads it in `OdolReader.ReadHeader` (so `ReadModelInfo` starts at `Index`).

### Animations
```
tbool AnimsExist;
if (AnimsExist) {
  ulong nAnimationClasses;
  AnimationClass AnimationClasses[nAnimationClasses];
  long  NoOfResolutions;                 // -1 if nAnimationClasses == 0
  Bones2Anims Bones2Anims[NoOfResolutions];
  Anims2Bones Anims2Bones[NoOfResolutions];
}
```
`AnimationClass` = ulong AnimTransformType; asciiz AnimClassName; asciiz AnimSource;
float MinMaxValue[2]; float MinMaxPhase[2]; ulong junk(=953267991);
IF ARMA3: ulong Always0; ulong sourceAddress; then a per-AnimTransformType float payload
(type 0-3 rotation: 2 floats; 4-7 translation: 2 floats; 8 direct: 8 floats; 9 hide: 1 float).
`Bones2Anims`/`Anims2Bones` per the ODOLV4x page.

### LODFaceDefaults
```
tbool UseDefault[Header.NoOfLods];
FaceData {
  ulong HeaderFaceCount;
  ulong aDefaultLong;   // 0xffffffff or e.g. 6f 7a 80 fa
  byte  UnknownByte;    // generally zero
  byte  aFlag;          // zero or one
  bytes Zeroes[7];
}[number of FALSE UseDefault entries];   // a FaceData only for lods whose UseDefault==0
```

## ModelInfo (fields AFTER `resolutions[]`, i.e. where `ReadModelInfo` starts)

```
ulong      Index;
float      MemLodSphere;
float      GeoLodSphere;
ulong      Remarks;
ulong      andHints;
ulong      orHints;
XYZTriplet geo_offset;
rgba       mapIconColor;
rgba       mapSelectedColor;
float      ViewDensity;
XYZTriplet bboxMinPosition;
XYZTriplet bboxMaxPosition;
if version>=70: float lodDensityCoef;
if version>=71: float drawImportance;
if version>=52: MinMaxVectors visual_bounds;   // 2x XYZTriplet = 24 bytes
XYZTriplet boundingCenter;
XYZTriplet geometryCenter;
XYZTriplet centerOfMass;
XYZTriplet p3dinfo_invInertia[3];               // 36 bytes
TinyBool   AutoCenter, lockAutoCenter, canOcclude, canBeOccluded, allowAnimation;  // 5 bytes
if version>=73 || dayz: TinyBool disableCover;
float      ThermalProfile[24];                  // 96 bytes
if dayz: float dayz_thermal_extra;
ulong      forceNotAlphaModel;                  // V48+
ulong      sbSource;
TinyBool   prefershadowvolume;
if version==48: float shadowOffset;
bool       allowAnimation;                       // (second one — wiki lists twice)
byte       mapType;
if dayz: dayz_mass_array[];
float      Mass;
float      MassReciprocal;
float      ArmorMass;
float      ArmorReciprocal;
if version>=72: float explosionshielding;
if version>=56: byte UnknownByteIndices[14]  else byte UnknownByteIndices[12];
//////// ARMA (V4x) ONLY ////////
ulong      UnknownLong;
TinyBool   canBlend;
if (dayz==54): byte dayzv126;
asciiz     ClassType;      // class="House"
asciiz     DestructType;   // damage="Tent"
TinyBool   frequent;
ulong      Always0;
if version>=54: byte preferred_shadows[NoOfLods][12];
```

**IMPORTANT DISCREPANCY (unresolved):** the byte total of the above for v73 is ~405 bytes
+ 2 asciiz, but empirically the skeleton name string sits only ~216 bytes after ModelInfo's
`Index` in a real v73 file (LilardPack Base_Gravis_Pack.p3d). The existing
`OdolReader.ReadModelInfo` uses a DIFFERENT (older/shorter) field list that happens to skip
exactly 216 bytes and lands correctly on the skeleton (verified: correct name, 103 bones,
correct bbox). So in real Arma3 files the **Skeleton appears much earlier than this wiki
field order implies** — the skeleton is NOT at the tail of this full ModelInfo field list.
Do not "fix" ReadModelInfo to match this wiki order without re-verifying against a real file;
the current code is empirically correct even though its field breakdown differs from the wiki.

### Skeleton (immediately follows the ModelInfo numeric block)
```
asciiz SkeletonName;               // "" if none
if (SkeletonName != null) {
  tbool isInherited;
  ulong NoOfBoneNames;
  SkeletonBoneName SkeletonBoneNames[NoOfBoneNames];   // {asciiz BoneName; asciiz ParentBoneName;}
  if (type>40 && !VBS2) byte Always0;   // arma2+
}
```

## ODOLv4xLod (one per resolution LOD — THE geometry struct)

```
ulong nProxies;
LodProxy LodProxies[nProxies];          // see P3D_Lod_Proxies
ulong nLodItems;
ulong LodItems[nLodItems];              // potentially compressed, except v64+
ulong nBoneLinks;
LodBoneLink LodBoneLinks[nBoneLinks];   // {ulong NoOfLinks(0..3); ulong Value[NoOfLinks];}
float UnknownFloat1;
float UnknownFloat2;
XYZTriplet MinPos;
XYZTriplet MaxPos;
XYZTriplet AutoCenterPos;
float Sphere;
ulong NoOfTextures;
asciiz LodPaaTextureNames[NoOfTextures];   // "lilardpack\textures\gravispack_co.paa"
ulong NoOfMaterials;
LodMaterial LodMaterials[NoOfMaterials];   // big — see below
LodEdges LodEdges;                          // compressed, see P3D_Lod_Edges
ulong NoOfFaces;
ulong OffsetToSectionsStruct;               // == faces AllocationSize (memory size); see Faces
ushort AlwaysZero;
LodFace LodFace[NoOfFaces];                  // see Faces
ulong nSections;
LodSection LodSections[nSections];           // see Sections
ulong nNamedSelections;
LodNamedSelection LodNamedSelections[nNamedSelections];   // potentially compressed; see below
ulong nTokens;
NamedProperty NamedProperties[nTokens];      // {asciiz Property; asciiz Value;}
ulong nFrames;
LodFrame LodFrames[nFrames];                  // see P3D_Lod_Frames
ulong IconColor;
ulong SelectedColor;
ulong special;                                // IsAlpha|IsTransparent|IsAnimated|OnSurface
byte  vertexBoneRefIsSimple;
ulong sizeOfVertexTable;                      // includes these 4 bytes
if (v5x) LodPointFlags LodPointFlags;         // potentially compressed
VertexTable VertexTable;
```

**Empirically confirmed on Base_Gravis_Pack.p3d LOD0 (res 1.0):** MinPos is at file
offset 4249 = `[-0.563, 0.119, -0.239]`, MaxPos `[0.563, 0.940, 0.744]` (matches model bbox).
`NoOfTextures=2` at offset 4289, first texture string at 4293. **This rigged backpack has
NONZERO LodItems/BoneLinks** (the region between LOD-start and MinPos is not three zero
counts) — the earlier "0 of each" assumption was wrong. So Proxies/LodItems/BoneLinks/Edges
DO need real parsing (and some are compressible) to walk from LOD-start to MinPos — OR use
`StartAddressOfLods[]` to seek to the LOD start and still parse forward through these, OR
anchor on the MinPos/MaxPos float pattern as a shortcut.

### VertexTable (all arrays subject to the 1024-byte compression rule)
```
UvSet DefaultUVset;
ulong nUVs;
UvSet UVSets[nUVs-1];
ulong NoOfPoints;
XYZTriplet LodPoints[NoOfPoints];
ulong nNormals;
(A2)LodNormals LodNormals[nNormals];
ulong nMinMax;
(A2)LodMinMax MinMax[nMinMax];       // optional
ulong nProperties;
VertProperty VertProperties[nProperties];   // optional, skeleton-related
ulong Count;
VertexNeighborInfo neighborBoneRef[Count];  // optional
```
"All non-zero counts are the same. Points, PointFlags, Normals and UV1 arrays are an
integral group — either all present or none." UV2/MinMax/VertProperties/neighbor are
individually optional (their counts can be 0).

### CompressedFill arrays (LodPointFlags, LodUVs, LodNormals)
```
ulong Count;
tbool DefaultFill;
if (DefaultFill) type Array;          // single default value for all Count entries
else             type Array[Count];   // subject to the 1024 rule if expectedSize>=1024
```
- `UvSet`: `if TrueARMA2 { float UVScale[4]; }` then `(A2)LodUV LodUV`.
  `LodUV` = CompressedFill of UVPair(float U,V). `A2LodUV` = CompressedFill of float.
- `LodNormals` = CompressedFill of XYZTriplet. `A2LodNormals` = CompressedFill of
  `CompressedXYZTriplet` (3×10-bit packed in a uint32):
  ```
  scale = -1.0/511;  x=cx&0x3FF; y=(cx>>10)&0x3FF; z=(cx>>20)&0x3FF;
  if(x>511)x-=1024; if(y>511)y-=1024; if(z>511)z-=1024;
  X=x*scale; Y=y*scale; Z=z*scale;   // (wiki's Z-guard tests x, likely a typo — use z)
  ```

### LodMaterial (a direct replication of the .rvmat)
```
asciiz RvMatName;                 // "" for the default stage
ulong  Type;                      // 9 == ArmA
D3DCOLORVALUE Emissive, Ambient, Diffuse, forcedDiffuse, Specular, Specular2;   // 6*16
float  SpecularPower;
ulong  PixelShaderId;             // see refShaderStages table
ulong  VertexShaderId;
LongBool mainLight;
ulong  ul_FogMode;
Asciiz BiSurfaceName;             // .bisurf
LongBool Arma1Mostly1;
ulong  RenderFlags;
ulong  nTextures;
ulong  nTransforms;               // == nTextures
LodStageTexture   StageTextures  [nTextures];    // {ulong TextureFilter; asciiz PaaTexture; ulong TransformIndex;}
LodStageTransform StageTransforms[nTransforms];   // {ulong UVSource; float Transform[4][3];}  (4*3 floats = 48 bytes)
```
First StageTexture is a dummy default (no PaaTexture). PaaTexture may be a literal path or a
procedural string like `#(argb,8,8,3)color(0,0,0,1,CO)`.

## EMPIRICALLY-CONFIRMED v73 REFINEMENTS (Base_Gravis_Pack.p3d, LOD0)

The generic wiki layout above is version-agnostic; these are the exact byte details that a
real ODOL v73 file uses, confirmed by parsing to validated anchors (all values matched
Eliteness). `OdolLodReader.ReadFromMinPos` implements exactly this:

- **LodMaterial (Type 9/11, PixelShaderId 102 "Super"):** after `RvMatName`, `Type(u32)`,
  6×`D3DCOLORVALUE`(96), `SpecularPower(f32)`, `PixelShaderId(u32)`, `VertexShaderId(u32)`,
  `mainLight(u32)`, `ul_FogMode(u32)`, `BiSurfaceName(asciiz)`, `Arma1Mostly1(u32)`,
  `RenderFlags(u32)`, `nTextures(u32)`, `nTransforms(u32)`. Then:
  - `StageTexture` × nTextures, each = `TextureFilter(u32)` + `PaaTexture(asciiz)` +
    `TransformIndex(u32)` + **1 trailing byte** (not in the wiki struct but present per stage).
  - `StageTransform` × nTransforms, each = `UVSource(u32)` + `Transform[4][3]`(48 bytes).
  - Then a **10-byte per-material trailer** (`03 00 00 00` + 6 zero bytes) before the next material.
- **LodEdges:** two `u32` counts (both 0 on this unrigged-visual LOD = 8 zero bytes). Nonzero
  = compressed, not yet handled.
- **NoOfFaces(u32), OffsetToSectionsStruct(u32), AlwaysZero(u16)**, then faces.
- **LodFace (v73):** `FaceType(byte)` + `VertexTableIndex[FaceType]` as **uint32** each
  (NOT ushort — the wiki's ushort is for older ODOL). Triangle = 13 bytes on disk (1 + 3×4);
  `OffsetToSectionsStruct = 16 × nFaces` (memory size, 16/tri). Section `FaceIndexOffsets`
  are in these 16-byte memory units.
- **nSections(u32)**, then each **LodSection = 46 bytes**: `FaceIndexOffsets[2]`(u32×2, memory
  units) + `MaterialIndexOffsets[2]`(u32×2) + `CommonPointsUserValue(u32)` +
  `CommonTextureIndex(short/u16)` + `CommonFaceFlags(u32)` + `MaterialIndex(i32)` +
  (`ExtraByte` only if MaterialIndex==-1) + `UnknowLong(u32)` + `UnknownResolution(f32)` +
  `UnknownResolution2(f32)` + **1 trailing u32** (present on v73, beyond the wiki struct).
  Confirmed: section0 `[0..8832]` tex1/mat0, section1 `[8832..141008]` tex0/mat1 — lands
  exactly on `nNamedSelections=4`.
- **nNamedSelections(u32)**, then each LodNamedSelection = `SelectedName(asciiz)` +
  `NoOfFaces(u32)`+FaceIndexes + `Always0Count(u32)`+array + `IsSectional(tbool)` +
  `NoOfUlongs(u32)`+SectionIndex + `nVertices(u32)`+VertexTableIndexes +
  `nTextureWeights(u32)`+VerticesWeights. **Confirmed framing:** each length-prefixed array,
  WHEN its count > 0, is preceded by a **1-byte compression flag** (0 = raw follows); when
  count == 0 there is no flag and no data. `NoOfUlongs` is always read (not gated on
  IsSectional). Confirmed: `[spine2 (sectional, SectionIndex=1), backpackcamo, neck,
  backpackcamo_lightcollar]` parse and land exactly on the tokens.
- **nTokens(u32)** then NamedProperties `(asciiz Property, asciiz Value)` — confirmed
  `autocenter=0, lodnoshadow=1`.
- **nFrames(u32)** (0 here) + LodFrames.
- Trailer: `IconColor(u32) SelectedColor(u32) special(u32) vertexBoneRefIsSimple(byte)
  sizeOfVertexTable(u32, includes its own 4 bytes)`. Then (if v5x) LodPointFlags, then the
  VertexTable. Confirmed: sizeOfVertexTable=126501, VertexTable at file offset 121084,
  ending at 121084-4+126501 = 247581 (= this LOD's end).

- **REMAINING WORK — the compressed VertexTable (the last field):** structure per the
  "VertexTable" section above (DefaultUVset, nUVs, UVSets, NoOfPoints, LodPoints, nNormals,
  LodNormals, ...). NoOfPoints = 7799 here (== max face vertex index 7798 + 1). The point/
  normal/UV arrays exceed 1024 bytes and the point data at the expected offset does NOT decode
  as raw XYZ floats, so they are LZO-compressed (Arma). Implementing this needs: (a) the exact
  CompressedFill framing (`ulong Count; tbool DefaultFill; if(DefaultFill) single-value else
  array[Count]`) — note the first VertexTable u32 is the UV set's CompressedFill Count (7799),
  with a `DefaultFill`/flag byte following; (b) LZO1x decompression via the existing `lzo.net`
  dependency, to the known expected output size AND tracking input-bytes-consumed to advance
  the cursor to the next array; (c) 10-bit normal unpacking (CompressedXYZTriplet, below).
  This is the one focused sub-task left for a renderable mesh.

## LodFaces (see P3D_Lod_Faces)

```
struct { ulong nFaces; ulong AllocationSize; LodFace LodFaces[nFaces]; }
```
**ODOL ARMA v4x LodFace ON DISK:**
```
byte   FaceType;                    // 3 == triangle, 4 == quad
ushort VertexTableIndex[FaceType];  // indices into the VertexTable
```
So disk size = 1 + 2*FaceType (triangle=7, quad=9). In MEMORY each face is
2 (FaceType as short) + 2*FaceType (triangle=8, quad=10) → `AllocationSize = sum of memory
sizes`. For all-triangles: `AllocationSize = 8*nFaces`. Verified: 8813 faces → 70504
(== `OffsetToSectionsStruct`; == Eliteness's reported face-data size). Relation:
`V4x OffsetToSectionsStruct = AllocationSize` and on-disk face bytes = AllocationSize - nFaces.
(In ArmA, FaceFlags & TextureIndex were moved OUT of faces into Sections.)

**Vertex index reordering for rendering (DirectX clockwise):**
- Odol triangle stores order B,A,C; for CW output emit index[0],index[2],index[1].
- Odol quad stores B,A,D,C; for CW output emit index[0],index[3],index[2],index[1].

## LodSection (see P3D_Lod_Sections) — ODOLV4x

```
ulong FaceIndexOffsets[2];      // from/to, in MEMORY-offset units (8/tri, 10/quad) into the face block
ulong MaterialIndexOffsets[2];  // ODOLV4x only
ulong CommonPointsUserValue;
short CommonTextureIndex;        // indexes LodPaaTextureNames; -1 = none
ulong CommonFaceFlags;
//// ODOLV4x only ////
long  MaterialIndex;             // indexes LodMaterials; -1 = none
if (MaterialIndex == -1) byte ExtraByte;
ulong UnknowLong;                // generally 2
float UnknownResolution;
float UnknownResolution2;        // generally 1000.0
```
Sections partition the face block sequentially. `FaceIndexOffsets` are cumulative MEMORY
offsets (start at 0; each triangle advances +8, each quad +10) — to map a section to actual
faces, walk faces in order tracking that memory cursor. `CommonTextureIndex` is the key field
for retexture preview: it says which texture the section's faces use.

## LodNamedSelection (see P3D_Named_Selections) — ODOL (arrays subject to 1024 rule; ODOL7/40 LZSS, Arma2 LZO)

```
asciiz SelectedName;               // "backpackcamo", "neck", ...  (matches hiddenSelections[])
ulong  NoOfFaces;
ushort FaceIndexes[NoOfFaces];      // indexes into LodFaces
ulong  Always0Count;
unknownType Array[Always0Count];    // IsSectional must be true
//// ODOL7 only: ulong Count; ulong UnknownV7Array[Count]; ////
tbool  IsSectional;
ulong  NoOfUlongs;
ulong  SectionIndex[NoOfUlongs];    // indexes LodSections (IsSectional)
ulong  nVertices;
ushort VertexTableIndexes[nVertices];
ulong  nTextureWeights;
byte   VerticesWeights[nTextureWeights];   // per-vertex weights extending VertexTableIndexes
```
Point/face weight bytes: 0 = not selected; 1 = 100%; 2..255 = fractional
(`decodeWeight`: b==0→0, b==2→1, b>2→1-round((b-2)/2.55555)*0.01).

## Compression
Compressible arrays are compressed only when the expected uncompressed size is **>= 1024
bytes**. Scheme depends on version: ODOL7/ODOLV40 = LZSS; Arma2/Arma3 = **standard LZO1X**
(oberhumer `lzo1x_decompress_safe`, confirmed via the BI wiki "Compressed LZO File Format"
page). One knows the item count and expected output size; decompress to exactly that size.

### CRITICAL for implementation (learned empirically this session)
- Arma's LZO is plain **LZO1X**, so a standard LZO1X decompressor is correct.
- The p3d arrays store NO compressed-length prefix — each compressed array is a self-
  terminating LZO1X block. To walk to the NEXT field you must know **how many input bytes the
  block consumed**. The reference `lzo1x_decompress_safe(in, out, OutLen)` returns exactly that
  (`return ip - in`) and errors if the block doesn't exactly fill `OutLen`.
- **`lzo.net`'s `LzoStream` is NOT usable for this** as-is: it reads the underlying stream in
  ~16 KB buffered chunks, so (a) `ms.Position` overshoots and can't give the true consumed
  count, and (b) it decodes straight past one block's end marker into the next block's bytes,
  producing garbage. Fix: port the wiki's `lzo1x_decompress_safe` (it returns consumed bytes and
  stops exactly at the block's end marker), OR feed `LzoStream` a `MemoryStream` bounded to the
  exact compressed block (whose length you'd first have to determine).
- Reference decompressor source (verbatim from the wiki, `#define M2_MAX_OFFSET 0x0800`):
  returns `ip-in` (consumed) at the end marker `if (m_pos == op) { ... return ip-in; }`. Port
  it faithfully — it is goto-heavy (labels: first_literal_run, match, copy_match, match_done,
  match_next). Full source is on `https://community.bistudio.com/wiki/Compressed_LZO_File_Format`.

### EMPIRICALLY-CONFIRMED VertexTable framing (Base_Gravis_Pack.p3d LOD0, all decoded & validated)
The faithful `Lzo1x` port decodes every array; each block's consumed-byte count lands exactly on
the next field. Full walk from `VertexTableStartOffset` (= right after sizeOfVertexTable = 121084):

1. **LodPointFlags** (present because v73 counts as "v5x"): a CompressedFill of ulong.
   `Count(u32)=7799`, `DefaultFill(byte)=1` -> single ulong value (0x3F) fills all. 9 bytes total
   here (default-filled, so no compressed data). @121084.
2. **DefaultUVset** @121093: `float UVScale[4]` = [minU, minV, maxU, maxV] = [0, 0, 0.9978, 1.0]
   (16 bytes), then LodUV CompressedFill: `Count(u32)=7799` @121109, `DefaultFill(byte)=0` @121113,
   `packingFlag(byte)=0x02` @121114 (0x02 = LZO), then the LZO block @121115 -> 31196 bytes
   (7799 UV entries of 4 bytes = 2x uint16, dequantized via UVScale), consumed 14912, ends 136027.
3. `nUVs(u32)=1` @136027 (so no extra UVSets).
4. `NoOfPoints(u32)=7799` @136031.
5. **LodPoints** (plain array, NO DefaultFill byte): `packingFlag(byte)=0x02` @136035, LZO block
   @136036 -> 93588 bytes = 7799 `XYZTriplet` (12 bytes each), consumed 42246, ends 178282.
   **First points decode to valid in-bbox coordinates** — e.g. (0.1906, 0.6627, 0.6820).
6. `nNormals(u32)=7799` @178282.
7. **LodNormals** (CompressedFill): `DefaultFill(byte)=0` @178286, `packingFlag(byte)=0x02` @178287,
   LZO block @178288 -> 31196 bytes = 7799 `CompressedXYZTriplet` (4 bytes each, 10-bit packed,
   scale -1/511), consumed 21393, ends 199681.
8. `nMinMax(u32)` @199681, then MinMax / VertProperties / neighborBoneRef (optional; not needed
   for the viewer).

**Framing rule:** a compressible array = `[Count(u32)]` then, for CompressedFill arrays,
`[DefaultFill(byte)]` (1 => a single inline value fills all, no compressed data; 0 => a real
array follows); plain arrays (LodPoints) skip the DefaultFill byte. When a real array follows and
its expected size >= 1024 it is `[packingFlag(byte)=0x02][LZO1x block]`; the LZO block is self-
terminating and `Lzo1x.Decompress` returns the consumed input bytes to reach the next field.
(Element sizes: UV=4, point=12, normal=4, pointflag=4.) Diagnostic verbs: `--lzo1x`, `--lzonat`.

---
### How this spec was obtained (do not lose this)
`WebFetch` on community.bistudio.com returns **HTTP 403** (Cloudflare). Mirrors
(community.bohemia.net) don't 403 but WebFetch's summarizer **hallucinates plausible-but-wrong
struct fields** even when asked for verbatim text (it invented a ModelInfo layout that failed
byte-verification). The reliable method: open the page in the **Chrome browser extension**
(`mcp__claude-in-chrome__navigate` + `get_page_text`) — the real browser passes the Cloudflare
challenge and returns exact page text. Retry the extension connection if it reports
"not connected"; it recovered after a couple of tries this session.
