using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReTex.Core.Assets;
using ReTex.Core.Mods;
using ReTex.Core.P3d;
using ReTex.Core.Paa;
using ReTex.Core.Pbo;
using ReTex.Core.Projects;
using ReTex.Core.Rap;
using ReTex.Core.Tools;

namespace ReTex.App.ViewModels;

/// <summary>Status-line severity, drives the status bar colour.</summary>
public enum StatusSeverity { Info, Warn, Error }

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();

    [ObservableProperty] private string _workshopPath = "";
    [ObservableProperty] private string _status = "Pick a mod and scan.";
    [ObservableProperty] private StatusSeverity _statusSeverity = StatusSeverity.Info;

    // --- Mods ---
    public ObservableCollection<ArmaMod> Mods { get; } = new();
    private List<ArmaMod> _allMods = new();
    [ObservableProperty] private string _modSearch = "";
    [ObservableProperty] private ArmaMod? _selectedMod;

    // --- Assets ---
    public ObservableCollection<AssetInfo> Assets { get; } = new();
    private List<AssetInfo> _allAssets = new();
    [ObservableProperty] private AssetInfo? _selectedAsset;
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private string _previewInfo = "";
    /// <summary>"2 / 3" when the current context has multiple previewable textures; "" for 0/1 (drives
    /// the prev/next arrow visibility). See <see cref="ShowPreviewSlot"/>.</summary>
    [ObservableProperty] private string _previewCounter = "";
    [ObservableProperty] private Model3DGroup? _previewModel3D;
    [ObservableProperty] private string _previewModelInfo = "";
    /// <summary>Texture path of the model part currently under the cursor (3D hover-to-identify).</summary>
    [ObservableProperty] private string _hoverTexture = "";
    /// <summary>Multi-line breakdown of every texture group + rvmat the model uses (info tooltip).</summary>
    [ObservableProperty] private string _previewGroupsSummary = "";
    /// <summary>Maps each rendered 3D part to a human label (texture + rvmat) for the hover hit-test.
    /// Set by <see cref="LoadPreviewModel"/>; read by the window's MouseMove handler.</summary>
    public IReadOnlyDictionary<GeometryModel3D, string>? PreviewPartLabels { get; private set; }

    // --- 3D preview UV debug transform (flip/rotate/shift the texture live to find the mapping that
    // matches the in-game look). Not persisted; a diagnostic aid, resets to identity. ---
    [ObservableProperty] private bool _uvFlipU;
    [ObservableProperty] private bool _uvFlipV;
    /// <summary>Rotation in 90° steps (0..3).</summary>
    [ObservableProperty] private int _uvRot90;
    [ObservableProperty] private double _uvOffsetU;
    [ObservableProperty] private double _uvOffsetV;
    /// <summary>Live readout of the current transform (so it can be reported back / baked in).</summary>
    [ObservableProperty] private string _uvXformSummary = "identity";
    // Last args passed to LoadPreviewModel, so the debug controls can rebuild the same model/selections.
    private string? _lastPreviewModel;
    private IReadOnlyList<RetexSelection>? _lastPreviewSelections;
    // Cache of the last fully-loaded preview (parsed mesh + groups + memoised textures) so a UV-debug
    // tweak only rebuilds the cheap geometry instead of re-extracting/parsing the .p3d and re-decoding
    // the PAAs. Populated by LoadPreviewModel; consumed by RebuildPreviewFast.
    private OdolLodMesh? _cachedMesh;
    private IReadOnlyList<OdolPreviewGroup>? _cachedGroups;
    private Func<PreviewTexture, BitmapSource?>? _cachedTexLoader;
    private Dictionary<string, BitmapSource?>? _cachedTextureBitmaps;
    private IReadOnlyDictionary<string, IReadOnlyList<ImageBrush>>? _cachedTextureBrushes;
    private int _previewModelLoadVersion;
    // Diagnostic mode: dress the whole model in a labelled UV coordinate grid so texture placement can be
    // checked region-by-region against the game. Built lazily on the UI thread; frozen for cross-thread use.
    [ObservableProperty] private bool _diagnosticGrid;
    private BitmapSource? _diagGridBmp;
    private BitmapSource DiagGrid => _diagGridBmp ??= ModelViewHelper.BuildCoordinateGrid(1024, 10);
    partial void OnDiagnosticGridChanged(bool value) => RebuildPreviewFast();
    /// <summary>Set by a live UV-debug rebuild so the view keeps the current camera instead of
    /// re-fitting (ZoomExtents) — the model is the same, only its texture mapping changed.</summary>
    public bool PreserveCameraOnNextPreview { get; set; }
    /// <summary>The parsed mesh of the current preview (for the 3D→texture pick tool); null if none.</summary>
    internal OdolLodMesh? CachedMesh => _cachedMesh;
    /// <summary>Human-readable result of the last 3D→texture pick (Ctrl+click), e.g. the UV + pixel.</summary>
    [ObservableProperty] private string _pickInfo = "";
    /// <summary>A glowing overlay of the faces that sample a spot picked on the 2D texture (reverse
    /// pick) so the user can see which body part a texture region maps to. Null = nothing highlighted.</summary>
    [ObservableProperty] private Model3DGroup? _highlightModel3D;

    /// <summary>Reverse pick: highlight, on the 3D model, the geometry that samples texture (u,v).
    /// Returns true if any faces matched.</summary>
    public bool HighlightUv(double u, double v)
    {
        if (_cachedMesh is null) return false;
        // Grow the radius until something is hit (sparse regions need a wider net), capped so it stays local.
        foreach (var r in new[] { 0.012, 0.025, 0.05 })
        {
            var hl = ModelViewHelper.BuildUvHighlight(_cachedMesh, u, v, r, CurrentUvXform);
            if (hl != null) { HighlightModel3D = hl; return true; }
        }
        HighlightModel3D = null;
        return false;
    }

    partial void OnUvFlipUChanged(bool value) => ApplyUvXform();
    partial void OnUvFlipVChanged(bool value) => ApplyUvXform();
    partial void OnUvRot90Changed(int value) => ApplyUvXform();
    partial void OnUvOffsetUChanged(double value) => ApplyUvXform();
    partial void OnUvOffsetVChanged(double value) => ApplyUvXform();

    private UvXform CurrentUvXform =>
        new(UvFlipU, UvFlipV, UvRot90, UvOffsetU, UvOffsetV);

    private void ApplyUvXform()
    {
        var x = CurrentUvXform;
        UvXformSummary = x.IsIdentity ? "identity"
            : $"rot {UvRot90 * 90}°{(UvFlipU ? " flipU" : "")}{(UvFlipV ? " flipV" : "")} off ({UvOffsetU:+0.00;-0.00;0.00}, {UvOffsetV:+0.00;-0.00;0.00})";
        if (!_suppressUvRebuild) RebuildPreviewFast();
    }

    /// <summary>Re-applies the current UV-debug transform to the already-loaded model without touching
    /// disk: reuses the cached mesh/groups/decoded-textures and only rebuilds geometry (fast enough to
    /// feel live while dragging a slider). Falls back to a full load if nothing is cached yet.</summary>
    private void RebuildPreviewFast()
    {
        if (_cachedMesh is null || _cachedGroups is null || _cachedTexLoader is null)
        {
            _ = LoadPreviewModel(_lastPreviewModel, _lastPreviewSelections);
            return;
        }
        try
        {
            var loader = DiagnosticGrid ? (_ => DiagGrid) : _cachedTexLoader;
            var frozen = ModelViewHelper.Build(_cachedMesh, _cachedGroups, loader, CurrentUvXform);
            var built = frozen is null ? null : ModelViewHelper.ActivateLiveTextures(frozen.Model, _cachedGroups);
            PreserveCameraOnNextPreview = true;   // same model — don't re-fit the camera on a UV tweak
            PreviewModel3D = built?.Model;
            PreviewPartLabels = built?.Parts.ToDictionary(kv => kv.Key, kv => DescribePart(kv.Value.Texture, _cachedMesh));
            _cachedTextureBrushes = built?.ProjectTextureBrushes;
        }
        catch (Exception ex) { PreviewModelInfo = $"(3D preview failed: {ex.Message})"; }
    }

    /// <summary>Exports the current model's UV layout as a transparent PNG (one colour per texture
    /// group) so the user can load it as a guide layer over their texture and paint decals onto the
    /// correct island. Saved next to the project's textures (or temp) and revealed in Explorer.</summary>
    [RelayCommand]
    private void ExportUvMap()
    {
        if (_cachedMesh is null || _cachedGroups is null)
        {
            SetStatus("Load a model in the 3D preview first (select an asset or entry).", StatusSeverity.Warn);
            return;
        }
        try
        {
            // Match the texture resolution when known so the guide lines up 1:1 with the atlas.
            int size = PreviewImage is { PixelWidth: > 0 } bmp ? Math.Clamp(bmp.PixelWidth, 512, 4096) : 2048;
            var uvMap = ModelViewHelper.RenderUvMap(_cachedMesh, _cachedGroups, size, CurrentUvXform);

            var modelName = Path.GetFileNameWithoutExtension((_lastPreviewModel ?? "model").Replace('\\', '/'));
            var dir = _project?.TexturesDir is { Length: > 0 } td && Directory.Exists(td)
                ? td : Path.Combine(Path.GetTempPath(), "ReTex", "uvmaps");
            Directory.CreateDirectory(dir);
            var outPath = Path.Combine(dir, $"{modelName}_uvmap.png");

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(uvMap));
            using (var fs = File.Create(outPath)) enc.Save(fs);

            SetStatus($"UV map exported: {outPath} — open it as a layer over your texture to align decals.", StatusSeverity.Info);
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{outPath}\"")); } catch { /* non-fatal */ }
        }
        catch (Exception ex) { SetStatus($"UV-map export failed: {ex.Message}", StatusSeverity.Error); }
    }

    /// <summary>Saves the labelled UV coordinate grid as a PNG. Convert it to .paa and wear it in-game
    /// (Arsenal), or feed it through "Open in viewer": comparing which grid cell lands on which body part
    /// in the game vs the preview proves whether the preview's texture placement is faithful.</summary>
    [RelayCommand]
    private void ExportDiagnosticGrid()
    {
        try
        {
            var dir = _project?.TexturesDir is { Length: > 0 } td && Directory.Exists(td)
                ? td : Path.Combine(Path.GetTempPath(), "ReTex", "uvmaps");
            Directory.CreateDirectory(dir);
            var outPath = Path.Combine(dir, "uv_coordinate_grid.png");
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(DiagGrid));
            using (var fs = File.Create(outPath)) enc.Save(fs);
            SetStatus($"UV grid exported: {outPath} — wear it in-game (convert to .paa) and compare which cell lands where vs the preview.", StatusSeverity.Info);
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{outPath}\"")); } catch { /* non-fatal */ }
        }
        catch (Exception ex) { SetStatus($"UV-grid export failed: {ex.Message}", StatusSeverity.Error); }
    }

    /// <summary>Extracts the current preview model plus its textures — with the user's retextures
    /// written at the model's baked texture paths — into a virtual-path mirror under %TEMP%, so an
    /// external viewer (P3D Analyzer / Object Builder) renders the RETEXTURED model. Returns the
    /// extracted .p3d path, or null on failure.</summary>
    public string? ExportModelForViewer()
    {
        if (_cachedMesh is null || _cachedGroups is null || string.IsNullOrWhiteSpace(_lastPreviewModel))
        {
            SetStatus("Load a model in the 3D preview first.", StatusSeverity.Warn);
            return null;
        }
        var allPbos = Mods.SelectMany(m => m.PboPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var selPbos = SelectedMod?.PboPaths.ToList() ?? new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "ReTex", "viewer");
        try
        {
            string? WriteVirtual(string virtualPath, byte[] bytes)
            {
                var rel = virtualPath.Replace('/', '\\').TrimStart('\\');
                var dest = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, bytes);
                return dest;
            }

            var modelBytes = ExtractAcrossMods(allPbos, selPbos, _lastPreviewModel!);
            if (modelBytes is null) { SetStatus("Could not extract the model .p3d.", StatusSeverity.Error); return null; }
            var modelPath = WriteVirtual(_lastPreviewModel!, modelBytes);

            // Each group's texture, written at the model's baked path. Retextured selections use the
            // user's painted .paa so the viewer shows the actual retexture.
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in _cachedGroups)
            {
                var src = g.Texture.SourceVirtualPath;
                if (string.IsNullOrWhiteSpace(src) || src.StartsWith("#")) continue; // skip procedural colours
                if (!written.Add(src)) continue;
                if (g.Texture.ProjectFilePath is { Length: > 0 } proj && File.Exists(proj))
                    WriteVirtual(src, File.ReadAllBytes(proj));
                else if (ExtractAcrossMods(allPbos, selPbos, src) is { } tb)
                    WriteVirtual(src, tb);
            }
            // Also drop any remaining textures the model references (normal/spec maps etc.) so it's complete.
            foreach (var t in _cachedMesh.Textures)
            {
                if (string.IsNullOrWhiteSpace(t) || t.StartsWith("#") || !written.Add(t)) continue;
                if (ExtractAcrossMods(allPbos, selPbos, t) is { } tb) WriteVirtual(t, tb);
            }
            return modelPath;
        }
        catch (Exception ex) { SetStatus($"Export for viewer failed: {ex.Message}", StatusSeverity.Error); return null; }
    }

    [RelayCommand] private void UvRotate() => UvRot90 = (UvRot90 + 1) % 4;
    [RelayCommand] private void UvResetXform()
    {
        _suppressUvRebuild = true;
        UvFlipU = false; UvFlipV = false; UvRot90 = 0; UvOffsetU = UvOffsetV = 0;
        _suppressUvRebuild = false;
        ApplyUvXform();
    }
    private bool _suppressUvRebuild;

    [ObservableProperty] private string _assetSearch = "";
    public ObservableCollection<SelectionChoice> AssetSelections { get; } = new();
    [ObservableProperty] private string _categoryFilter = "All";
    public string[] Categories { get; } = { "All", "Equipment", "Weapon", "Vehicle", "Unit", "Prop", "Backpack", "Glasses" };

    private const string AllPbos = "All PBOs";
    public ObservableCollection<string> PboNames { get; } = new() { AllPbos };
    [ObservableProperty] private string _pboFilter = AllPbos;

    // --- Project ---
    [ObservableProperty] private string _projectsRoot = "";
    [ObservableProperty] private string _projectName = "MyRetex";
    [ObservableProperty] private string _configText = "";
    [ObservableProperty] private bool _copySourceValues = true;
    [ObservableProperty] private RetexEntry? _selectedEntry;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private string _currentProjectName = "(no project)";
    [ObservableProperty] private string _currentProjectPath = "";
    public ObservableCollection<RetexEntry> ProjectEntries { get; } = new();
    /// <summary>Recent project files (full path to retex.json), newest first.</summary>
    public ObservableCollection<string> RecentProjects { get; } = new();
    /// <summary>Bound to the "Recent" combo; selecting an item opens it then resets to null so the same
    /// item can be picked again.</summary>
    [ObservableProperty] private string? _selectedRecent;
    private RetexProject? _project;

    // --- Form editor (structured alternative to hand-editing config.cpp) ---
    [ObservableProperty] private string _formDisplayName = "";
    [ObservableProperty] private string _formAuthor = "";
    [ObservableProperty] private string _formPrefix = "";
    [ObservableProperty] private string _formRequiredAddons = "";
    /// <summary>Bottom-right editor tab: 0 = form editor, 1 = config.cpp.</summary>
    [ObservableProperty] private int _editorTabIndex;
    /// <summary>Per-selection rows for the currently selected entry (Browse-to-assign texture).</summary>
    public ObservableCollection<SelectionRowViewModel> FormSelections { get; } = new();
    // True while the form fields are being repopulated from the model, so the commit handlers don't
    // fire (and regenerate) in response to our own programmatic assignments.
    private bool _suppressFormCommit;
    private int _prevEditorTab;

    // --- Tooling status ---
    [ObservableProperty] private bool _pbocReady;
    [ObservableProperty] private string _pbocTooltip = "";

    // Live reload for project PAAs. The watcher covers the addon's directory, while the exact-path
    // set limits refreshes to textures used by the currently selected entry.
    private FileSystemWatcher? _texWatcher;
    private readonly DispatcherTimer _texDebounce;
    private readonly object _textureWatchGate = new();
    private readonly HashSet<string> _watchedTexturePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingTexturePaths = new(StringComparer.OrdinalIgnoreCase);
    private int _textureWatchVersion;
    private bool _disposed;
    [ObservableProperty] private string _textureWatchStatus = "";

    public MainViewModel()
    {
        WorkshopPath = _settings.WorkshopPath;
        ProjectsRoot = _settings.ProjectsRoot;
        LoadRecents();
        RefreshPbocStatus();
        _texDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _texDebounce.Tick += async (_, _) =>
        {
            _texDebounce.Stop();
            await RefreshChangedTexturesAsync();
        };
        _prevEditorTab = Math.Clamp(_settings.LastEditorTab, 0, 1);
        EditorTabIndex = _prevEditorTab;
        if (Directory.Exists(WorkshopPath)) ScanWorkshop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _texDebounce.Stop();
        _texWatcher?.Dispose();
        _texWatcher = null;
    }

    /// <summary>Sets the status line text and severity in one call.</summary>
    internal void SetStatus(string message, StatusSeverity severity = StatusSeverity.Info)
    {
        Status = message;
        StatusSeverity = severity;
    }

    // ---------------------------------------------------------------- first-run / settings

    /// <summary>
    /// Called once the main window is loaded. If the workshop folder can't be found and the
    /// user hasn't already been through Settings, pop it up automatically so they can point
    /// ReTex at the right folder instead of silently showing an empty mod list.
    /// </summary>
    public void CheckFirstRunSetup(Window owner)
    {
        if (_settings.SetupCompleted) return;
        if (Directory.Exists(WorkshopPath)) return;
        OpenSettings(owner, isFirstRun: true);
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettings(Application.Current.MainWindow, isFirstRun: false);

    private void OpenSettings(Window? owner, bool isFirstRun)
    {
        var window = new SettingsWindow(_settings, isFirstRun) { Owner = owner };
        if (window.ShowDialog() != true) return;

        WorkshopPath = _settings.WorkshopPath;
        ProjectsRoot = _settings.ProjectsRoot;
        RefreshPbocStatus();
        if (Directory.Exists(WorkshopPath)) ScanWorkshop();
    }

    [RelayCommand]
    private void ShowAbout()
    {
        var window = new AboutWindow { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    partial void OnWorkshopPathChanged(string value)
    {
        _settings.WorkshopPath = value;
        _settings.Save();
    }

    partial void OnProjectsRootChanged(string value)
    {
        _settings.ProjectsRoot = value;
        _settings.Save();
    }

    /// <summary>Recomputes whether pboc.exe is available (used for the up-front toolbar indicator).</summary>
    private void RefreshPbocStatus()
    {
        var pboc = File.Exists(_settings.PbocPath) ? _settings.PbocPath : PboTool.FindDefault();
        PbocReady = pboc is not null;
        PbocTooltip = PbocReady
            ? $"pboc.exe ready — {pboc}"
            : "pboc.exe not found. Install PBO Manager or set its path in Settings (needed only for \"Pack @Mod\").";
    }

    // ---------------------------------------------------------------- scanning / assets

    [RelayCommand]
    private void ScanWorkshop()
    {
        Assets.Clear();
        try
        {
            _allMods = ModScanner.ScanFolder(WorkshopPath);
            ApplyModFilter();
            SetStatus($"{_allMods.Count} mods in {WorkshopPath}");
        }
        catch (Exception ex) { SetStatus($"Scan failed: {ex.Message}", StatusSeverity.Error); }
    }

    partial void OnModSearchChanged(string value) => ApplyModFilter();

    private void ApplyModFilter()
    {
        Mods.Clear();
        var s = ModSearch.Trim();
        IEnumerable<ArmaMod> view = _allMods;
        if (s.Length > 0)
            view = view.Where(m => m.DisplayName.Contains(s, StringComparison.OrdinalIgnoreCase)
                                || m.Name.Contains(s, StringComparison.OrdinalIgnoreCase));
        foreach (var m in view) Mods.Add(m);
    }

    partial void OnSelectedModChanged(ArmaMod? value)
    {
        if (value is not null) LoadAssetsCommand.Execute(null);
    }

    [RelayCommand]
    private async Task LoadAssets()
    {
        Assets.Clear();
        _allAssets = new();
        var mod = SelectedMod;
        if (mod is null) return;

        SetStatus($"Loading assets from {mod.DisplayName}...");
        _allAssets = await Task.Run(() => AssetService.LoadForMod(mod));

        // Rebuild the PBO (sub-mod) list from the loaded assets.
        PboNames.Clear();
        PboNames.Add(AllPbos);
        foreach (var name in _allAssets.Select(a => Path.GetFileName(a.SourcePbo))
                     .Where(n => n.Length > 0).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            PboNames.Add(name);
        PboFilter = AllPbos;

        ApplyAssetFilter();
        SetStatus($"{mod.DisplayName}: {_allAssets.Count} retexturable assets in {PboNames.Count - 1} PBOs");
    }

    partial void OnAssetSearchChanged(string value) => ApplyAssetFilter();
    partial void OnCategoryFilterChanged(string value) => ApplyAssetFilter();
    partial void OnPboFilterChanged(string value) => ApplyAssetFilter();

    partial void OnSelectedAssetChanged(AssetInfo? value)
    {
        LoadPreviewCommand.Execute(null);
        _ = LoadPreviewModel(value?.Model, null); // browse flow: source model, default textures

        AssetSelections.Clear();
        if (value is null) return;
        int n = Math.Max(value.HiddenSelections.Count, value.HiddenSelectionsTextures.Count);
        for (int i = 0; i < n; i++)
        {
            var name = i < value.HiddenSelections.Count && value.HiddenSelections[i].Length > 0
                ? value.HiddenSelections[i]
                : (i < value.HiddenSelectionsTextures.Count
                    ? Path.GetFileName(value.HiddenSelectionsTextures[i].Replace('\\', '/'))
                    : $"selection {i}");
            AssetSelections.Add(new SelectionChoice { Index = i, Name = name });
        }
    }

    // The set of textures the 2D preview can cycle through for the current selection (an asset's
    // hidden-selection textures, or a project entry's per-selection textures) + the current index.
    private enum PreviewKind { SourceVirtual, ProjectFile }
    private sealed record PreviewSlot(string Label, PreviewKind Kind, string Path);
    private List<PreviewSlot> _previewSlots = new();
    private int _previewIndex;
    private int _previewImageLoadVersion;

    [RelayCommand]
    private void LoadPreview()
    {
        var asset = SelectedAsset;
        if (asset is null) { _previewSlots = new(); _ = ShowPreviewSlot(0); return; }
        // One slot per hidden-selection texture; label it with the selection name when known.
        var slots = new List<PreviewSlot>();
        for (int i = 0; i < asset.HiddenSelectionsTextures.Count; i++)
        {
            var t = asset.HiddenSelectionsTextures[i];
            if (t.Length == 0) continue;
            string label = i < asset.HiddenSelections.Count && asset.HiddenSelections[i].Length > 0
                ? asset.HiddenSelections[i] : Path.GetFileName(t.Replace('\\', '/'));
            slots.Add(new PreviewSlot(label, PreviewKind.SourceVirtual, t));
        }
        _previewSlots = slots;
        _ = ShowPreviewSlot(0);
    }

    [RelayCommand]
    private void PreviewPrev() => _ = ShowPreviewSlot(_previewIndex - 1);

    [RelayCommand]
    private void PreviewNext() => _ = ShowPreviewSlot(_previewIndex + 1);

    /// <summary>Shows the Nth previewable texture for the current selection (wrapping), loading its
    /// pixels off the UI thread. Source textures are extracted from the scanned mods; project textures
    /// are read from disk. Updates <see cref="PreviewCounter"/> so the arrows appear only when >1.</summary>
    private async Task ShowPreviewSlot(int index)
    {
        int loadVersion = Interlocked.Increment(ref _previewImageLoadVersion);
        if (_previewSlots.Count == 0)
        {
            _previewIndex = 0;
            PreviewImage = null;
            PreviewInfo = SelectedAsset is not null || SelectedEntry is not null ? "(no texture)" : "";
            PreviewCounter = "";
            return;
        }
        int n = _previewSlots.Count;
        index = ((index % n) + n) % n;
        _previewIndex = index;
        PreviewCounter = n > 1 ? $"{index + 1} / {n}" : "";
        var slot = _previewSlots[index];

        var allPbos = Mods.SelectMany(m => m.PboPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var selPbos = SelectedMod?.PboPaths.ToList() ?? new List<string>();
        try
        {
            var img = await Task.Run(() =>
            {
                if (slot.Kind == PreviewKind.ProjectFile)
                    return File.Exists(slot.Path) ? PaaImage.LoadFile(slot.Path) : null;
                var bytes = ExtractAcrossMods(allPbos, selPbos, slot.Path);
                return bytes is null ? null : PaaImage.Load(bytes);
            });
            if (loadVersion != _previewImageLoadVersion) return;
            if (img is null) { PreviewImage = null; PreviewInfo = $"({slot.Label}: texture not found)"; return; }
            PreviewImage = ImageHelper.ToBitmap(img);
            string tag = slot.Kind == PreviewKind.ProjectFile ? "project" : "source";
            PreviewInfo = $"{slot.Label}  {img.Width}x{img.Height}  ({tag})";
        }
        catch (Exception ex)
        {
            if (loadVersion != _previewImageLoadVersion) return;
            PreviewImage = null;
            PreviewInfo = $"(preview failed: {ex.Message})";
        }
    }

    private void ApplyAssetFilter()
    {
        Assets.Clear();
        IEnumerable<AssetInfo> view = _allAssets;

        if (CategoryFilter != "All" && Enum.TryParse<AssetCategory>(CategoryFilter, out var cat))
            view = view.Where(a => a.Category == cat);

        if (PboFilter != AllPbos)
            view = view.Where(a => Path.GetFileName(a.SourcePbo).Equals(PboFilter, StringComparison.OrdinalIgnoreCase));

        var s = AssetSearch.Trim();
        if (s.Length > 0)
            view = view.Where(a => a.Label.Contains(s, StringComparison.OrdinalIgnoreCase));

        foreach (var a in view.OrderBy(a => a.Category).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase))
            Assets.Add(a);
    }

    // ---------------------------------------------------------------- projects

    /// <summary>Points the view model at a project: updates the header, MRU and texture watcher.</summary>
    private void SetProject(RetexProject proj)
    {
        SelectedEntry = null;
        _project = proj;
        CurrentProjectName = proj.Name;
        CurrentProjectPath = proj.ProjectFilePath;
        _settings.PushRecentProject(proj.ProjectFilePath);
        _settings.Save();
        LoadRecents();
        SetupTextureWatcher();
        RebuildProjectForm();
    }

    /// <summary>Loads the MRU list from settings, dropping projects whose file no longer exists.</summary>
    private void LoadRecents()
    {
        _settings.PruneRecentProjects();
        _settings.Save();
        RecentProjects.Clear();
        foreach (var p in _settings.RecentProjects) RecentProjects.Add(p);
    }

    [RelayCommand]
    private void RetextureSelected()
    {
        if (SelectedAsset is null || SelectedMod is null) { SetStatus("Select an asset first.", StatusSeverity.Warn); return; }

        var indices = AssetSelections.Where(s => s.Selected).Select(s => s.Index).ToList();
        if (AssetSelections.Count > 0 && indices.Count == 0) { SetStatus("Tick at least one selection to retexture.", StatusSeverity.Warn); return; }
        IReadOnlyCollection<int>? chosen = AssetSelections.Count > 0 ? indices : null;

        try
        {
            PreserveManualEdits();
            if (_project is null) SetProject(CreateNewProject());
            var entry = RetexProjectService.AddRetexture(_project!, SelectedAsset, SelectedMod.PboPaths, indices: chosen, copyValues: CopySourceValues, modAssets: _allAssets);
            RetexProjectService.GenerateConfig(_project!);
            RefreshEntries();
            ConfigText = File.ReadAllText(_project!.ConfigPath);
            var copied = entry.Selections.Count(s => s.ProjectTexture.Length > 0);
            SetStatus($"Added {entry.NewClassName} ({copied} texture(s) copied). Project: {_project.ProjectDir}");
        }
        catch (Exception ex) { SetStatus($"Retexture failed: {ex.Message}", StatusSeverity.Error); }
    }

    /// <summary>Creates a project under ProjectsRoot using the configured author + prefix template.</summary>
    private RetexProject CreateNewProject()
    {
        var prefix = RetexProjectService.ExpandPrefix(_settings.DefaultPrefixTemplate, ProjectName);
        return RetexProjectService.CreateProject(ProjectsRoot, ProjectName, prefix, _settings.DefaultAuthor);
    }

    [RelayCommand]
    private void NewProject()
    {
        var proj = CreateNewProject();
        RetexProjectService.GenerateConfig(proj);
        SetProject(proj);
        RefreshEntries();
        ConfigText = File.ReadAllText(proj.ConfigPath);
        SetStatus($"New project: {proj.ProjectDir}");
    }

    [RelayCommand]
    private void OpenProject()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ReTex project (retex.json)|retex.json",
            InitialDirectory = Directory.Exists(ProjectsRoot) ? ProjectsRoot : null,
        };
        if (dlg.ShowDialog() != true) return;
        OpenProjectFile(dlg.FileName);
    }

    partial void OnSelectedRecentChanged(string? value)
    {
        if (value is null) return;
        var path = value;
        SelectedRecent = null;      // reset so re-picking the same entry fires again
        OpenRecent(path);
    }

    /// <summary>Opens a project from the recents list.</summary>
    [RelayCommand]
    private void OpenRecent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            SetStatus("That project file no longer exists; removing it from recents.", StatusSeverity.Warn);
            _settings.RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            LoadRecents();
            return;
        }
        OpenProjectFile(path);
    }

    private void OpenProjectFile(string projectFilePath)
    {
        try
        {
            var proj = RetexProject.Load(projectFilePath);
            ProjectName = proj.Name;
            ProjectsRoot = Path.GetDirectoryName(proj.ProjectDir) ?? ProjectsRoot;
            SetProject(proj);
            ConfigText = File.Exists(proj.ConfigPath) ? File.ReadAllText(proj.ConfigPath) : "";
            // Sync the model to the config.cpp on disk BEFORE listing entries, so hand-edited
            // displayNames (and copied class values) show correctly and aren't silently reverted by
            // the next regenerate. Without this, the model holds the stale retex.json names while
            // config.cpp holds the edits, and generation clobbers the edits. Best-effort (see
            // RetexProjectService.PreserveManualEdits).
            PreserveManualEdits();
            RefreshEntries();
            SetStatus($"Opened {proj.Name} ({proj.Entries.Count} retextures)");
            WarnIfMissingTextures(proj);
        }
        catch (Exception ex) { SetStatus($"Open failed: {ex.Message}", StatusSeverity.Error); }
    }

    /// <summary>
    /// Surfaces entries whose ProjectTexture no longer exists on disk - config.cpp would still
    /// reference the path and the PBO would pack "successfully" with that retexture silently
    /// missing in-game (see RetexProjectService.FindMissingTextures).
    /// </summary>
    private void WarnIfMissingTextures(RetexProject proj)
    {
        var missing = RetexProjectService.FindMissingTextures(proj);
        if (missing.Count == 0) return;
        SetStatus($"{Status}  ⚠ {missing.Count} texture reference(s) point at missing files: {string.Join("; ", missing)}", StatusSeverity.Warn);
    }

    partial void OnSelectedEntryChanged(RetexEntry? value)
    {
        RenameText = value?.NewClassName ?? "";
        RebuildEntryForm(value);
        UpdateWatchedTexturePaths(value);
        if (_project is null || value is null) { _previewSlots = new(); _ = ShowPreviewSlot(0); return; }
        // One previewable slot per selection: the retextured project .paa when present, else the source
        // texture. The prev/next arrows then cycle through every selection of this retexture.
        var slots = new List<PreviewSlot>();
        foreach (var s in value.Selections.OrderBy(s => s.Index))
        {
            if (s.ProjectTexture.Length > 0)
                slots.Add(new PreviewSlot(s.Name.Length > 0 ? s.Name : s.ProjectTexture,
                    PreviewKind.ProjectFile, Path.Combine(_project.AddonDir, s.ProjectTexture)));
            else if (s.SourceTexture.Length > 0)
                slots.Add(new PreviewSlot(s.Name.Length > 0 ? s.Name : Path.GetFileName(s.SourceTexture.Replace('\\', '/')),
                    PreviewKind.SourceVirtual, s.SourceTexture));
        }
        _previewSlots = slots;
        _ = ShowPreviewSlot(0);
        _ = LoadPreviewModel(PreviewModelFor(value), value.Selections); // project flow: retexture applied
    }

    /// <summary>Picks the model to preview a retexture against. A uniform is a pair (a CfgWeapons item
    /// + its worn CfgVehicles unit); the item's own model is a ground/inventory model (e.g. Grav_U.p3d)
    /// whose UV layout differs from the worn body (e.g. Grav_U_Inceptor.p3d), so previewing the
    /// retexture on it maps the texture onto the wrong mesh and looks scrambled. Among the pair's two
    /// models, choose the one whose file name best matches the retexture's source texture (the worn
    /// body's baked texture shares its stem, e.g. Grav_U_Inceptor.p3d ← Grav_U_Inceptor_UM_co.paa).
    /// This corrects both new projects and older ones whose stored models predate the fix.</summary>
    private string PreviewModelFor(RetexEntry entry)
    {
        var candidates = new List<string>();
        if (entry.SourceModel.Length > 0) candidates.Add(entry.SourceModel);
        if ((entry.IsUniform || entry.IsUniformUnit) && entry.PartnerClass.Length > 0 && _project is not null)
        {
            var partner = _project.Entries.FirstOrDefault(e =>
                e.NewClassName.Equals(entry.PartnerClass, StringComparison.OrdinalIgnoreCase));
            if (partner is not null && partner.SourceModel.Length > 0) candidates.Add(partner.SourceModel);
        }
        candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count <= 1) return entry.SourceModel;

        string texStem = Path.GetFileNameWithoutExtension(
            (entry.Selections.FirstOrDefault(s => s.SourceTexture.Length > 0)?.SourceTexture ?? "")
                .Replace('\\', '/')).ToLowerInvariant();
        return candidates
            .OrderByDescending(m => CommonPrefixLength(Path.GetFileNameWithoutExtension(m).ToLowerInvariant(), texStem))
            .ThenByDescending(m => m.Length)
            .First();
    }

    private static int CommonPrefixLength(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    // ---------------------------------------------------------------- form editor

    /// <summary>Repopulates the per-entry form fields (display name + selection rows) from the model.</summary>
    private void RebuildEntryForm(RetexEntry? entry)
    {
        _suppressFormCommit = true;
        FormDisplayName = entry?.DisplayName ?? "";
        FormSelections.Clear();
        if (entry is not null)
            foreach (var s in entry.Selections.OrderBy(s => s.Index))
                FormSelections.Add(new SelectionRowViewModel(s, BrowseSelectionTexture));
        _suppressFormCommit = false;
    }

    /// <summary>Repopulates the project-level form fields (author / prefix / requiredAddons) from the model.</summary>
    private void RebuildProjectForm()
    {
        _suppressFormCommit = true;
        FormAuthor = _project?.Author ?? "";
        FormPrefix = _project?.Prefix ?? "";
        FormRequiredAddons = _project is null ? "" : string.Join(Environment.NewLine, _project.RequiredAddons);
        _suppressFormCommit = false;
    }

    partial void OnEditorTabIndexChanged(int value)
    {
        // Leaving the config.cpp tab (index 1): fold recognized manual text edits back into the
        // model, then refresh the form so it reflects them (the chosen "auto-fold on tab switch").
        // We deliberately DON'T rewrite ConfigText here — the user's text (incl. comments) stays until
        // an explicit Regenerate or a form edit rebuilds it, so tabbing over is non-destructive.
        if (_prevEditorTab == 1 && value != 1 && _project is not null)
        {
            PreserveManualEdits();
            RebuildEntryForm(SelectedEntry);
            RebuildProjectForm();
        }
        _prevEditorTab = value;
        _settings.LastEditorTab = value;
        _settings.Save();
    }

    partial void OnFormDisplayNameChanged(string value)
    {
        if (_suppressFormCommit || _project is null || SelectedEntry is null) return;
        if (value == SelectedEntry.DisplayName) return;
        SelectedEntry.DisplayName = value;
        if (value.Trim().Length == 0) SetStatus("Display name is blank — the item may be unnamed in Arsenal.", StatusSeverity.Warn);
        RegenerateAndRefresh(refreshList: false);
    }

    partial void OnFormAuthorChanged(string value)
    {
        if (_suppressFormCommit || _project is null) return;
        if (value == _project.Author) return;
        _project.Author = value;
        RegenerateAndRefresh(refreshList: false);
    }

    partial void OnFormPrefixChanged(string value)
    {
        if (_suppressFormCommit || _project is null) return;
        var v = value.Trim();
        if (v.Length == 0 || v == _project.Prefix) return;
        _project.Prefix = v;   // changes the in-game texture virtual paths; regenerate re-points them
        RegenerateAndRefresh(refreshList: false);
    }

    partial void OnFormRequiredAddonsChanged(string value)
    {
        if (_suppressFormCommit || _project is null) return;
        var list = value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (list.SequenceEqual(_project.RequiredAddons)) return;
        _project.RequiredAddons = list;
        RegenerateAndRefresh(refreshList: false);
    }

    /// <summary>Assigns an external .paa (chosen via a file dialog) to a selection, copying it into the
    /// project's textures folder and re-pointing the config + preview.</summary>
    private void BrowseSelectionTexture(SelectionRowViewModel row)
    {
        if (_project is null) { SetStatus("Open or create a project first.", StatusSeverity.Warn); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose a .paa texture for '{row.Name}'",
            Filter = "Arma texture (*.paa)|*.paa|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(_project.TexturesDir) ? _project.TexturesDir : null,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var projRel = RetexProjectService.ImportTexture(_project, dlg.FileName);
            row.Selection.ProjectTexture = projRel;
            RegenerateAndRefresh(refreshList: true);
            SetStatus($"Assigned {Path.GetFileName(dlg.FileName)} to '{row.Name}'.");
        }
        catch (Exception ex) { SetStatus($"Couldn't assign texture: {ex.Message}", StatusSeverity.Error); }
    }

    /// <summary>Regenerates config.cpp from the (form-edited) model and refreshes the editor text.
    /// Preserves the current entry selection; only rebuilds the left list when its rows may have
    /// changed (class name / texture assignments), not for pure value edits.</summary>
    private void RegenerateAndRefresh(bool refreshList)
    {
        if (_project is null) return;
        RetexProjectService.GenerateConfig(_project);
        ConfigText = File.ReadAllText(_project.ConfigPath);
        if (!refreshList) return;
        var keep = SelectedEntry;
        RefreshEntries();
        if (keep is not null)
            SelectedEntry = _project.Entries.FirstOrDefault(e => ReferenceEquals(e, keep));
    }

    /// <summary>Builds the 3D model preview off the UI thread: extracts the .p3d from the source
    /// mod's PBOs, decodes it (<see cref="OdolLodReader.ReadVisualLod"/>), maps sections to resolved
    /// textures (<see cref="OdolMeshPreview"/>), and builds a frozen <see cref="Model3DGroup"/>.
    /// The model is resolved across ALL scanned mods (not just the selected one) so a project entry
    /// previews from its own source mod without the user having to re-select it. Falls back with an
    /// explanatory message when the model can't be extracted or decoded, leaving the flat tab.</summary>
    private async Task LoadPreviewModel(string? modelVirtualPath, IReadOnlyList<RetexSelection>? selections,
        bool preserveCamera = false)
    {
        int loadVersion = Interlocked.Increment(ref _previewModelLoadVersion);
        _lastPreviewModel = modelVirtualPath;      // remembered so the UV-debug controls can rebuild
        _lastPreviewSelections = selections;
        var uvXform = CurrentUvXform;
        _cachedMesh = null; _cachedGroups = null; _cachedTexLoader = null;
        _cachedTextureBitmaps = null; _cachedTextureBrushes = null;          // invalidate live-reload cache
        HighlightModel3D = null; PickInfo = "";                              // clear stale pick/highlight
        PreviewModel3D = null;
        PreviewModelInfo = "";
        HoverTexture = "";
        PreviewGroupsSummary = "";
        PreviewPartLabels = null;
        if (string.IsNullOrWhiteSpace(modelVirtualPath)) { PreviewModelInfo = "(no source model for this asset)"; return; }

        // Snapshot PBO paths on the UI thread (Mods is an ObservableCollection - not thread-safe).
        var allPbos = Mods.SelectMany(m => m.PboPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var selPbos = SelectedMod?.PboPaths.ToList() ?? new List<string>();
        if (allPbos.Count == 0) { PreviewModelInfo = "(scan a mod folder to preview the model)"; return; }
        string? addonDir = _project?.AddonDir;
        // Memoising texture loader: decode each PAA once and reuse it, so a later fast rebuild (UV debug)
        // pays no decode cost. Kept as the cache's loader for RebuildPreviewFast.
        var texCache = new Dictionary<string, BitmapSource?>();
        BitmapSource? Loader(PreviewTexture tex)
        {
            var key = TextureCacheKey(tex);
            if (!texCache.TryGetValue(key, out var b)) { b = LoadPreviewTexture(tex, allPbos, selPbos); texCache[key] = b; }
            return b;
        }
        // Pre-build the diagnostic grid on the UI thread (RenderTargetBitmap needs STA) so the background
        // build can just reference the frozen bitmap.
        bool diagGridOn = DiagnosticGrid;
        var diagBmp = diagGridOn ? DiagGrid : null;
        Func<PreviewTexture, BitmapSource?> effectiveLoader = diagGridOn ? (_ => diagBmp) : Loader;
        try
        {
            var (model, info, labels, summary, mesh, groups) = await Task.Run(() =>
            {
                var bytes = ExtractAcrossMods(allPbos, selPbos, modelVirtualPath!);
                if (bytes is null) return ((Model3DGroup?)null, "(source model not found — is its mod installed and scanned?)", (IReadOnlyDictionary<GeometryModel3D, string>?)null, "", (OdolLodMesh?)null, (IReadOnlyList<OdolPreviewGroup>?)null);
                var m = OdolLodReader.ReadAnyVisualLod(bytes);
                if (m is null) return (null, "(3D preview unavailable for this model — its .p3d format/UV layout isn't fully supported yet; use the Texture tab)", null, "", null, null);
                var g = OdolMeshPreview.BuildGroups(m, selections, addonDir);
                var built = ModelViewHelper.Build(m, g, effectiveLoader, uvXform);
                string name = Path.GetFileName(modelVirtualPath!.Replace('\\', '/'));
                var infoStr = $"{name}  {m.Points.Length} verts · {m.Faces.Count} faces · {g.Count} texture group(s)  ⓘ hover a part";

                // Per-part hover labels + a full groups/materials breakdown for the info tooltip.
                var lbl = built is null ? null : built.Parts.ToDictionary(kv => kv.Key, kv => DescribePart(kv.Value.Texture, m));
                var sum = DescribeModel(m, g);
                return (built?.Model, infoStr, (IReadOnlyDictionary<GeometryModel3D, string>?)lbl, sum, m, (IReadOnlyList<OdolPreviewGroup>?)g);
            });
            if (loadVersion != _previewModelLoadVersion) return;
            if (model is not null && groups is not null)
            {
                var live = ModelViewHelper.ActivateLiveTextures(model, groups);
                model = live.Model;
                labels = live.Parts.ToDictionary(kv => kv.Key, kv => DescribePart(kv.Value.Texture, mesh!));
                _cachedTextureBrushes = live.ProjectTextureBrushes;
            }
            if (preserveCamera) PreserveCameraOnNextPreview = true;
            PreviewModel3D = model;
            PreviewModelInfo = info;
            PreviewPartLabels = labels;
            PreviewGroupsSummary = summary;
            // Cache for live UV-debug rebuilds (only when a real model loaded).
            if (mesh is not null && groups is not null && model is not null)
            {
                _cachedMesh = mesh; _cachedGroups = groups; _cachedTexLoader = Loader;
                _cachedTextureBitmaps = texCache;
            }
        }
        catch (Exception ex)
        {
            if (loadVersion == _previewModelLoadVersion)
                PreviewModelInfo = $"(3D preview failed: {ex.Message})";
        }
    }

    private static string TextureCacheKey(PreviewTexture tex)
    {
        string projectPath = tex.ProjectFilePath ?? "";
        if (projectPath.Length > 0)
        {
            try { projectPath = Path.GetFullPath(projectPath); }
            catch { /* keep the original path as the cache identity */ }
        }
        return projectPath + "|" + tex.SourceVirtualPath;
    }

    /// <summary>One-line label for a hovered model part: its texture (retextured or source) plus the
    /// rvmat material that applies it, when the model uses one.</summary>
    private static string DescribePart(PreviewTexture tex, OdolLodMesh mesh)
    {
        string line = tex.SourceVirtualPath.Length > 0 ? tex.SourceVirtualPath : "(no texture)";
        if (tex.IsRetextured) line += "  (retextured)";
        var mat = MaterialForTexture(mesh, tex.SourceVirtualPath);
        return mat.Length > 0 ? $"{line}\nvia material: {mat}" : line;
    }

    /// <summary>Multi-line breakdown of every texture group + every rvmat the model references.</summary>
    private static string DescribeModel(OdolLodMesh mesh, IReadOnlyList<OdolPreviewGroup> groups)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Textures on this model:");
        foreach (var g in groups)
        {
            var t = g.Texture;
            string label = t.IsRetextured ? $"{t.SourceVirtualPath}  (retextured)"
                          : t.SourceVirtualPath.Length > 0 ? t.SourceVirtualPath : "(untextured)";
            var mat = MaterialForTexture(mesh, t.SourceVirtualPath);
            sb.AppendLine(mat.Length > 0 ? $"  • {label}   [material: {mat}]" : $"  • {label}");
        }
        var mats = mesh.Materials.Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mats.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Materials (rvmat) used:");
            foreach (var m in mats) sb.AppendLine($"  • {m}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Best-effort: finds the rvmat of the section whose base texture matches the given path
    /// (so the hover can say a part's texture is applied via that material). "" if none/unknown.</summary>
    private static string MaterialForTexture(OdolLodMesh mesh, string texturePath)
    {
        if (texturePath.Length == 0) return "";
        string norm = texturePath.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        foreach (var s in mesh.Sections)
        {
            string t = s.CommonTextureIndex >= 0 && s.CommonTextureIndex < mesh.Textures.Count ? mesh.Textures[s.CommonTextureIndex] : "";
            if (t.Replace('/', '\\').TrimStart('\\').ToLowerInvariant() != norm) continue;
            if (s.MaterialIndex >= 0 && s.MaterialIndex < mesh.Materials.Count) return mesh.Materials[s.MaterialIndex];
        }
        return "";
    }

    /// <summary>Extracts a virtual path from the scanned mods, preferring the PBOs most likely to
    /// hold it so the common case is fast: (1) PBOs whose file name matches the path's first segment
    /// (the BI "$PBOPREFIX$ == addon name" convention), then (2) the selected mod's PBOs, then
    /// (3) everything else as a last resort. <see cref="VirtualFileService.Extract"/> stops at the
    /// first match, so the tail is only ever opened on a genuine miss.</summary>
    private static byte[]? ExtractAcrossMods(List<string> allPbos, List<string> selPbos, string virtualPath)
    {
        string first = virtualPath.Replace('/', '\\').TrimStart('\\').Split('\\')[0];
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<string> ps) { foreach (var p in ps) if (seen.Add(p)) ordered.Add(p); }
        Add(allPbos.Where(p => Path.GetFileNameWithoutExtension(p).Equals(first, StringComparison.OrdinalIgnoreCase)));
        Add(selPbos);
        Add(allPbos);
        return VirtualFileService.Extract(ordered, virtualPath);
    }

    /// <summary>Loads the pixels for a resolved preview texture: a project .paa from disk when the
    /// section was retextured, else the model's default texture extracted from the source mods
    /// (resolved across all scanned mods, since a model's textures often live in a different mod).</summary>
    private static BitmapSource? LoadPreviewTexture(PreviewTexture tex, List<string> allPbos, List<string> selPbos)
    {
        try
        {
            if (tex.ProjectFilePath != null && File.Exists(tex.ProjectFilePath))
                return ImageHelper.ToBitmap(PaaImage.LoadFile(tex.ProjectFilePath));
            if (tex.SourceVirtualPath.Length > 0)
            {
                var bytes = ExtractAcrossMods(allPbos, selPbos, tex.SourceVirtualPath);
                if (bytes != null) return ImageHelper.ToBitmap(PaaImage.Load(bytes));
            }
        }
        catch { /* fall back to flat colour in ModelViewHelper */ }
        return null;
    }


    /// <summary>Extracts the current context's SOURCE texture(s) to temp .paa files and opens them in
    /// the default image editor — for referencing the originals or grabbing parts of them while
    /// retexturing. Uses the selected asset's textures when browsing, else the selected entry's source
    /// textures. Copies live under %TEMP%\ReTex\source so the real PBO/project is never touched.</summary>
    [RelayCommand]
    private void OpenSourceTextures()
    {
        var texs = (SelectedAsset is not null
                ? SelectedAsset.HiddenSelectionsTextures
                : SelectedEntry?.Selections.Select(s => s.SourceTexture) ?? Enumerable.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (texs.Count == 0) { SetStatus("No source texture to open for the current selection.", StatusSeverity.Warn); return; }

        var allPbos = Mods.SelectMany(m => m.PboPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var selPbos = SelectedMod?.PboPaths.ToList() ?? new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "ReTex", "source");
        try { Directory.CreateDirectory(dir); } catch (Exception ex) { SetStatus($"Couldn't create temp folder: {ex.Message}", StatusSeverity.Error); return; }

        int opened = 0;
        foreach (var t in texs)
        {
            try
            {
                var bytes = ExtractAcrossMods(allPbos, selPbos, t);
                if (bytes is null) continue;
                var name = Path.GetFileName(t.Replace('\\', '/'));
                if (Path.GetExtension(name).Length == 0) name += ".paa";
                var path = Path.Combine(dir, name);
                File.WriteAllBytes(path, bytes);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                opened++;
            }
            catch { /* skip this one, keep going */ }
        }
        SetStatus(opened > 0
            ? $"Opened {opened} source texture(s) in your editor (temp copies in {dir})."
            : "Couldn't extract the source texture(s) — is the source mod scanned?",
            opened > 0 ? StatusSeverity.Info : StatusSeverity.Warn);
    }

    /// <summary>De-rapifies the selected asset's source mod config (config.bin) to readable text and
    /// opens it in the default editor as a read-only reference (temp copy). Runs off the UI thread
    /// since some mod configs are large. Falls back to a text config.cpp if the mod ships one.</summary>
    [RelayCommand]
    private async Task OpenSourceConfig()
    {
        var pbo = SelectedAsset?.SourcePbo;
        if (string.IsNullOrEmpty(pbo) || !File.Exists(pbo))
        {
            SetStatus("Select an asset first — its source PBO holds the original config.", StatusSeverity.Warn);
            return;
        }
        SetStatus("Reading source config…");
        try
        {
            var (outPath, reason) = await Task.Run<(string?, string)>(() =>
            {
                // config.bin (rapified) is the norm; a few mods ship a text config.cpp instead.
                var bytes = VirtualFileService.Extract(new[] { pbo }, "config.bin")
                         ?? VirtualFileService.Extract(new[] { pbo }, "config.cpp");
                if (bytes is null) return (null, "not found (or compressed — would need pboc)");

                string text = RapReader.IsRapified(bytes)
                    ? $"// De-rapified from {Path.GetFileName(pbo)} by ReTex — read-only reference.\r\n\r\n"
                      + RapWriter.WriteBody(RapReader.Parse(bytes), indent: 0)
                    : System.Text.Encoding.UTF8.GetString(bytes);

                var dir = Path.Combine(Path.GetTempPath(), "ReTex", "source");
                Directory.CreateDirectory(dir);
                var op = Path.Combine(dir, Path.GetFileNameWithoutExtension(pbo) + ".config.cpp");
                File.WriteAllText(op, text);
                return (op, "");
            });

            if (outPath is null) { SetStatus($"Couldn't read the source config — {reason}.", StatusSeverity.Warn); return; }
            Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true });
            SetStatus($"Opened original config from {Path.GetFileName(pbo)} (read-only temp copy).");
        }
        catch (Exception ex) { SetStatus($"Couldn't open source config: {ex.Message}", StatusSeverity.Error); }
    }

    /// <summary>Re-runs the preview for whatever is currently selected without resetting the current
    /// texture slot. The watcher uses the same path but preserves the 3D camera.</summary>
    [RelayCommand]
    private void RefreshPreview() => RefreshPreviewCore(preserveCamera: false, changedPaths: null);

    private void RefreshPreviewCore(bool preserveCamera, IReadOnlyCollection<string>? changedPaths)
    {
        if (SelectedEntry is not null)
        {
            _ = ShowPreviewSlot(_previewIndex);
            _ = LoadPreviewModel(PreviewModelFor(SelectedEntry), SelectedEntry.Selections, preserveCamera);
        }
        else if (SelectedAsset is not null)
        {
            _ = ShowPreviewSlot(_previewIndex);
            _ = LoadPreviewModel(SelectedAsset.Model, null, preserveCamera);
        }

        if (changedPaths is null)
            SetStatus("Preview refreshed.");
        else if (changedPaths.Count > 0)
            SetStatus($"Live preview updated: {string.Join(", ", changedPaths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase))}");
    }

    /// <summary>Watches the open addon's PAA files. Events are filtered against the selected entry's
    /// exact project-texture paths, so unrelated project saves do not rebuild the current model.</summary>
    private void SetupTextureWatcher()
    {
        _texWatcher?.Dispose();
        _texWatcher = null;
        if (_project is null || _disposed) { UpdateWatchedTexturePaths(null); return; }
        try
        {
            Directory.CreateDirectory(_project.AddonDir);
            var w = new FileSystemWatcher(_project.AddonDir, "*.paa")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024,
            };
            void OnChanged(object? _, FileSystemEventArgs e) => OnTextureFileChanged(e.FullPath);
            w.Changed += OnChanged;
            w.Created += OnChanged;
            w.Deleted += OnChanged;
            w.Renamed += (_, e) =>
            {
                OnTextureFileChanged(e.OldFullPath);
                OnTextureFileChanged(e.FullPath);
            };
            w.Error += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(SetupTextureWatcher));
            _texWatcher = w;
            w.EnableRaisingEvents = true;
        }
        catch
        {
            _texWatcher?.Dispose();
            _texWatcher = null;
            TextureWatchStatus = "Live reload unavailable";
            return;
        }
        UpdateWatchedTexturePaths(SelectedEntry);
    }

    private void UpdateWatchedTexturePaths(RetexEntry? entry)
    {
        var paths = new List<string>();
        if (_project is not null && entry is not null)
        {
            string addonRoot = Path.GetFullPath(_project.AddonDir).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (var selection in entry.Selections)
            {
                if (string.IsNullOrWhiteSpace(selection.ProjectTexture)) continue;
                string fullPath = Path.GetFullPath(Path.Combine(_project.AddonDir, selection.ProjectTexture));
                if (fullPath.StartsWith(addonRoot, StringComparison.OrdinalIgnoreCase)) paths.Add(fullPath);
            }
        }

        lock (_textureWatchGate)
        {
            _watchedTexturePaths.Clear();
            foreach (string path in paths) _watchedTexturePaths.Add(path);
            _pendingTexturePaths.Clear();
            _textureWatchVersion++;
        }
        int count = paths.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        TextureWatchStatus = count == 0
            ? ""
            : $"Live reload: {count} PAA{(count == 1 ? "" : "s")}";
    }

    private void OnTextureFileChanged(string path)
    {
        if (_disposed) return;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return; }

        lock (_textureWatchGate)
        {
            if (!_watchedTexturePaths.Contains(fullPath)) return;
            _pendingTexturePaths.Add(fullPath);
        }

        // Editors commonly emit several write/rename events. Restart one UI-thread timer so only the
        // completed save rebuilds the previews.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _texDebounce.Stop();
            _texDebounce.Start();
        });
    }

    private async Task RefreshChangedTexturesAsync()
    {
        string[] changed;
        int watchVersion;
        lock (_textureWatchGate)
        {
            changed = _pendingTexturePaths.Where(_watchedTexturePaths.Contains).ToArray();
            _pendingTexturePaths.Clear();
            watchVersion = _textureWatchVersion;
        }
        if (changed.Length == 0 || _disposed) return;

        await WaitForStableTextureFilesAsync(changed);

        lock (_textureWatchGate)
        {
            if (watchVersion != _textureWatchVersion || _disposed) return;
            changed = changed.Where(_watchedTexturePaths.Contains).ToArray();
        }
        if (changed.Length > 0) await ApplyLiveTextureUpdatesAsync(changed, watchVersion);
    }

    private async Task ApplyLiveTextureUpdatesAsync(IReadOnlyCollection<string> changedPaths, int watchVersion)
    {
        var updates = await Task.Run(() =>
        {
            var result = new Dictionary<string, (bool Success, BitmapSource? Bitmap, string Error)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in changedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                {
                    result[path] = (true, null, "");
                    continue;
                }
                try { result[path] = (true, ImageHelper.ToBitmap(PaaImage.LoadFile(path)), ""); }
                catch (Exception ex) { result[path] = (false, null, ex.Message); }
            }
            return result;
        });

        lock (_textureWatchGate)
        {
            if (watchVersion != _textureWatchVersion || _disposed) return;
        }

        var applied = new List<string>();
        var failures = new List<string>();
        foreach (var (path, update) in updates)
        {
            if (!update.Success)
            {
                failures.Add($"{Path.GetFileName(path)}: {update.Error}");
                continue; // keep the last valid image when an editor leaves an invalid intermediate file
            }

            BitmapSource displayBitmap = update.Bitmap ?? ModelViewHelper.MissingTextureBitmap;
            if (_cachedTextureBitmaps is not null)
            {
                foreach (string key in _cachedTextureBitmaps.Keys.ToArray())
                {
                    int separator = key.IndexOf('|');
                    string cachedPath = separator >= 0 ? key[..separator] : key;
                    if (cachedPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                        _cachedTextureBitmaps[key] = update.Bitmap;
                }
            }

            // Updating ImageSource invalidates only the material. Geometry, UVs, model groups, camera,
            // hover mappings, and selection highlights remain exactly as they are.
            if (!DiagnosticGrid && _cachedTextureBrushes is not null
                && _cachedTextureBrushes.TryGetValue(path, out var brushes))
                foreach (var brush in brushes) brush.ImageSource = displayBitmap;

            if (_previewSlots.Count > 0 && _previewIndex >= 0 && _previewIndex < _previewSlots.Count)
            {
                var slot = _previewSlots[_previewIndex];
                if (slot.Kind == PreviewKind.ProjectFile && PathsEqual(slot.Path, path))
                {
                    Interlocked.Increment(ref _previewImageLoadVersion); // cancel an older asynchronous load
                    PreviewImage = update.Bitmap;
                    PreviewInfo = update.Bitmap is null
                        ? $"({slot.Label}: texture not found)"
                        : $"{slot.Label}  {update.Bitmap.PixelWidth}x{update.Bitmap.PixelHeight}  (project, live)";
                }
            }
            applied.Add(path);
        }

        if (failures.Count > 0)
            SetStatus($"Live reload kept the previous texture: {string.Join("; ", failures)}", StatusSeverity.Warn);
        else if (applied.Count > 0)
            SetStatus($"Live texture updated: {string.Join(", ", applied.Select(Path.GetFileName))}");
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return left.Equals(right, StringComparison.OrdinalIgnoreCase); }
    }

    private readonly record struct TextureFileState(bool Exists, bool Readable, long Length, long LastWriteTicks);

    private static async Task WaitForStableTextureFilesAsync(IReadOnlyCollection<string> paths)
    {
        static TextureFileState State(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return new(false, true, 0, 0);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return new(true, stream.Length > 0, stream.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch { return new(true, false, -1, -1); }
        }

        // Atomic-save editors replace the file, while others write it in place. Wait until two
        // consecutive snapshots agree and every existing file can be opened before decoding.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var before = paths.Select(State).ToArray();
            await Task.Delay(125);
            var after = paths.Select(State).ToArray();
            if (before.SequenceEqual(after) && after.All(s => !s.Exists || s.Readable)) return;
        }
    }

    /// <summary>Opens the selected retexture's copied .paa(s) in the OS default editor (GIMP/Photoshop with a PAA plugin).</summary>
    [RelayCommand]
    private void EditEntryTextures()
    {
        if (_project is null || SelectedEntry is null) { SetStatus("Select a retexture in the project first.", StatusSeverity.Warn); return; }
        var files = SelectedEntry.Selections
            .Where(s => s.ProjectTexture.Length > 0)
            .Select(s => Path.Combine(_project.AddonDir, s.ProjectTexture))
            .Where(File.Exists).Distinct().ToList();
        if (files.Count == 0) { SetStatus("This retexture has no copied textures.", StatusSeverity.Warn); return; }
        foreach (var f in files)
        {
            try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus($"Open failed: {ex.Message}", StatusSeverity.Error); return; }
        }
        SetStatus($"Opened {files.Count} texture(s) in your default .paa editor. Save in place — the preview refreshes automatically.");
    }

    /// <summary>Adds every currently-listed (filtered) asset to the project in one go.</summary>
    [RelayCommand]
    private async Task RetextureAllListed()
    {
        if (SelectedMod is null) { SetStatus("Pick a mod first.", StatusSeverity.Warn); return; }
        if (Assets.Count == 0) { SetStatus("No assets listed.", StatusSeverity.Warn); return; }

        var mod = SelectedMod;
        var toAdd = Assets.ToList();
        PreserveManualEdits();
        if (_project is null) SetProject(CreateNewProject());
        var proj = _project!;

        SetStatus($"Batch retexturing {toAdd.Count} assets…");
        int n = await Task.Run(() =>
        {
            int c = 0;
            foreach (var a in toAdd) { RetexProjectService.AddRetexture(proj, a, mod.PboPaths, copyValues: CopySourceValues, modAssets: _allAssets); c++; }
            RetexProjectService.GenerateConfig(proj);
            return c;
        });
        RefreshEntries();
        ConfigText = File.ReadAllText(proj.ConfigPath);
        SetStatus($"Batch: added {n} retextures to {proj.Name}.");
    }

    [RelayCommand]
    private void RemoveEntry()
    {
        if (_project is null || SelectedEntry is null) return;
        var name = SelectedEntry.NewClassName;
        bool pair = SelectedEntry.PartnerClass.Length > 0 && (SelectedEntry.IsUniform || SelectedEntry.IsUniformUnit);
        var prompt = pair
            ? $"Remove retexture '{name}' and its paired uniform unit?"
            : $"Remove retexture '{name}'?";
        if (MessageBox.Show(prompt, "Remove retexture", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        PreserveManualEdits();
        // A uniform is two cross-linked entries (item + clothing unit); remove both halves together.
        if (pair)
            _project.Entries.RemoveAll(e =>
                e.NewClassName.Equals(SelectedEntry.PartnerClass, StringComparison.OrdinalIgnoreCase));
        _project.Entries.Remove(SelectedEntry);
        RetexProjectService.GenerateConfig(_project);
        RefreshEntries();
        ConfigText = File.ReadAllText(_project.ConfigPath);
        SetStatus($"Removed {name}.");
    }

    /// <summary>Renames the selected entry's generated class (RenameText). The config is regenerated
    /// so the new class name is reflected everywhere it's referenced.</summary>
    [RelayCommand]
    private void RenameEntry()
    {
        if (_project is null || SelectedEntry is null) { SetStatus("Select a retexture to rename.", StatusSeverity.Warn); return; }
        var entry = SelectedEntry;
        var desired = (RenameText ?? "").Trim();
        if (desired.Length == 0) { SetStatus("Enter a new class name.", StatusSeverity.Warn); return; }
        if (desired.Equals(entry.NewClassName, StringComparison.Ordinal)) return;

        PreserveManualEdits(); // capture edits against the CURRENT class name before it changes

        // Collision with a DIFFERENT entry? Refuse rather than silently mangling the name.
        if (_project.Entries.Any(e => e != entry && e.NewClassName.Equals(desired, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"A retexture named '{desired}' already exists.", StatusSeverity.Warn);
            return;
        }

        var oldName = entry.NewClassName;
        entry.NewClassName = RetexProjectService.MakeUniqueClassName(_project, desired);
        // Keep a uniform pair's back-reference in sync: the partner unit points at us by class name.
        foreach (var e in _project.Entries)
            if (e != entry && e.PartnerClass.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                e.PartnerClass = entry.NewClassName;
        RetexProjectService.GenerateConfig(_project);
        RefreshEntries();
        SelectedEntry = _project.Entries.FirstOrDefault(e => e.NewClassName.Equals(entry.NewClassName, StringComparison.Ordinal));
        ConfigText = File.ReadAllText(_project.ConfigPath);
        SetStatus($"Renamed to {entry.NewClassName}.");
    }

    /// <summary>Duplicates the selected entry (new unique class name, reusing its already-copied
    /// texture files). A uniform is a pair (CfgWeapons item + CfgVehicles clothing unit); duplicating
    /// either half clones BOTH and re-establishes their reciprocal <c>uniformClass</c> cross-links.</summary>
    [RelayCommand]
    private void DuplicateEntry()
    {
        if (_project is null || SelectedEntry is null) { SetStatus("Select a retexture to duplicate.", StatusSeverity.Warn); return; }
        var src = SelectedEntry;
        PreserveManualEdits(); // so the clone inherits the source entry's current edits

        // Uniform pair: clone both halves and cross-link the clones to each other (not the originals).
        if (src.IsUniform || src.IsUniformUnit)
        {
            var partner = _project.Entries.FirstOrDefault(e =>
                e.NewClassName.Equals(src.PartnerClass, StringComparison.OrdinalIgnoreCase));
            if (partner is not null)
            {
                var cloneA = CloneEntry(src, RetexProjectService.MakeUniqueClassName(_project, src.NewClassName + "_copy"));
                var cloneB = CloneEntry(partner, RetexProjectService.MakeUniqueClassName(_project, partner.NewClassName + "_copy"));
                cloneA.PartnerClass = cloneB.NewClassName;
                cloneB.PartnerClass = cloneA.NewClassName;
                _project.Entries.Add(cloneA);
                _project.Entries.Add(cloneB);
                RetexProjectService.GenerateConfig(_project);
                RefreshEntries();
                SelectedEntry = _project.Entries.FirstOrDefault(e => e.NewClassName.Equals(cloneA.NewClassName, StringComparison.Ordinal));
                ConfigText = File.ReadAllText(_project.ConfigPath);
                SetStatus($"Duplicated uniform pair → {cloneA.NewClassName} + {cloneB.NewClassName} (shares the same texture files).");
                return;
            }
            // Partner missing (older/edited project): fall through to a single clone.
        }

        var clone = CloneEntry(src, RetexProjectService.MakeUniqueClassName(_project, src.NewClassName + "_copy"));
        _project.Entries.Add(clone);
        RetexProjectService.GenerateConfig(_project);
        RefreshEntries();
        SelectedEntry = _project.Entries.FirstOrDefault(e => e.NewClassName.Equals(clone.NewClassName, StringComparison.Ordinal));
        ConfigText = File.ReadAllText(_project.ConfigPath);
        SetStatus($"Duplicated to {clone.NewClassName} (shares the same texture files).");
    }

    /// <summary>Deep-copies a retexture entry (all fields + selections) under a new class name. Copies
    /// the uniform/unit flags so a cloned pair keeps its roles; <see cref="RetexEntry.PartnerClass"/> is
    /// left for the caller to set (it must point at the clone's partner, not the original's).</summary>
    private static RetexEntry CloneEntry(RetexEntry src, string newClassName) => new()
    {
        SourceClass = src.SourceClass,
        Category = src.Category,
        SourceModel = src.SourceModel,
        SourceAddon = src.SourceAddon,
        DisplayName = src.DisplayName,
        NewClassName = newClassName,
        CopiedBody = src.CopiedBody,
        IsUniform = src.IsUniform,
        IsUniformUnit = src.IsUniformUnit,
        Selections = src.Selections.Select(s => new RetexSelection
        {
            Index = s.Index, Name = s.Name,
            SourceTexture = s.SourceTexture, ProjectTexture = s.ProjectTexture,
            SourceMaterial = s.SourceMaterial, ProjectMaterial = s.ProjectMaterial,
        }).ToList(),
    };

    /// <summary>Folds the config editor's manual edits (displayName, copied stats) back into the
    /// project model so the next regeneration preserves them instead of clobbering.</summary>
    private void PreserveManualEdits()
    {
        if (_project is null) return;
        RetexProjectService.PreserveManualEdits(_project, ConfigText);
    }

    [RelayCommand]
    private void RegenerateConfig()
    {
        if (_project is null) { SetStatus("No project yet.", StatusSeverity.Warn); return; }
        if (MessageBox.Show(
                "Regenerate config.cpp from the project?\n\n" +
                "KEPT:  your edits to displayName and copied class values (armor, ItemInfo, stats…).\n\n" +
                "LOST:  any comments you added, your manual formatting/layout, and any classes or " +
                "properties you hand-wrote in the text that aren't part of the project's retextures.\n\n" +
                "Continue?",
                "Regenerate config — this discards manual text changes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        PreserveManualEdits();
        int merged = RetexProjectService.ConsolidateTextures(_project); // count for the status line
        RetexProjectService.GenerateConfig(_project);                   // (also consolidates; idempotent)
        RefreshEntries();
        ConfigText = File.ReadAllText(_project.ConfigPath);
        SetStatus(merged > 0
            ? $"Regenerated config; merged {merged} duplicate texture file(s) into shared copies."
            : "Regenerated config from project entries (manual edits overwritten).");
        WarnIfMissingTextures(_project);
    }

    private void RefreshEntries()
    {
        ProjectEntries.Clear();
        if (_project is null) return;
        foreach (var e in _project.Entries)
        {
            e.HasMissingTexture = e.Selections.Any(s =>
                s.ProjectTexture.Length > 0 && !File.Exists(Path.Combine(_project.AddonDir, s.ProjectTexture)));
            ProjectEntries.Add(e);
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        if (_project is null) { SetStatus("No project yet.", StatusSeverity.Warn); return; }
        // Fold recognized text edits (displayName, copied class values) into the model + retex.json
        // BEFORE writing, so the saved config and the project model stay in sync. Otherwise the model
        // keeps the old names and a later regenerate reverts what was just saved.
        PreserveManualEdits();
        File.WriteAllText(_project.ConfigPath, ConfigText);
        RefreshEntries(); // reflect any folded name changes in the entry list
        SetStatus("Saved config.cpp");
    }

    [RelayCommand]
    private void OpenProjectFolder()
    {
        if (_project is null) { SetStatus("No project yet.", StatusSeverity.Warn); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_project.ProjectDir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task Pack()
    {
        if (_project is null) { SetStatus("No project yet.", StatusSeverity.Warn); return; }
        var pboc = File.Exists(_settings.PbocPath) ? _settings.PbocPath : PboTool.FindDefault();
        if (pboc is null)
        {
            SetStatus("pboc.exe not found. Install PBO Manager, or set its path in Settings.", StatusSeverity.Error);
            OpenSettings(Application.Current.MainWindow, isFirstRun: true);
            return;
        }

        try
        {
            Directory.CreateDirectory(_project.AddonDir);          // in case the folder was moved/removed
            File.WriteAllText(_project.ConfigPath, ConfigText);    // pack what's shown
            var tool = new PboTool(pboc);
            SetStatus("Packing...");
            var res = await RetexProjectService.PackModAsync(_project, tool);
            if (res.Success)
            {
                SetStatus($"Packed mod -> {RetexProjectService.ModFolder(_project)}");
                WarnIfMissingTextures(_project);
            }
            else
            {
                var detail = res.StdErr.Trim();
                if (detail.Length == 0) detail = res.StdOut.Trim();
                SetStatus($"Pack failed (exit {res.ExitCode}): {detail}", StatusSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            // e.g. pboc.exe can't be launched, or a file is locked. Report instead of crashing.
            SetStatus($"Pack failed: {ex.Message}", StatusSeverity.Error);
        }
    }

    // ---------------------------------------------------------------- window state (persisted)

    /// <summary>Reads back the persisted window geometry (called by the window on load).</summary>
    public AppSettings Settings => _settings;
}
