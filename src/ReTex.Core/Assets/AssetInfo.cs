namespace ReTex.Core.Assets;

public enum AssetCategory
{
    Vehicle,    // CfgVehicles vehicle (car/tank/air/ship)
    Unit,       // CfgVehicles man/soldier
    Prop,       // CfgVehicles object/thing/building
    Backpack,   // CfgVehicles bag
    Weapon,     // CfgWeapons weapon
    Equipment,  // CfgWeapons wearable item (headgear/vest/uniform)
    Glasses,    // CfgGlasses
    Other,
}

/// <summary>A retexturable asset discovered in a mod config: a class with (resolved) hidden selections/textures.</summary>
public sealed class AssetInfo
{
    public required string ClassName { get; init; }
    public required AssetCategory Category { get; init; }
    public string Parent { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Model { get; init; } = "";
    public int Scope { get; init; } = 2;

    public IReadOnlyList<string> HiddenSelections { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> HiddenSelectionsTextures { get; init; } = Array.Empty<string>();

    /// <summary>Per-selection .rvmat material overrides (hiddenSelectionsMaterials[]). Empty when the
    /// asset doesn't expose material swaps. Parallel to <see cref="HiddenSelections"/> by index.</summary>
    public IReadOnlyList<string> HiddenSelectionsMaterials { get; init; } = Array.Empty<string>();

    /// <summary>The mod PBO this asset came from (filled in by the caller).</summary>
    public string SourcePbo { get; set; } = "";

    /// <summary>Source CfgPatches addon name (for requiredAddons in generated configs).</summary>
    public string SourceAddon { get; set; } = "";

    /// <summary>The PBO's $PBOPREFIX$, used to map virtual texture paths back to PBO entries.</summary>
    public string SourcePrefix { get; set; } = "";

    /// <summary>The parsed source class node (for copying its values into a retexture). Transient.</summary>
    public Rap.RapClass? SourceClassNode { get; set; }

    public string Label => DisplayName.Length > 0 ? $"{DisplayName}  ({ClassName})" : ClassName;

    public override string ToString() => Label;
}
