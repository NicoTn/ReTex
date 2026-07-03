using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReTex.Core.Mods;
using ReTex.Core.Tools;

namespace ReTex.App.ViewModels;

/// <summary>Backs the Settings window: lets the user (re)point ReTex at the folders/tools it needs.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    [ObservableProperty] private string _workshopPath;
    [ObservableProperty] private string _projectsRoot;
    [ObservableProperty] private string _pbocPath;
    [ObservableProperty] private string _defaultAuthor;
    [ObservableProperty] private string _defaultPrefixTemplate;

    [ObservableProperty] private string _workshopStatus = "";
    [ObservableProperty] private bool _workshopOk;
    [ObservableProperty] private string _projectsRootStatus = "";
    [ObservableProperty] private bool _projectsRootOk;
    [ObservableProperty] private string _pbocStatus = "";
    [ObservableProperty] private bool _pbocOk;

    /// <summary>Set when a value was actually changed and confirmed with OK.</summary>
    public bool Saved { get; private set; }

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _workshopPath = settings.WorkshopPath;
        _projectsRoot = settings.ProjectsRoot;
        _pbocPath = settings.PbocPath;
        _defaultAuthor = settings.DefaultAuthor;
        _defaultPrefixTemplate = string.IsNullOrWhiteSpace(settings.DefaultPrefixTemplate)
            ? @"z\{slug}\addons\main" : settings.DefaultPrefixTemplate;
        Revalidate();
    }

    partial void OnWorkshopPathChanged(string value) => ValidateWorkshop();
    partial void OnProjectsRootChanged(string value) => ValidateProjectsRoot();
    partial void OnPbocPathChanged(string value) => ValidatePboc();

    public void Revalidate()
    {
        ValidateWorkshop();
        ValidateProjectsRoot();
        ValidatePboc();
    }

    private void ValidateWorkshop()
    {
        if (string.IsNullOrWhiteSpace(WorkshopPath) || !Directory.Exists(WorkshopPath))
        {
            WorkshopOk = false;
            WorkshopStatus = "Folder not found.";
            return;
        }
        var count = ModScanner.ScanFolder(WorkshopPath).Count;
        if (count == 0)
        {
            WorkshopOk = false;
            WorkshopStatus = "Folder exists, but no mods (@-folders with an addons\\ subfolder) were found in it.";
        }
        else
        {
            WorkshopOk = true;
            WorkshopStatus = $"Found {count} mod(s).";
        }
    }

    private void ValidateProjectsRoot()
    {
        if (string.IsNullOrWhiteSpace(ProjectsRoot))
        {
            ProjectsRootOk = false;
            ProjectsRootStatus = "Please choose a folder.";
            return;
        }
        // This folder is created on demand, so it's fine if it doesn't exist yet -
        // just make sure the path is well-formed and its parent is reachable.
        if (Directory.Exists(ProjectsRoot))
        {
            ProjectsRootOk = true;
            ProjectsRootStatus = "Folder exists.";
            return;
        }
        var parent = Path.GetDirectoryName(ProjectsRoot);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
        {
            ProjectsRootOk = true;
            ProjectsRootStatus = "Will be created here.";
        }
        else
        {
            ProjectsRootOk = false;
            ProjectsRootStatus = "Parent folder doesn't exist.";
        }
    }

    private void ValidatePboc()
    {
        if (string.IsNullOrWhiteSpace(PbocPath))
        {
            var found = PboTool.FindDefault();
            if (found is not null)
            {
                PbocOk = true;
                PbocStatus = $"Auto-detected at {found}";
            }
            else
            {
                PbocOk = false;
                PbocStatus = "Not found. Install PBO Manager, or browse to pboc.exe (only needed for the \"Pack @Mod\" step).";
            }
            return;
        }

        if (File.Exists(PbocPath))
        {
            PbocOk = true;
            PbocStatus = "Found.";
        }
        else
        {
            PbocOk = false;
            PbocStatus = "File not found.";
        }
    }

    [RelayCommand]
    private void BrowseWorkshop()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select your Arma 3 Workshop / mods folder",
            InitialDirectory = Directory.Exists(WorkshopPath) ? WorkshopPath : null,
        };
        if (dlg.ShowDialog() == true) WorkshopPath = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseProjectsRoot()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a folder to store ReTex projects in",
            InitialDirectory = Directory.Exists(ProjectsRoot) ? ProjectsRoot : null,
        };
        if (dlg.ShowDialog() == true) ProjectsRoot = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowsePboc()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select pboc.exe (PBO Manager CLI)",
            Filter = "pboc.exe|pboc.exe|Executable (*.exe)|*.exe|All files (*.*)|*.*",
            InitialDirectory = File.Exists(PbocPath) ? Path.GetDirectoryName(PbocPath) : null,
        };
        if (dlg.ShowDialog() == true) PbocPath = dlg.FileName;
    }

    [RelayCommand]
    private void AutoDetectPboc()
    {
        PbocPath = "";
        ValidatePboc();
    }

    [RelayCommand]
    private void Save()
    {
        _settings.WorkshopPath = WorkshopPath;
        _settings.ProjectsRoot = ProjectsRoot;
        _settings.PbocPath = PbocPath;
        _settings.DefaultAuthor = (DefaultAuthor ?? "").Trim();
        _settings.DefaultPrefixTemplate = string.IsNullOrWhiteSpace(DefaultPrefixTemplate)
            ? @"z\{slug}\addons\main" : DefaultPrefixTemplate.Trim();
        _settings.SetupCompleted = true;
        _settings.Save();
        Saved = true;
    }
}
