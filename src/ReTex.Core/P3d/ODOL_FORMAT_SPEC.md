# ODOL v4x P3D format

This is the implementation reference for ReTex's Arma 3 ODOL reader. It summarizes the
BI Community documentation and the modern v73/v75 details verified against real models and
output from Arma 3 Tools Binarize. It is not a complete specification for every ODOL version.

Sources:

- [P3D File Format - ODOLV4x](https://community.bistudio.com/wiki/P3D_File_Format_-_ODOLV4x)
- [P3D Model Info](https://community.bistudio.com/wiki/P3D_Model_Info)
- [P3D Lod Faces](https://community.bistudio.com/wiki/P3D_Lod_Faces)
- [P3D Lod Sections](https://community.bistudio.com/wiki/P3D_Lod_Sections)
- [P3D Named Selections](https://community.bistudio.com/wiki/P3D_Named_Selections)
- [Compressed LZO File Format](https://community.bistudio.com/wiki/Compressed_LZO_File_Format)

## Scope

ReTex currently targets modern Arma 3 models, specifically ODOL v73 and v75. The reader:

- locates and decodes the highest-detail visual LOD;
- reads points, packed normals, UV sets, faces, sections, materials, and named selections;
- resolves diffuse texture paths for the 3D retexture preview;
- supports raw and LZO1X-compressed vertex arrays.

Older ODOL versions can differ in index widths, compression, and material layout. Do not apply
the v73/v75 refinements below to older files without validating their byte layout.

## Primitive types

All values are little-endian.

| Format name | Size | Meaning |
| --- | ---: | --- |
| `byte`, `tbool`, `TinyBool` | 1 | Byte or Boolean |
| `ushort`, `short` | 2 | Unsigned or signed 16-bit integer |
| `ulong`, `long` | 4 | Unsigned or signed 32-bit integer |
| `float` | 4 | IEEE 754 single precision |
| `asciiz` | variable | Null-terminated ASCII string |
| `XYZTriplet` | 12 | Three floats |
| `D3DCOLORVALUE` | 16 | Four floats |

The wiki uses `ulong` for a 4-byte value, not a C# or C `unsigned long` whose size may vary.

## File layout

```text
ODOLv4x {
    StandardP3DHeader Header;
    ModelInfo ModelInfo;
    Animations Animations;
    ulong StartAddressOfLods[Header.NoOfLods];
    ulong EndAddressOfLods[Header.NoOfLods];
    LODFaceDefaults FaceDefaults;
    ODOLv4xLod Lods[Header.NoOfLods];
}
```

The header contains the `ODOL` signature, version, LOD count, and one resolution value per LOD.
The start/end arrays contain absolute file offsets and use the same index as the resolutions.
Arma stores LOD data from higher to lower LOD type, so physical file order is not necessarily
visual-resolution order.

ReTex reads the header and ModelInfo directly. Its visual reader currently locates validated
`MinPos` anchors because the complete Animations and rigged LOD-head structures are not decoded.

## ModelInfo

ModelInfo contains model-wide bounds, centers, mass and rendering flags, followed by skeleton
metadata. The fields vary significantly by ODOL version. ReTex only consumes the subset needed
to validate LOD bounds and read the skeleton:

```text
asciiz SkeletonName;
if (SkeletonName is not empty) {
    tbool IsInherited;
    ulong BoneCount;
    { asciiz BoneName; asciiz ParentBoneName; } Bones[BoneCount];
    byte AlwaysZero; // modern Arma, non-VBS2
}
```

`OdolReader.ReadModelInfo` is byte-validated on real v73/v75 files. The generic wiki field list
does not describe every modern variant accurately enough to replace that implementation blindly.

## Visual LOD

The documented LOD body is:

```text
ulong nProxies;
LodProxy Proxies[nProxies];
ulong nLodItems;
ulong LodItems[nLodItems];
ulong nBoneLinks;
LodBoneLink BoneLinks[nBoneLinks];
float UnknownFloat1;
float UnknownFloat2;
XYZTriplet MinPos;
XYZTriplet MaxPos;
XYZTriplet AutoCenterPos;
float Sphere;
ulong nTextures;
asciiz TextureNames[nTextures];
ulong nMaterials;
LodMaterial Materials[nMaterials];
LodEdges Edges;
ulong nFaces;
ulong FacesAllocationSize;
ushort AlwaysZero;
LodFace Faces[nFaces];
ulong nSections;
LodSection Sections[nSections];
ulong nNamedSelections;
LodNamedSelection NamedSelections[nNamedSelections];
ulong nProperties;
{ asciiz Name; asciiz Value; } Properties[nProperties];
ulong nFrames;
LodFrame Frames[nFrames];
ulong IconColor;
ulong SelectedColor;
ulong SpecialFlags;
byte VertexBoneRefIsSimple;
ulong SizeOfVertexTable; // includes this field's four bytes
LodPointFlags PointFlags; // v50+
VertexTable Vertices;
```

Rigged models can have non-empty proxies, LOD items, and bone links. ReTex bypasses that variable
head by anchoring on a candidate `MinPos`, then validates the bounds and the structures that follow.
It currently accepts only empty edge arrays and zero animation frames in the selected visual LOD.

## Faces

```text
byte FaceType; // 3 = triangle, 4 = quad
Index VertexTableIndex[FaceType];
```

The wiki's older layout uses 16-bit indices. Modern v73/v75 files with large vertex tables use
32-bit indices. ReTex infers the width from `FacesAllocationSize`:

| Index width | Triangle memory size | Quad memory size |
| ---: | ---: | ---: |
| 2 bytes | 8 | 10 |
| 4 bytes | 16 | 20 |

The on-disk face size is `1 + FaceType * IndexWidth`. The allocation size and section offsets use
the in-memory size `IndexWidth * (1 + FaceType)`, not the smaller on-disk size.

## Sections

A section owns a consecutive range of faces and supplies their material and texture metadata.
The v73/v75 layout consumed by ReTex is:

```text
ulong FaceIndexOffsets[2];
ulong MaterialIndexOffsets[2];
ulong CommonPointsUserValue;
short CommonTextureIndex;
ulong CommonFaceFlags;
long MaterialIndex;
if (MaterialIndex == -1) byte ExtraByte;
ulong UnknownLong;
float UnknownResolution1;
float UnknownResolution2;
ulong TrailingValue;
```

`FaceIndexOffsets` are cumulative memory offsets into the face block. To map sections to face
ordinals, walk faces in order and advance by the memory size in the table above.

`CommonTextureIndex` indexes the LOD texture-name array. If it is absent, ReTex falls back to the
section material's diffuse stage texture.

## Materials

```text
asciiz RvMatName;
ulong Type;
D3DCOLORVALUE Emissive, Ambient, Diffuse, ForcedDiffuse, Specular, Specular2;
float SpecularPower;
ulong PixelShaderId;
ulong VertexShaderId;
ulong MainLight;
ulong FogMode;
asciiz SurfaceName;
ulong Arma1Mostly1;
ulong RenderFlags;
ulong nStageTextures;
ulong nStageTransforms;
StageTexture StageTextures[nStageTextures];
StageTransform StageTransforms[nStageTransforms];
StageTexture TrailingStage;
```

For modern files:

```text
StageTexture   = ulong Filter; asciiz Texture; ulong TransformIndex; byte TrailingByte;
StageTransform = ulong UVSource; float Transform[4][3];
```

The trailing stage must be parsed as a real `StageTexture`; its path is often empty but can contain
a thermal texture. ReTex chooses a stage ending in `_co.paa`, or the first non-empty stage as a
fallback.

## Named selections

Named selections connect `hiddenSelections[]` names to faces or sections:

```text
asciiz Name;
ulong nFaceIndexes;
Index FaceIndexes[nFaceIndexes];
ulong nAlwaysZero;
ulong AlwaysZero[nAlwaysZero];
tbool IsSectional;
ulong nSectionIndexes;
ulong SectionIndexes[nSectionIndexes];
ulong nVertexIndexes;
Index VertexIndexes[nVertexIndexes];
ulong nWeights;
byte VertexWeights[nWeights];
```

For v73/v75, `FaceIndexes` and `VertexIndexes` use the detected face-index width. Section indexes
remain 32-bit. A sectional selection can have no face list; its membership is then the union of
the referenced section face ranges.

Each non-empty named-selection array has a one-byte packing flag: `0` means raw and a non-zero
value means LZO1X-compressed. Empty arrays have neither a flag nor payload.

## Vertex table

```text
UvSet DefaultUVSet;
ulong nUVSets;
UvSet AdditionalUVSets[nUVSets - 1];
ulong nPoints;
XYZTriplet Points[nPoints];
ulong nNormals;
PackedNormal Normals[nNormals];
ulong nMinMax;
MinMax MinMaxValues[nMinMax];
ulong nProperties;
VertProperty Properties[nProperties];
ulong nNeighbors;
VertexNeighborInfo Neighbors[nNeighbors];
```

ReTex decodes point flags, all UV sets needed to reach the point data, points, and normals. Optional
MinMax, vertex-property, and neighbor arrays are not needed by the preview and are not interpreted.

### Array framing

A `CompressedFill<T>` is:

```text
ulong Count;
tbool DefaultFill;
if (DefaultFill) T Value;
else T Values[Count];
```

If a real array's expected byte size is below 1024, its bytes follow directly. At 1024 bytes or
larger, one packing byte precedes it:

- `0x00`: raw bytes;
- `0x02`: one self-terminating LZO1X block.

Point arrays are plain counted arrays and omit `DefaultFill`. The decompressor must produce the
exact expected output size and report consumed input bytes so parsing can continue at the next
field. `Lzo1x.Decompress` implements this behavior; stream wrappers that read past the block are
not suitable.

### UV sets

```text
float UVScale[4]; // minU, minV, maxU, maxV
CompressedFill<PackedUV> UVs;

PackedUV = short U; short V;
```

Arma Binarize maps each UV component relative to its min/max range into signed 16-bit values from
`-32767` to `32767`. Decode each component as:

```text
relative = (packed + 32767.0) / 65534.0
value    = minimum + relative * (maximum - minimum)
```

Treating the packed words as unsigned moves UV islands to unrelated parts of the texture atlas.
This signed mapping was verified by binarizing an MLOD with known UVs using Arma 3 Tools.

### Packed normals

A normal is three signed 10-bit fields in one 32-bit word:

```text
x = packed        & 0x3ff;
y = (packed >> 10) & 0x3ff;
z = (packed >> 20) & 0x3ff;
if (x > 511) x -= 1024;
if (y > 511) y -= 1024;
if (z > 511) z -= 1024;
normal = (x, y, z) * (-1.0 / 511.0);
```

## Verified fixtures

- `Base_Gravis_Pack.p3d`, ODOL v73: complete visual LOD parsing, materials, 32-bit faces,
  sections, named selections, LZO arrays, 7,799 points, UVs, and normals.
- `Grav_U.p3d` and `Grav_U_Inceptor.p3d`, ODOL v75: up to 28,383 points and 32,225 faces,
  four sections, named-selection retexture mapping, and correct in-game UV placement.
- Arma 3 Tools Binarize control model, ODOL v75: known MLOD UVs round-trip through the signed
  `PackedUV` formula above.

## Current limitations

- The complete Animations block and variable rigged LOD head are not decoded.
- Non-empty LOD edge arrays and animation frames are unsupported.
- Material parsing is validated for the modern Arma layouts encountered by ReTex, not every shader
  or historical ODOL version.
- The visual reader uses validated LOD anchors and chooses the highest-face-count non-degenerate UV
  LOD instead of traversing every LOD solely through the address table.
