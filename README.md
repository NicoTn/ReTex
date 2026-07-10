# ReTex - Arma 3 Retexture Studio

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
- **3D model preview**: reads modern ODOL v73/v75 and MLOD models, maps materials and hidden
  selections, and displays project textures directly on the model.
- **UV inspection tools**: identify model parts, locate clicked geometry on the texture, display a
  UV grid, and temporarily adjust UV orientation for diagnostics.
- **Live texture reload**: watches the selected project entry's PAA files and updates the existing
  2D image and 3D material in place when an external editor saves or replaces them. The model,
  geometry, UVs, and camera are not rebuilt.
- **Project management**: create or open projects, add individual assets or batches, duplicate and
  remove entries, and open copied textures in the configured image editor.
- **Config editor**: edit generated `config.cpp` with syntax highlighting, line numbers, and search.
- **One-click packaging**: generates `mod.cpp` and packs `addons\main.pbo` into a ready-to-load
  Arma 3 mod folder.

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
3. Review the texture or 3D model preview.
4. Select the hidden selections you want to retexture.
5. Enter a project name and choose **Retexture selected asset**.
6. Use **Edit project** to open the copied texture, make your changes, and save it in place.
7. ReTex detects the save and swaps the 3D material immediately without rebuilding the model.
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
      ModelViewHelper.cs      WPF 3D model construction and UV visualization
      ArmaConfig.xshd         Config syntax-highlighting rules
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
5. The PAA and P3D readers build the 2D and 3D previews.
6. `RetexProjectService` copies selected textures into the project.
7. `ConfigGenerator` creates inheriting classes with updated texture and material paths.
8. **Pack @Mod** invokes `pboc.exe` and writes the final mod folder.

## Third-party components

- [pboman3](https://github.com/winseros/pboman3): PBO packing through `pboc.exe`
- [AvalonEdit](https://github.com/icsharpcode/AvalonEdit): config editor
- [lzo.net](https://github.com/zzattack/lzo.net): LZO support
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet): MVVM framework
- [Helix Toolkit](https://github.com/helix-toolkit/helix-toolkit): WPF 3D viewport
