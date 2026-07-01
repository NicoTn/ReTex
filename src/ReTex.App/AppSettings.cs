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

    /// <summary>True once the user has gone through Settings at least once (suppresses the automatic first-run prompt).</summary>
    public bool SetupCompleted { get; set; }

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
