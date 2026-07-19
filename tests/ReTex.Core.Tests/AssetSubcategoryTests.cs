using ReTex.Core.Assets;
using ReTex.Core.Rap;

namespace ReTex.Core.Tests;

public sealed class AssetSubcategoryTests
{
    [Fact]
    public void Extract_AssignsArsenalEquipmentAndWeaponGroups()
    {
        var root = CppConfigParser.Parse("""
            class CfgWeapons {
                class Uniform_Base { hiddenSelections[] = {"camo"}; hiddenSelectionsTextures[] = {"u.paa"};
                    class ItemInfo { type = 801; uniformClass = "Soldier"; };
                };
                class Vest_Base { hiddenSelections[] = {"camo"}; hiddenSelectionsTextures[] = {"v.paa"};
                    class ItemInfo { type = 701; containerClass = "Supply80"; };
                };
                class Pistol_Base { type = 2; hiddenSelections[] = {"camo"}; hiddenSelectionsTextures[] = {"p.paa"}; };
                class Launcher_Base { type = 4; hiddenSelections[] = {"camo"}; hiddenSelectionsTextures[] = {"l.paa"}; };
            };
            """);

        var assets = AssetExtractor.Extract(root).ToDictionary(a => a.ClassName);

        Assert.Equal(AssetSubcategory.Uniform, assets["Uniform_Base"].Subcategory);
        Assert.Equal(AssetSubcategory.Vest, assets["Vest_Base"].Subcategory);
        Assert.Equal(AssetSubcategory.Handgun, assets["Pistol_Base"].Subcategory);
        Assert.Equal(AssetSubcategory.Launcher, assets["Launcher_Base"].Subcategory);
    }

    [Fact]
    public void Extract_AssignsVehicleAndBackpackGroupsFromAncestry()
    {
        var root = CppConfigParser.Parse("""
            class CfgVehicles {
                class Helicopter_Base { maxSpeed = 250; hiddenSelections[] = {"camo"}; hiddenSelectionsTextures[] = {"h.paa"}; };
                class My_Heli: Helicopter_Base {};
                class Bag_Base { maximumLoad = 80; hiddenSelections[] = {"camo"}; hiddenSelectionsTextures[] = {"b.paa"}; };
                class My_Pack: Bag_Base {};
            };
            """);

        var assets = AssetExtractor.Extract(root).ToDictionary(a => a.ClassName);

        Assert.Equal(AssetSubcategory.Helicopter, assets["My_Heli"].Subcategory);
        Assert.Equal(AssetSubcategory.Backpack, assets["My_Pack"].Subcategory);
    }

    [Fact]
    public void Extract_UsesInheritedItemInfoUniformModelForWearable()
    {
        var root = CppConfigParser.Parse("""
            class CfgWeapons {
                class HelmetBase {
                    hiddenSelections[] = {"camo"};
                    class ItemInfo { type = 605; uniformModel = "mod\helmet_visible.p3d"; };
                };
                class HelmetVariant: HelmetBase {
                    hiddenSelectionsTextures[] = {"mod\\helmet_red.paa"};
                    class ItemInfo: ItemInfo {};
                };
            };
            """);

        var asset = Assert.Single(AssetExtractor.Extract(root), a => a.ClassName == "HelmetVariant");

        Assert.Equal("mod\\helmet_visible.p3d", asset.Model);
        Assert.Equal(AssetSubcategory.Headgear, asset.Subcategory);
    }
}
