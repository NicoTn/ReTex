using ReTex.Core.Assets;
using ReTex.Core.Projects;

namespace ReTex.Core.Tests;

public sealed class ConfigGeneratorUniformTests
{
    [Fact]
    public void BackfillSourceMetadata_FillsBlanksWithoutOverwritingExistingValues()
    {
        var project = new RetexProject();
        project.RequiredAddons.Clear();
        project.Entries.Add(new RetexEntry { SourceClass = "Legacy_Helmet" });
        project.Entries.Add(new RetexEntry
        {
            SourceClass = "Keep_Me", SourceModel = "existing.p3d", SourceAddon = "Existing_Addon"
        });
        var assets = new[]
        {
            new AssetInfo { ClassName = "legacy_helmet", Category = AssetCategory.Equipment,
                Model = "resolved\\helmet.p3d", SourceAddon = "Resolved_Addon" },
            new AssetInfo { ClassName = "Keep_Me", Category = AssetCategory.Equipment,
                Model = "replacement.p3d", SourceAddon = "Replacement_Addon" }
        };

        RetexProjectService.BackfillSourceMetadata(project, assets);

        Assert.Equal("resolved\\helmet.p3d", project.Entries[0].SourceModel);
        Assert.Equal("Resolved_Addon", project.Entries[0].SourceAddon);
        Assert.Equal("existing.p3d", project.Entries[1].SourceModel);
        Assert.Equal("Existing_Addon", project.Entries[1].SourceAddon);
        Assert.Contains("Resolved_Addon", project.RequiredAddons);
        Assert.Contains("Replacement_Addon", project.RequiredAddons);
    }

    [Fact]
    public void Uniform_OverridesTexturesAtTopLevelAndInsideItemInfo()
    {
        var project = new RetexProject { Name = "Test", Prefix = "z\\test\\addons\\main" };
        project.Entries.Add(new RetexEntry
        {
            SourceClass = "SourceUniform",
            NewClassName = "TestUniform_Red",
            Category = AssetCategory.Equipment,
            IsUniform = true,
            PartnerClass = "TestUniform_Red_Unit",
            Selections =
            {
                new RetexSelection { Index = 0, Name = "camo", ProjectTexture = "textures\\red_arms.paa" },
                new RetexSelection { Index = 1, Name = "camo2", SourceTexture = "source\\body.paa" },
            },
        });

        var config = ConfigGenerator.Generate(project);
        var texturePath = "\\z\\test\\addons\\main\\textures\\red_arms.paa";

        Assert.Equal(2, Count(config, texturePath));
        Assert.Contains("class ItemInfo: ItemInfo", config);
        Assert.Contains("uniformClass = \"TestUniform_Red_Unit\"", config);
    }

    [Fact]
    public void SynchronizeUniformPairs_RepairsWornUnitTextureFromEditedItem()
    {
        var project = new RetexProject();
        project.Entries.Add(new RetexEntry
        {
            NewClassName = "RedUniform",
            IsUniform = true,
            PartnerClass = "RedUniform_Unit",
            Selections = { new RetexSelection { Index = 0, Name = "camo", ProjectTexture = "textures\\red.paa" } },
        });
        project.Entries.Add(new RetexEntry
        {
            NewClassName = "RedUniform_Unit",
            IsUniformUnit = true,
            PartnerClass = "RedUniform",
            Selections = { new RetexSelection { Index = 0, Name = "camo", ProjectTexture = "textures\\old.paa" } },
        });

        var changed = RetexProjectService.SynchronizeUniformPairs(project);

        Assert.Equal(1, changed);
        Assert.Equal("textures\\red.paa", project.Entries[1].Selections[0].ProjectTexture);
    }

    [Fact]
    public void GenerateConfig_PreservesIntentionalByteIdenticalTextureCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ReTexTests", Guid.NewGuid().ToString("N"));
        try
        {
            var project = new RetexProject { ProjectDir = dir };
            Directory.CreateDirectory(project.TexturesDir);
            File.WriteAllBytes(Path.Combine(project.TexturesDir, "shared.paa"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(project.TexturesDir, "working-copy.paa"), new byte[] { 1, 2, 3, 4 });
            project.Entries.Add(new RetexEntry
            {
                SourceClass = "SourceA", NewClassName = "A", Category = AssetCategory.Weapon,
                Selections = { new RetexSelection { Index = 0, SourceTexture = "source\\same.paa", ProjectTexture = "textures\\shared.paa" } },
            });
            project.Entries.Add(new RetexEntry
            {
                SourceClass = "SourceA", NewClassName = "A_Copy", Category = AssetCategory.Weapon,
                Selections = { new RetexSelection { Index = 0, SourceTexture = "source\\same.paa", ProjectTexture = "textures\\working-copy.paa" } },
            });

            RetexProjectService.GenerateConfig(project);

            Assert.Equal("textures\\working-copy.paa", project.Entries[1].Selections[0].ProjectTexture);
            Assert.True(File.Exists(Path.Combine(project.TexturesDir, "working-copy.paa")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UniformUnit_IsProtectedAndHiddenFromPublicBrowsers()
    {
        var project = new RetexProject();
        project.Entries.Add(new RetexEntry
        {
            SourceClass = "SourceUnit", NewClassName = "InternalWornUnit",
            Category = AssetCategory.Unit, IsUniformUnit = true,
        });

        var config = ConfigGenerator.Generate(project);

        Assert.Contains("class InternalWornUnit: SourceUnit", config);
        Assert.Contains("scope = 1;", config);
        Assert.Contains("scopeArsenal = 0;", config);
        Assert.Contains("scopeCurator = 0;", config);
    }

    [Fact]
    public void LegacyPairNormalization_RepairsMissingUnitRoleWithoutChangingNames()
    {
        var project = new RetexProject();
        project.Entries.Add(new RetexEntry
        {
            NewClassName = "LegacyUniform", IsUniform = true, PartnerClass = "LegacyUnit",
        });
        project.Entries.Add(new RetexEntry { NewClassName = "LegacyUnit" });

        project.NormalizeLegacyUniformPairs();

        Assert.True(project.Entries[1].IsUniformUnit);
        Assert.Equal("LegacyUniform", project.Entries[1].PartnerClass);
        Assert.Equal("LegacyUnit", project.Entries[0].PartnerClass);
    }

    private static int Count(string text, string value)
    {
        int count = 0, offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }
}
