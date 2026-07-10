using System.IO;
using System.Text.Json;

namespace ReTex.App;

/// <summary>Simple persisted settings (paths), stored in %AppData%\ReTex\settings.json.</summary>
public sealed class AppSettings
{
    public string WorkshopPath { get; set; } = @"C:\Program Files (x86)\Steam\steamapps\common\Arma 3\!Workshop";
    public string ProjectsRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReTex_Projects");

    /// <summary>Manual override for pboc.exe (PBO Manager CLI). Empty = auto-detect.</summary>
    public string PbocPath { get; set; } = "";

    /// <summary>Path to an external model viewer exe (e.g. P3D Analyzer) for the "Open in viewer"
    /// handoff. Empty = ask the first time.</summary>
    public string P3dViewerPath { get; set; } = "";

    /// <summary>True once the user has gone through Settings at least once (suppresses the automatic first-run prompt).</summary>
    public bool SetupCompleted { get; set; }

    /// <summary>Most-recently-used project files (full path to retex.json), newest first. Capped and de-duplicated.</summary>
    public List<string> RecentProjects { get; set; } = new();

    /// <summary>Default author stamped onto new projects (retex.json + mod.cpp). Empty = none.</summary>
    public string DefaultAuthor { get; set; } = "";

    /// <summary>$PBOPREFIX$ template for new projects. "{slug}" is replaced with the sanitized project name.
    /// Empty falls back to the built-in "z\{slug}\addons\main".</summary>
    public string DefaultPrefixTemplate { get; set; } = @"z\{slug}\addons\main";

    // --- Main window layout (persisted so the app reopens where you left it) ---
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }
    /// <summary>Height (in star units, stored as a raw double) of the browse/preview row above the config editor.</summary>
    public double BrowseRowHeight { get; set; }

    /// <summary>Last-used bottom-right editor tab (0 = form editor, 1 = config.cpp), so the app reopens on it.</summary>
    public int LastEditorTab { get; set; }

    private const int MaxRecent = 8;

    /// <summary>Pushes a project file to the top of the MRU list (de-duplicated, capped).</summary>
    public void PushRecentProject(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath)) return;
        RecentProjects.RemoveAll(p => string.Equals(p, projectFilePath, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, projectFilePath);
        if (RecentProjects.Count > MaxRecent) RecentProjects.RemoveRange(MaxRecent, RecentProjects.Count - MaxRecent);
    }

    /// <summary>Drops MRU entries whose file no longer exists (e.g. project deleted/moved).</summary>
    public void PruneRecentProjects() => RecentProjects.RemoveAll(p => !File.Exists(p));

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReTex");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
