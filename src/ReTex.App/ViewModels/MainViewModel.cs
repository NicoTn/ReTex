using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReTex.Core.Assets;
using ReTex.Core.Mods;
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
        }
        catch (Exception ex) { Status = $"Open failed: {ex.Message}"; }
    }

    partial void OnSelectedEntryChanged(RetexEntry? value)
    {
        if (_project is null || value is null) return;
        var rel = value.Selections.FirstOrDefault(s => s.ProjectTexture.Length > 0)?.ProjectTexture;
        if (rel is not null) _ = LoadProjectPreview(Path.Combine(_project.AddonDir, rel));
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
        RetexProjectService.GenerateConfig(_project);
        ConfigText = File.ReadAllText(_project.ConfigPath);
        Status = "Regenerated config from project entries (manual edits overwritten).";
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
        var pboc = PboTool.FindDefault();
        if (pboc is null) { Status = "pboc.exe not found (install PBO Manager)."; return; }

        File.WriteAllText(_project.ConfigPath, ConfigText); // pack what's shown
        var tool = new PboTool(pboc);
        Status = "Packing...";
        var res = await RetexProjectService.PackModAsync(_project, tool);
        Status = res.Success
            ? $"Packed mod -> {RetexProjectService.ModFolder(_project)}"
            : $"Pack failed (exit {res.ExitCode}): {res.StdErr}";
    }
}
