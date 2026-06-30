namespace ReTex.Core.Mods;

/// <summary>An installed Arma 3 mod folder (e.g. an @-folder under !Workshop).</summary>
public sealed class ArmaMod
{
    /// <summary>Folder name, e.g. "@3den Enhanced".</summary>
    public required string Name { get; init; }

    /// <summary>Full path to the mod folder.</summary>
    public required string Path { get; init; }

    /// <summary>Friendly name from mod.cpp 'name', falling back to the folder name.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Full paths of the mod's PBOs (under addons\).</summary>
    public List<string> PboPaths { get; } = new();

    public int PboCount => PboPaths.Count;

    public override string ToString() => DisplayName;
}
