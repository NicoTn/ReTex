using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
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

public partial class MainViewModel : ObservableObject
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

    // Re-fires the preview shortly after a project .paa changes on disk (external editor save).
    private FileSystemWatcher? _texWatcher;
    private readonly DispatcherTimer _texDebounce;

    public MainViewModel()
    {
        WorkshopPath = _settings.WorkshopPath;
        ProjectsRoot = _settings.ProjectsRoot;
        LoadRecents();
        RefreshPbocStatus();
        _texDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _texDebounce.Tick += (_, _) => { _texDebounce.Stop(); RefreshPreview(); };
        _prevEditorTab = Math.Clamp(_settings.LastEditorTab, 0, 1);
        EditorTabIndex = _prevEditorTab;
        if (Directory.Exists(WorkshopPath)) ScanWorkshop();
    }

    /// <summary>Sets the status line text and severity in one call.</summary>
    private void SetStatus(string message, StatusSeverity severity = StatusSeverity.Info)
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
            if (img is null) { PreviewImage = null; PreviewInfo = $"({slot.Label}: texture not found)"; return; }
            PreviewImage = ImageHelper.ToBitmap(img);
            string tag = slot.Kind == PreviewKind.ProjectFile ? "project" : "source";
            PreviewInfo = $"{slot.Label}  {img.Width}x{img.Height}  ({tag})";
        }
        catch (Exception ex) { PreviewImage = null; PreviewInfo = $"(preview failed: {ex.Message})"; }
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
    private async Task LoadPreviewModel(string? modelVirtualPath, IReadOnlyList<RetexSelection>? selections)
    {
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
        try
        {
            var (model, info, labels, summary) = await Task.Run(() =>
            {
                var bytes = ExtractAcrossMods(allPbos, selPbos, modelVirtualPath!);
                if (bytes is null) return ((Model3DGroup?)null, "(source model not found — is its mod installed and scanned?)", (IReadOnlyDictionary<GeometryModel3D, string>?)null, "");
                var mesh = OdolLodReader.ReadAnyVisualLod(bytes);
                if (mesh is null) return (null, "(3D preview unavailable for this model — its .p3d format/UV layout isn't fully supported yet; use the Texture tab)", null, "");
                var groups = OdolMeshPreview.BuildGroups(mesh, selections, addonDir);
                var built = ModelViewHelper.Build(mesh, groups, tex => LoadPreviewTexture(tex, allPbos, selPbos));
                string name = Path.GetFileName(modelVirtualPath!.Replace('\\', '/'));
                var infoStr = $"{name}  {mesh.Points.Length} verts · {mesh.Faces.Count} faces · {groups.Count} texture group(s)  ⓘ hover a part";

                // Per-part hover labels + a full groups/materials breakdown for the info tooltip.
                var lbl = built is null ? null : built.Parts.ToDictionary(kv => kv.Key, kv => DescribePart(kv.Value.Texture, mesh));
                var sum = DescribeModel(mesh, groups);
                return (built?.Model, infoStr, (IReadOnlyDictionary<GeometryModel3D, string>?)lbl, sum);
            });
            PreviewModel3D = model;
            PreviewModelInfo = info;
            PreviewPartLabels = labels;
            PreviewGroupsSummary = summary;
        }
        catch (Exception ex) { PreviewModelInfo = $"(3D preview failed: {ex.Message})"; }
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

    /// <summary>Re-runs the preview for whatever is currently selected. Used by the Refresh button
    /// and the texture-file watcher so an external .paa edit shows up without reselecting.</summary>
    [RelayCommand]
    private void RefreshPreview()
    {
        if (SelectedEntry is not null) { OnSelectedEntryChanged(SelectedEntry); SetStatus("Preview refreshed."); }
        else if (SelectedAsset is not null) OnSelectedAssetChanged(SelectedAsset);
    }

    /// <summary>Watches the open project's textures folder so saving a .paa from an external editor
    /// refreshes the preview automatically (debounced to coalesce the burst editors emit on save).</summary>
    private void SetupTextureWatcher()
    {
        _texWatcher?.Dispose();
        _texWatcher = null;
        if (_project is null) return;
        try
        {
            Directory.CreateDirectory(_project.TexturesDir);
            var w = new FileSystemWatcher(_project.TexturesDir, "*.paa")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            void OnChanged(object? _, FileSystemEventArgs __) => OnTextureFileChanged();
            w.Changed += OnChanged;
            w.Created += OnChanged;
            w.Renamed += (_, __) => OnTextureFileChanged();
            _texWatcher = w;
        }
        catch { /* watching is a nicety; ignore if the folder can't be watched */ }
    }

    private void OnTextureFileChanged()
    {
        // Marshal to the UI thread and (re)start the debounce timer; the Tick refreshes the preview.
        Application.Current?.Dispatcher.Invoke(() => { _texDebounce.Stop(); _texDebounce.Start(); });
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
    /// texture files). Uniform pairs are skipped — their cross-links make a clone non-trivial.</summary>
    [RelayCommand]
    private void DuplicateEntry()
    {
        if (_project is null || SelectedEntry is null) { SetStatus("Select a retexture to duplicate.", StatusSeverity.Warn); return; }
        var src = SelectedEntry;
        if (src.IsUniform || src.IsUniformUnit)
        {
            SetStatus("Duplicating uniform pairs isn't supported — retexture the uniform again instead.", StatusSeverity.Warn);
            return;
        }

        PreserveManualEdits(); // so the clone inherits the source entry's current edits
        var clone = new RetexEntry
        {
            SourceClass = src.SourceClass,
            Category = src.Category,
            SourceModel = src.SourceModel,
            SourceAddon = src.SourceAddon,
            DisplayName = src.DisplayName,
            NewClassName = RetexProjectService.MakeUniqueClassName(_project, src.NewClassName + "_copy"),
            CopiedBody = src.CopiedBody,
            Selections = src.Selections
                .Select(s => new RetexSelection { Index = s.Index, Name = s.Name, SourceTexture = s.SourceTexture, ProjectTexture = s.ProjectTexture })
                .ToList(),
        };
        _project.Entries.Add(clone);
        RetexProjectService.GenerateConfig(_project);
        RefreshEntries();
        SelectedEntry = _project.Entries.FirstOrDefault(e => e.NewClassName.Equals(clone.NewClassName, StringComparison.Ordinal));
        ConfigText = File.ReadAllText(_project.ConfigPath);
        SetStatus($"Duplicated to {clone.NewClassName} (shares the same texture files).");
    }

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
