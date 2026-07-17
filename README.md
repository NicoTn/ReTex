# ReTex - Arma 3 Retexture Studio

> [!WARNING]
> This build contains 3D ODOL model decoding. Use it only with models you own or have explicit
> permission to inspect. Do not use it to extract, reconstruct, or redistribute another creator's work.

<img width="1872" height="745" alt="ReTex application showing the asset browser and 3D model preview" src="https://github.com/user-attachments/assets/c3e7bdeb-970d-402e-a922-b87fabe1d9bd" />

ReTex is a Windows desktop app for creating Arma 3 retexture mods. It scans installed mods,
finds assets with hidden selections, copies their source textures, generates `config.cpp`, and
packages the finished project as a loadable `@mod`.

![Windows](https://img.shields.io/badge/platform-Windows-blue)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)

## Features

- **Mod and asset browser**: scans Workshop or local mod folders and groups retexturable equipment,
  weapons, vehicles, units, props, backpacks, and glasses.
- **Native config parsing**: reads both rapified `config.bin` and plain `config.cpp`, including
  inheritance and cross-PBO hidden-selection values.
- **2D texture preview**: decodes Arma `.paa` textures, including DXT1, DXT5, and LZO data.
- **3D model preview (in progress)**: reads supported modern ODOL v73/v75 and MLOD models, maps
  materials and hidden selections, and displays project textures directly on the model.
- **UV inspection tools**: identify model parts, locate clicked geometry on the texture, display a
  UV grid, and temporarily adjust UV orientation for diagnostics.
- **Live texture reload**: watches the selected project entry's PAA files and updates the existing
  2D image and 3D material in place when an external editor saves or replaces them. The model,
  geometry, UVs, and camera are not rebuilt.
- **Integrated 2D/3D painting**: paint flattened project textures directly on the atlas or project
  brush strokes through the visible model surface. Changes appear in both views immediately.
- **Paint and recolor tools**: brush, eraser, eyedropper, contiguous/global fill, magic-wand color
  selection, tolerance-based replacement, luminance-preserving colorize, texture-preserving tint,
  and tiled undo/redo.
- **Native PAA saving**: writes BC1/BC3 textures and mipmaps without requiring Arma 3 Tools.
- **Low-latency paint feedback**: uploads only changed texels, keeps 3D materials bound to the live
  bitmap, and displays measured input-to-bitmap CPU latency in Paint Studio.
- **Frame-batched painting**: high-frequency mouse input is coalesced into interpolated 60 Hz paint
  passes. Each touched texture uploads once per frame, and overlapping 3D projection work is reduced.
- **Project management**: create or open projects, add individual assets or batches, duplicate and
  remove entries, and open copied textures in the configured image editor.
- **Config editor**: edit generated `config.cpp` with syntax highlighting, line numbers, and search.
- **One-click packaging**: generates `mod.cpp` and packs `addons\main.pbo` into a ready-to-load
  Arma 3 mod folder.

## 3D preview (in progress)

The 3D preview is actively being developed. It currently provides a practical way to inspect many
modern Arma 3 retextures before packing or launching the game:

- Decodes visual LOD geometry from supported ODOL v73/v75 and editable MLOD models.
- Reads packed points, normals, UV sets, triangle and quad faces, sections, and materials.
- Resolves diffuse PAA textures from model sections and RVMAT stages.
- Maps project textures through hidden-selection names and face membership where available.
- Displays separate texture groups with hover-to-identify labels.
- Supports model orbit, zoom, view-cube navigation, and automatic framing.
- Lets you Ctrl+click the model to locate the sampled UV point on the 2D texture.
- Lets you Ctrl+click the texture to highlight nearby faces on the model.
- Provides a labeled UV grid, UV-map export, and temporary flip/rotate/offset diagnostics.
- Replaces watched project materials in place after an external PAA save, without rebuilding the
  model, geometry, UVs, or camera.

The preview is not intended to reproduce Arma's full renderer. Shader stages, animations, proxies,
and some historical or uncommon P3D layouts are not fully supported yet. If a model cannot be
decoded safely, ReTex keeps the asset available and falls back to the 2D texture preview.

## Paint workspace

Select a project entry and choose **Paint Studio** from either preview toolbar. Painting opens in a
separate, resizable window so the 2D atlas and 3D model can use the full screen. Choose a 2D, 3D,
or split layout and use the texture selector to change the active atlas. A 3D stroke paints every
visible project texture crossed by the brush and is recorded as one undo action.

The 3D projector uses normalized triangle weights, wraps negative and tiled UV coordinates, keeps
generated texels inside the atlas, and always samples the exact center of the screen-space brush.

Paint Studio uses a Photoshop-style left sidebar for tools, foreground/background colors, RGB and
HSV controls, recent swatches, brush settings, and selection options. With **MagicWand**, click to
replace the selection, hold **Shift** while clicking to add matching areas, or hold **Alt** while
clicking to remove matching areas.

The color panel includes a saturation/value field, hue rail, live foreground/background swatches,
hex and channel entry, and recent colors. The options bar exposes the active tool, brush size,
opacity, texture, workspace layout, history, and save actions. Hard, Soft, and Airbrush presets are
available from the brush panel.

- Left-drag paints with the selected brush or eraser.
- Brush and eraser show a high-contrast circular cursor matching the affected diameter in both the
  2D texture and screen-space 3D projection.
- Right-click samples a color in 2D. In 3D, right-drag rotates the camera and **Ctrl+right-click** samples.
- Hold **Space** and drag to pan the 2D view; use the mouse wheel to zoom.
- Hold **Alt** while dragging in 3D to navigate instead of painting.
- Click the 3D viewport to focus it, then use **WASD** to move and **Q/E** to move down/up.
  Hold **Shift** for fast movement or **Ctrl** for precise movement. The sidebar movement-speed
  control scales navigation for small equipment or large vehicles.
- Right-drag in the 3D viewport uses the same orbit rotation gesture as the main model viewer.
- Use **[** and **]** to change brush size, **Ctrl+Z/Ctrl+Y** for history, and **Ctrl+S** to save.
- Tool shortcuts are **B** Brush, **E** Eraser, **I** Eyedropper, **G** Fill, **W** Magic Wand,
  **R** Replace, **C** Colorize, and **T** Texture Tint. Use **X** to swap colors and **D** to reset them.
- Fill, MagicWand, Replace, Colorize, and TextureTint use the tolerance and contiguous/global controls.
- **TextureTint** blends toward the foreground color while retaining the source luminance, scratches,
  wear, shading, and alpha. Tint strength controls how much of the original color variation remains.
- Brush spacing controls stroke density independently from brush size.
- Shared interpolation keeps fast pointer movement continuous in both the 2D atlas and 3D projection.
- No-op strokes over an identical color are ignored instead of dirtying the bitmap or history.
- Large fills and recolors run in the background with progress and cancellation; cancelling restores
  every changed tile without adding an undo action.
- Magic Wand selection and native PAA encoding also run in the background, keeping the workspace
  responsive on large atlases. Internal saves are filtered from external live-reload events so the
  mutable 2D/3D paint bindings remain active after saving.
- Undo history is capped at 100 actions or 512 MB and maintains accurate accounting across undo and redo.
- PAA save progress advances per mipmap and cancellation stops before the next compression stage.

**Save all** atomically replaces the project PAAs, creates one original backup under
`paint-backups`, and generates a complete mip chain. Unsaved work is periodically stored under
`.retex/paint-recovery` and offered for recovery when the project entry is reopened. Channel-
swizzled and procedural PAAs remain read-only in this first paint release.

Recovery records are written atomically per texture. Saving or discarding one entry removes only
its own recovery data, so unsaved work from another project entry remains available after switching.

## Requirements

### Running ReTex

| Requirement | Notes |
| --- | --- |
| Windows 10 or 11 x64 | ReTex uses WPF and is Windows-only. |
| Arma 3 and source mods | Select an Arma 3 mod folder or Workshop directory to scan. |
| [PBO Manager](https://github.com/winseros/pboman3) | Required only for **Pack @Mod**. ReTex automatically locates `pboc.exe` in standard per-user and all-users install paths. |
| .NET 8 Desktop Runtime | Required only for framework-dependent builds. Self-contained releases include the runtime. |

An image editor capable of working with PAA files is recommended. BI's TexView 2, included with
Arma 3 Tools on Steam, can convert between PAA and common image formats.

### Building from source

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- NuGet access for the first restore

## Install

1. Download `ReTex-win-x64.zip` from [Releases](../../releases).
2. Extract it to any folder.
3. Run `ReTex.exe`.
4. Install [PBO Manager](https://github.com/winseros/pboman3) if you want ReTex to package PBOs.

The self-contained release does not require a separate .NET installation.

## Quick start

1. Set the mod folder at the top of the window and select **Scan**.
2. Choose a mod and then an asset.
3. Review the texture or, when supported, the in-progress 3D model preview.
4. Select the hidden selections you want to retexture.
5. Enter a project name and choose **Retexture selected asset**.
6. Open **Paint Studio** to edit the copied texture in 2D or directly on the model, then choose **Save all**.
7. Alternatively, use **Edit project** with an external editor; ReTex reloads external saves live.
8. Choose **Pack @Mod** and load the generated `@<project>` folder in Arma 3.

Projects retain their selected classes, source models, textures, materials, and generated class
names, so they can be reopened and regenerated later.

## Build

```powershell
git clone <repository-url>
Set-Location ReTex_App
dotnet build ReTex_App.sln -c Release
dotnet run --project src/ReTex.App/ReTex.App.csproj -c Release
```

Close any running ReTex instance before rebuilding because Windows can lock the current executable
and assemblies.

## Package

Create a self-contained, single-file Windows release:

```powershell
dotnet publish src/ReTex.App/ReTex.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts/ReTex-win-x64
```

Create a smaller framework-dependent release:

```powershell
dotnet publish src/ReTex.App/ReTex.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o artifacts/ReTex-win-x64-fxdep
```

## Project structure

```text
ReTex_App/
  ReTex_App.sln
  src/
    ReTex.Core/
      Assets/                 Asset discovery and inheritance resolution
      Mods/                   Arma mod scanning
      P3d/                    ODOL/MLOD mesh, UV, material, and selection decoding
      Paa/                    PAA texture decoding
      Pbo/                    PBO reading and virtual-path resolution
      Projects/               Project storage, config generation, and packaging
      Rap/                    config.bin and config.cpp parsing
      Tools/                  External tool wrappers
    ReTex.App/
      ViewModels/             Application state and commands
      MainWindow.xaml         Main WPF interface
      PaintWindow.xaml        Separate 2D/3D paint workspace
      ModelViewHelper.cs      WPF 3D model construction and UV visualization
      ArmaConfig.xshd         Config syntax-highlighting rules
  tests/
    ReTex.Core.Tests/         Paint engine, recovery, projection, and PAA tests
    ReTex.App.Tests/          STA/WPF live bitmap and 3D material integration tests
  tools/
    Probe/                    PBO, P3D, config, and pipeline diagnostics
    PaaDump/                  PAA diagnostics
    RapDump/                  Parsed config-tree diagnostics
    WpfRender/                Headless 3D-preview renderer
```

The detailed modern ODOL layout used by the preview is documented in
[`src/ReTex.Core/P3d/ODOL_FORMAT_SPEC.md`](src/ReTex.Core/P3d/ODOL_FORMAT_SPEC.md).

## How it works

1. `ModScanner` discovers Arma mod folders and their PBO files.
2. ReTex reads each PBO directory and extracts only the files it needs.
3. The RAP or C++ parser loads the source config without requiring external extraction tools.
4. `AssetExtractor` resolves classes and inherited hidden selections across the mod's PBOs.
5. The PAA reader builds the 2D preview, and the P3D reader builds a 3D preview when the model format
   is supported.
6. `RetexProjectService` copies selected textures into the project.
7. `ConfigGenerator` creates inheriting classes with updated texture and material paths.
8. **Pack @Mod** invokes `pboc.exe` and writes the final mod folder.

## Third-party components

- [pboman3](https://github.com/winseros/pboman3): PBO packing through `pboc.exe`
- [AvalonEdit](https://github.com/icsharpcode/AvalonEdit): config editor
- [lzo.net](https://github.com/zzattack/lzo.net): LZO support
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet): MVVM framework
- [Helix Toolkit](https://github.com/helix-toolkit/helix-toolkit): WPF 3D viewport
