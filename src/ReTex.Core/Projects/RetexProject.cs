using System.Text.Json;
using System.Text.Json.Serialization;
using ReTex.Core.Assets;

namespace ReTex.Core.Projects;

/// <summary>One hidden selection being retextured, and the new texture file backing it.</summary>
public sealed class RetexSelection
{
    public int Index { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Original texture virtual path from the source config (may be empty).</summary>
    public string SourceTexture { get; set; } = "";

    /// <summary>Texture file inside the project (relative to the addon folder), e.g. "textures\helmet_co.paa".</summary>
    public string ProjectTexture { get; set; } = "";
}

/// <summary>A single retexture: a new class inheriting a source class with new textures.</summary>
public sealed class RetexEntry
{
    public string SourceClass { get; set; } = "";
    public AssetCategory Category { get; set; }
    public string SourceModel { get; set; } = "";
    public string SourceAddon { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Generated class name for the retextured variant (editable).</summary>
    public string NewClassName { get; set; } = "";

    /// <summary>Source class's declared values (armor, weapon stats, ItemInfo, ...) serialized to config text, copied into the new class. Empty if not copying values.</summary>
    public string CopiedBody { get; set; } = "";

    public List<RetexSelection> Selections { get; set; } = new();
}

/// <summary>A retexture mod project: one addon PBO holding multiple retextures.</summary>
public sealed class RetexProject
{
    public string Name { get; set; } = "MyRetex";
    public string Author { get; set; } = "";

    /// <summary>$PBOPREFIX$ for the project's addon, e.g. "z\myretex\addons\main".</summary>
    public string Prefix { get; set; } = "z\\myretex\\addons\\main";

    /// <summary>Source mod CfgPatches addons this project inherits from (requiredAddons).</summary>
    public List<string> RequiredAddons { get; set; } = new() { "A3_Characters_F" };

    public List<RetexEntry> Entries { get; set; } = new();

    // --- Paths (not serialized; set from where the project file lives) ---
    [JsonIgnore] public string ProjectDir { get; set; } = "";
    [JsonIgnore] public string AddonDir => Path.Combine(ProjectDir, "addons", "main");
    [JsonIgnore] public string TexturesDir => Path.Combine(AddonDir, "textures");
    [JsonIgnore] public string ConfigPath => Path.Combine(AddonDir, "config.cpp");
    [JsonIgnore] public string ProjectFilePath => Path.Combine(ProjectDir, "retex.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save()
    {
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(ProjectFilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static RetexProject Load(string projectFilePath)
    {
        var proj = JsonSerializer.Deserialize<RetexProject>(File.ReadAllText(projectFilePath), JsonOpts)
                   ?? throw new InvalidDataException("Invalid project file.");
        proj.ProjectDir = Path.GetDirectoryName(projectFilePath)!;
        return proj;
    }
}
