# ReTex — Arma 3 Retexture Studio

A Windows desktop app for retexturing Arma 3 mods. Browse your installed Workshop
mods, find every retexturable asset (weapons, gear, vehicles, props, glasses), pick
the hidden selections you want to reskin, and ReTex builds a ready-to-load **retexture
mod** for you: it copies the source textures, generates a correct `config.cpp`, and
packs the whole thing into a loadable `@mod` PBO — no manual config writing, no
unpacking PBOs by hand.

![status](https://img.shields.io/badge/platform-Windows-blue) ![net](https://img.shields.io/badge/.NET-8.0-512BD4)

---

## Features

- **Mod & asset browser** — scans your Arma 3 `!Workshop` folder, lists every mod, and
  reads each PBO's config to surface all retexturable classes, grouped by category
  (Equipment, Weapon, Vehicle, Unit, Prop, Backpack, Glasses). Filter by PBO, category,
  or name.
- **Native config parsing** — reads both binarized `config.bin` (rapified) and plain
  `config.cpp`, with no external tools. Resolves `hiddenSelections` /
  `hiddenSelectionsTextures` up the inheritance chain, including across PBOs in the same
  mod.
- **2D texture preview** — decodes `.paa` (DXT1/DXT5 + LZO) and shows the selected
  asset's texture inline.
- **One-click retexture** — pick which hidden selections to reskin (per-selection
  checkboxes), and ReTex copies the source `.paa`(s) into your project. Optional **Copy
  source values** carries over armor/weapon stats and `ItemInfo` so worn gear keeps
  working.
- **Config editor with syntax highlighting** — the generated `config.cpp` is shown in a
  full code editor (keyword/string/comment/number coloring, line numbers, `Ctrl+F`
  find). Edit by hand or regenerate from the project.
- **Project management** — New / Open project, list / remove / **Edit** retextures
  (opens the copied `.paa` in your default image editor for a GIMP/Photoshop round-trip),
  and **Retexture all listed** to batch-add everything currently shown.
- **One-click packing** — **Pack @Mod** writes `mod.cpp` + `addons\main.pbo` into a
  proper `@`-folder you can drop straight into your Arma 3 mods and load.

---

## Requirements

### To run the app

| Requirement | Notes |
|---|---|
| **Windows 10 / 11 (x64)** | The app is WPF; Windows only. |
| **.NET 8 Desktop Runtime** | Only for the *framework-dependent* download. The **self-contained release has the runtime bundled** — nothing to install. Get it at <https://dotnet.microsoft.com/download/dotnet/8.0> (choose *Desktop Runtime x64*). |
| **PBO Manager (pboman3)** | Provides `pboc.exe`, used for the **Pack @Mod** step only. ReTex auto-detects it at `%LOCALAPPDATA%\PBO Manager\pboc.exe`. Get it from <https://github.com/winseros/pboman3>. Browsing, previewing, copying textures, and config generation all work **without** it — you only need it to pack the final PBO. |
| **Arma 3 + Workshop mods** | Needed to have something to retexture. Point ReTex at your `…\Steam\steamapps\common\Arma 3\!Workshop` folder. |

### Optional

| Tool | Why |
|---|---|
| **GIMP** or **Photoshop** with a **PAA plugin** | To actually paint the copied `.paa` textures. The **Edit** button opens them in your default `.paa` handler. PAA plugins: [Photoshop](https://community.bistudio.com/wiki/PAA_File_Format) tools / GIMP DDS + PAA conversion, or BI's **TexView 2** (part of Arma 3 Tools on Steam) to convert PAA↔PNG. |
| **Mikero's Tools (AIO)** | The all-in-one modding suite — **DeRap** (binarized `config.bin` → readable `config.cpp`), **DePbo / ExtractPbo**, **MakePbo / pboProject**, etc. Useful for manually inspecting a mod's config or unpacking PBOs when you want to dig in beyond what ReTex shows. ReTex parses configs natively and packs via `pboc`, so this is **not required**, but it's the standard toolkit for Arma config/PBO work. Download (AIO installer): <https://mikero.bytex.digital/Downloads>. |
| **Arma 3 Tools** (Steam) | TexView 2, Addon Builder, etc. — handy but not required by ReTex. |

### To build from source

| Requirement | Notes |
|---|---|
| **.NET 8 SDK** | <https://dotnet.microsoft.com/download/dotnet/8.0>. Includes everything needed to `build`/`publish`. |
| NuGet packages | Restored automatically on build: **CommunityToolkit.Mvvm** (MVVM), **AvalonEdit** (config editor), **lzo.net** (PAA decompression). |

---

## Download & run (release)

1. Download the latest `ReTex-win-x64.zip` from [Releases](../../releases).
2. Unzip anywhere and run **`ReTex.exe`**.
3. (For packing) install [PBO Manager](https://github.com/winseros/pboman3) if you
   haven't — ReTex finds `pboc.exe` automatically.

The self-contained build needs **no .NET install**.

---

## Quick start

1. **Set your mods folder** — paste your `…\Arma 3\!Workshop` path at the top and click
   **Scan** (it's remembered between sessions).
2. **Pick a mod**, then **pick an asset** from the list. Its texture previews on the right.
3. Tick the **selections** you want to retexture and set a **project name**.
4. Click **Retexture selected asset →**. ReTex copies the source `.paa`(s) and generates
   the config.
5. Click **Edit** to open the copied texture in your image editor, paint it, and save in
   place.
6. Click **Pack @Mod**. Load the resulting `@<project>` folder in Arma 3 — your retexture
   appears in Arsenal / the editor.

---

## Build from source

```sh
git clone <this-repo>
cd ReTex_App

# build everything
dotnet build ReTex_App.sln -c Release

# run the app
dotnet run --project src/ReTex.App -c Release
```

### Produce a release build

Self-contained, single-file, no .NET install required on the target machine:

```sh
dotnet publish src/ReTex.App/ReTex.App.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o artifacts/ReTex-win-x64
```

Framework-dependent (smaller; requires the .NET 8 Desktop Runtime on the target):

```sh
dotnet publish src/ReTex.App/ReTex.App.csproj -c Release -r win-x64 ^
  --self-contained false -o artifacts/ReTex-win-x64-fxdep
```

> **Note:** close any running instance of ReTex before building — the running app locks
> `ReTex.exe` / `ReTex.Core.dll` in `bin\Debug`, which produces `MSB3027` / `MSB3021`
> *copy* errors. Those are file-lock errors, not compile errors.

---

## Project layout

```
ReTex_App/
  ReTex_App.sln
  src/
    ReTex.Core/                     UI-agnostic logic (no WPF deps; testable)
      Pbo/PboArchive.cs             parse PBO header; list + extract entries (no full unpack)
      Pbo/VirtualFileService.cs     resolve a virtual texture path to bytes across a mod's PBOs
      Mods/ModScanner.cs            discover @-mods under a folder (follows Workshop junctions)
      Rap/RapReader.cs              native binarized config.bin (rapified) parser
      Rap/CppConfigParser.cs        plain-text config.cpp parser
      Rap/RapWriter.cs              serialize a class body (for the copied-values feature)
      Assets/AssetExtractor.cs      find retexturable classes + resolve inheritance
      Assets/AssetService.cs        load assets for a whole mod (global cross-PBO index)
      Paa/PaaImage.cs               decode .paa (DXT1/5 + LZO) for preview
      Projects/RetexProjectService  create project, add retexture, copy .paa, pack
      Projects/ConfigGenerator.cs   emit config.cpp (new classes, textures, requiredAddons)
      Tools/PboTool.cs              pboc.exe (pboman3) wrapper for pack/unpack
      P3d/OdolReader.cs             ODOL v73 header/skeleton parse (3D preview — paused)
    ReTex.App/                      WPF front-end (MVVM, CommunityToolkit.Mvvm)
      MainWindow.xaml               Mods | Assets | Detail+preview+project+config editor
      ArmaConfig.xshd               AvalonEdit syntax grammar for config.cpp
      AvalonEditBehaviour.cs        two-way text binding for the editor
      ViewModels/MainViewModel.cs
  tools/                            dev/diagnostic console apps (not shipped)
    Probe/                          end-to-end pipeline validator + PBO/config diagnostics
    RapDump/                        dump a parsed config tree
    PaaDump/                        dump PAA header/decode info
```

---

## How it works

1. **Scan** discovers `@`-mods under your Workshop folder (Workshop entries are NTFS
   junctions; the .NET directory APIs follow them).
2. For each PBO, ReTex reads the header and extracts the config (`config.bin` or
   `config.cpp`) **without unpacking** the whole archive, then parses it natively.
3. `AssetExtractor` walks `CfgWeapons` / `CfgVehicles` / `CfgGlasses`, resolving
   `hiddenSelections` up the inheritance chain (across all of the mod's PBOs) so variants
   that only override textures are still caught.
4. On **Retexture**, the chosen source `.paa`(s) are copied into the project's
   `textures/` folder, and `ConfigGenerator` emits new classes that inherit the original,
   repoint `hiddenSelectionsTextures[]` at the copies, and declare the right
   `requiredAddons` (the source mod's `CfgPatches` addon).
5. **Pack @Mod** runs `pboc` to build `addons\main.pbo`, alongside a generated `mod.cpp`,
   producing a loadable `@`-folder.

---

## Known gotchas

- **`pboc` won't overwrite** an existing output PBO (it exits 0 but silently skips);
  `PboTool` deletes the target first so a repack always takes effect.
- **No `$PBOPREFIX$`?** Arma falls back to the **PBO file name** as the prefix; ReTex does
  the same when resolving textures.
- **Forward-slash entry paths.** Some packers store PBO entries with `/` instead of `\`;
  ReTex normalizes separators so those PBOs are detected and their textures copy.
- **Extensionless texture refs.** Many mods (especially vehicles) write
  `hiddenSelectionsTextures` without the `.paa` extension; ReTex resolves the `.paa` form
  automatically.
- **Weapons in Arsenal.** A retextured weapon must point `baseWeapon` at *itself* or
  Arsenal hides it as a sub-variant; ReTex emits the self-reference for you.

---

## Roadmap

- **3D model preview** — paused. `OdolReader` parses the ODOL v73 header/ModelInfo/
  skeleton (validated), but full mesh extraction needs ODOL de-binarization (no clean v73
  spec; existing tools are MLOD-only or unautomatable). Kept as a foundation.
- **Modlist import** — import an Arma launcher preset (`.html`) instead of a folder scan.
- **In-project warnings** — flag retextures whose source texture failed to copy.

---

## Credits / third-party

- [pboman3](https://github.com/winseros/pboman3) — `pboc` PBO pack/unpack CLI.
- [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) — config editor.
- [lzo.net](https://github.com/zzattack/lzo.net) — LZO decompression for PAA.
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM.
