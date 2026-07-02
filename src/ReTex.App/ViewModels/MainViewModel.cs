using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReTex.Core.Assets;
using ReTex.Core.Mods;
using ReTex.Core.P3d;
using ReTex.Core.Paa;
using ReTex.Core.Pbo;
using ReTex.Core.Projects;
using ReTex.Core.Tools;

namespace ReTex.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Load();

    [ObservableProperty] private string _workshopPath = "";
    [ObservableProperty] private string _status = "Pick a mod and scan.";

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
    [ObservableProperty] private Model3DGroup? _previewModel3D;
    [ObservableProperty] private string _previewModelInfo = "";
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
    public ObservableCollection<RetexEntry> ProjectEntries { get; } = new();
    private RetexProject? _project;

    public MainViewModel()
    {
        WorkshopPath = _settings.WorkshopPath;
        ProjectsRoot = _settings.ProjectsRoot;
        if (Directory.Exists(WorkshopPath)) ScanWorkshop();
    }

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
        if (Directory.Exists(WorkshopPath)) ScanWorkshop();
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

    [RelayCommand]
    private void ScanWorkshop()
    {
        Assets.Clear();
        try
        {
            _allMods = ModScanner.ScanFolder(WorkshopPath);
            ApplyModFilter();
            Status = $"{_allMods.Count} mods in {WorkshopPath}";
        }
        catch (Exception ex) { Status = $"Scan failed: {ex.Message}"; }
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

        Status = $"Loading assets from {mod.DisplayName}...";
        _allAssets = await Task.Run(() => AssetService.LoadForMod(mod));

        // Rebuild the PBO (sub-mod) list from the loaded assets.
        PboNames.Clear();
        PboNames.Add(AllPbos);
        foreach (var name in _allAssets.Select(a => Path.GetFileName(a.SourcePbo))
                     .Where(n => n.Length > 0).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            PboNames.Add(name);
        PboFilter = AllPbos;

        ApplyAssetFilter();
        Status = $"{mod.DisplayName}: {_allAssets.Count} retexturable assets in {PboNames.Count - 1} PBOs";
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

    [RelayCommand]
    private async Task LoadPreview()
    {
        PreviewImage = null;
        PreviewInfo = "";
        var asset = SelectedAsset;
        var mod = SelectedMod;
        if (asset is null || mod is null) return;

        var tex = asset.HiddenSelectionsTextures.FirstOrDefault(t => t.Length > 0);
        if (tex is null) { PreviewInfo = "(no texture)"; return; }

        try
        {
            var img = await Task.Run(() =>
            {
                var bytes = VirtualFileService.Extract(mod.PboPaths, tex);
                return bytes is null ? null : PaaImage.Load(bytes);
            });
            if (img is null) { PreviewInfo = "(texture not found)"; return; }
            PreviewImage = ImageHelper.ToBitmap(img);
            PreviewInfo = $"{Path.GetFileName(tex.Replace('\\', '/'))}  {img.Width}x{img.Height}";
        }
        catch (Exception ex) { PreviewInfo = $"(preview failed: {ex.Message})"; }
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

    [RelayCommand]
    private void RetextureSelected()
    {
        if (SelectedAsset is null || SelectedMod is null) { Status = "Select an asset first."; return; }

        var indices = AssetSelections.Where(s => s.Selected).Select(s => s.Index).ToList();
        if (AssetSelections.Count > 0 && indices.Count == 0) { Status = "Tick at least one selection to retexture."; return; }
        IReadOnlyCollection<int>? chosen = AssetSelections.Count > 0 ? indices : null;

        try
        {
            _project ??= RetexProjectService.CreateProject(ProjectsRoot, ProjectName);
            var entry = RetexProjectService.AddRetexture(_project, SelectedAsset, SelectedMod.PboPaths, indices: chosen, copyValues: CopySourceValues, modAssets: _allAssets);
            RetexProjectService.GenerateConfig(_project);
            RefreshEntries();
            ConfigText = File.ReadAllText(_project.ConfigPath);
            var copied = entry.Selections.Count(s => s.ProjectTexture.Length > 0);
            Status = $"Added {entry.NewClassName} ({copied} texture(s) copied). Project: {_project.ProjectDir}";
        }
        catch (Exception ex) { Status = $"Retexture failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void NewProject()
    {
        _project = RetexProjectService.CreateProject(ProjectsRoot, ProjectName);
        RetexProjectService.GenerateConfig(_project);
        RefreshEntries();
        ConfigText = File.ReadAllText(_project.ConfigPath);
        Status = $"New project: {_project.ProjectDir}";
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
        try
        {
            _project = RetexProject.Load(dlg.FileName);
            ProjectName = _project.Name;
            ProjectsRoot = Path.GetDirectoryName(_project.ProjectDir) ?? ProjectsRoot;
            RefreshEntries();
            ConfigText = File.Exists(_project.ConfigPath) ? File.ReadAllText(_project.ConfigPath) : "";
            Status = $"Opened {_project.Name} ({_project.Entries.Count} retextures)";
            WarnIfMissingTextures(_project);
        }
        catch (Exception ex) { Status = $"Open failed: {ex.Message}"; }
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
        Status += $"  ⚠ {missing.Count} texture reference(s) point at missing files: {string.Join("; ", missing)}";
    }

    partial void OnSelectedEntryChanged(RetexEntry? value)
    {
        if (_project is null || value is null) return;
        var rel = value.Selections.FirstOrDefault(s => s.ProjectTexture.Length > 0)?.ProjectTexture;
        if (rel is not null) _ = LoadProjectPreview(Path.Combine(_project.AddonDir, rel));
        _ = LoadPreviewModel(value.SourceModel, value.Selections); // project flow: retexture applied
    }

    /// <summary>Builds the 3D model preview off the UI thread: extracts the .p3d from the source
    /// mod's PBOs, decodes it (<see cref="OdolLodReader.ReadVisualLod"/>), maps sections to resolved
    /// textures (<see cref="OdolMeshPreview"/>), and builds a frozen <see cref="Model3DGroup"/>.
    /// The model is resolved across ALL scanned mods (not just the selected one) so a project entry
    /// previews from its own source mod without the user having to re-select it. Falls back with an
    /// explanatory message when the model can't be extracted or decoded, leaving the flat tab.</summary>
    private async Task LoadPreviewModel(string? modelVirtualPath, IReadOnlyList<RetexSelection>? selections)
    {
        PreviewModel3D = null;
        PreviewModelInfo = "";
        if (string.IsNullOrWhiteSpace(modelVirtualPath)) { PreviewModelInfo = "(no source model for this asset)"; return; }

        // Snapshot PBO paths on the UI thread (Mods is an ObservableCollection - not thread-safe).
        var allPbos = Mods.SelectMany(m => m.PboPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var selPbos = SelectedMod?.PboPaths.ToList() ?? new List<string>();
        if (allPbos.Count == 0) { PreviewModelInfo = "(scan a mod folder to preview the model)"; return; }
        string? addonDir = _project?.AddonDir;
        try
        {
            var (model, info) = await Task.Run(() =>
            {
                var bytes = ExtractAcrossMods(allPbos, selPbos, modelVirtualPath!);
                if (bytes is null) return ((Model3DGroup?)null, "(source model not found — is its mod installed and scanned?)");
                var mesh = OdolLodReader.ReadAnyVisualLod(bytes);
                if (mesh is null) return (null, "(3D preview not available for this model format — see the texture tab)");
                var groups = OdolMeshPreview.BuildGroups(mesh, selections, addonDir);
                var m3d = ModelViewHelper.Build(mesh, groups, tex => LoadPreviewTexture(tex, allPbos, selPbos));
                string name = Path.GetFileName(modelVirtualPath!.Replace('\\', '/'));
                return (m3d, $"{name}  {mesh.Points.Length} verts · {mesh.Faces.Count} faces · {groups.Count} texture group(s)");
            });
            PreviewModel3D = model;
            PreviewModelInfo = info;
        }
        catch (Exception ex) { PreviewModelInfo = $"(3D preview failed: {ex.Message})"; }
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

    private async Task LoadProjectPreview(string path)
    {
        try
        {
            var img = await Task.Run(() => File.Exists(path) ? PaaImage.LoadFile(path) : null);
            if (img is null) return;
            PreviewImage = ImageHelper.ToBitmap(img);
            PreviewInfo = $"{Path.GetFileName(path)}  {img.Width}x{img.Height}  (project)";
        }
        catch (Exception ex) { PreviewInfo = $"(preview failed: {ex.Message})"; }
    }

    /// <summary>Opens the selected retexture's copied .paa(s) in the OS default editor (GIMP/Photoshop with a PAA plugin).</summary>
    [RelayCommand]
    private void EditEntryTextures()
    {
        if (_project is null || SelectedEntry is null) { Status = "Select a retexture in the project first."; return; }
        var files = SelectedEntry.Selections
            .Where(s => s.ProjectTexture.Length > 0)
            .Select(s => Path.Combine(_project.AddonDir, s.ProjectTexture))
            .Where(File.Exists).Distinct().ToList();
        if (files.Count == 0) { Status = "This retexture has no copied textures."; return; }
        foreach (var f in files)
        {
            try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); }
            catch (Exception ex) { Status = $"Open failed: {ex.Message}"; return; }
        }
        Status = $"Opened {files.Count} texture(s) in your default .paa editor. Save in place, then reselect to preview.";
    }

    /// <summary>Adds every currently-listed (filtered) asset to the project in one go.</summary>
    [RelayCommand]
    private async Task RetextureAllListed()
    {
        if (SelectedMod is null) { Status = "Pick a mod first."; return; }
        if (Assets.Count == 0) { Status = "No assets listed."; return; }

        var mod = SelectedMod;
        var toAdd = Assets.ToList();
        _project ??= RetexProjectService.CreateProject(ProjectsRoot, ProjectName);
        var proj = _project;

        Status = $"Batch retexturing {toAdd.Count} assets…";
        int n = await Task.Run(() =>
        {
            int c = 0;
            foreach (var a in toAdd) { RetexProjectService.AddRetexture(proj, a, mod.PboPaths, copyValues: CopySourceValues, modAssets: _allAssets); c++; }
            RetexProjectService.GenerateConfig(proj);
            return c;
        });
        RefreshEntries();
        ConfigText = File.ReadAllText(proj.ConfigPath);
        Status = $"Batch: added {n} retextures to {proj.Name}.";
    }

    [RelayCommand]
    private void RemoveEntry()
    {
        if (_project is null || SelectedEntry is null) return;
        // A uniform is two cross-linked entries (item + clothing unit); remove both halves together.
        if (SelectedEntry.PartnerClass.Length > 0 && (SelectedEntry.IsUniform || SelectedEntry.IsUniformUnit))
            _project.Entries.RemoveAll(e =>
                e.NewClassName.Equals(SelectedEntry.PartnerClass, StringComparison.OrdinalIgnoreCase));
        _project.Entries.Remove(SelectedEntry);
        RetexProjectService.GenerateConfig(_project);
        RefreshEntries();
        ConfigText = File.ReadAllText(_project.ConfigPath);
        Status = "Removed retexture.";
    }

    [RelayCommand]
    private void RegenerateConfig()
    {
        if (_project is null) { Status = "No project yet."; return; }
        int merged = RetexProjectService.ConsolidateTextures(_project); // count for the status line
        RetexProjectService.GenerateConfig(_project);                   // (also consolidates; idempotent)
        RefreshEntries();
        ConfigText = File.ReadAllText(_project.ConfigPath);
        Status = merged > 0
            ? $"Regenerated config; merged {merged} duplicate texture file(s) into shared copies."
            : "Regenerated config from project entries (manual edits overwritten).";
        WarnIfMissingTextures(_project);
    }

    private void RefreshEntries()
    {
        ProjectEntries.Clear();
        if (_project is null) return;
        foreach (var e in _project.Entries) ProjectEntries.Add(e);
    }

    [RelayCommand]
    private void SaveConfig()
    {
        if (_project is null) { Status = "No project yet."; return; }
        File.WriteAllText(_project.ConfigPath, ConfigText);
        Status = "Saved config.cpp";
    }

    [RelayCommand]
    private void OpenProjectFolder()
    {
        if (_project is null) { Status = "No project yet."; return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_project.ProjectDir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task Pack()
    {
        if (_project is null) { Status = "No project yet."; return; }
        var pboc = File.Exists(_settings.PbocPath) ? _settings.PbocPath : PboTool.FindDefault();
        if (pboc is null)
        {
            Status = "pboc.exe not found. Install PBO Manager, or set its path in Settings.";
            OpenSettings(Application.Current.MainWindow, isFirstRun: true);
            return;
        }

        File.WriteAllText(_project.ConfigPath, ConfigText); // pack what's shown
        var tool = new PboTool(pboc);
        Status = "Packing...";
        var res = await RetexProjectService.PackModAsync(_project, tool);
        Status = res.Success
            ? $"Packed mod -> {RetexProjectService.ModFolder(_project)}"
            : $"Pack failed (exit {res.ExitCode}): {res.StdErr}";
        if (res.Success) WarnIfMissingTextures(_project);
    }
}
