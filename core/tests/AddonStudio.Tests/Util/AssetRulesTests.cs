using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Tests.Util;

public sealed class AssetRulesTests
{
    // one row per line of the SPEC section 4 table
    [Theory]
    [InlineData("panorama/layout/custom_game/hud.xml", RootKind.Panorama, "panorama/layout/custom_game/hud.vxml_c")]
    [InlineData("panorama/styles/hud.css", RootKind.Panorama, "panorama/styles/hud.vcss_c")]
    [InlineData("panorama/scripts/hud.js", RootKind.Panorama, "panorama/scripts/hud.vjs_c")]
    [InlineData("panorama/images/icons/star.svg", RootKind.Panorama, "panorama/images/icons/star.vsvg_c")]
    [InlineData("materials/ui/star.svg", RootKind.Panorama, "materials/ui/star.vsvg_c")]
    [InlineData("panorama/images/coin.png", RootKind.Texture, "panorama/images/coin_png.vtex_c")]
    [InlineData("panorama/images/hud/bg.jpg", RootKind.Texture, "panorama/images/hud/bg_jpg.vtex_c")]
    [InlineData("panorama/images/bg.jpeg", RootKind.Texture, "panorama/images/bg_jpeg.vtex_c")]
    [InlineData("panorama/images/bg.tga", RootKind.Texture, "panorama/images/bg_tga.vtex_c")]
    [InlineData("panorama/images/bg.psd", RootKind.Texture, "panorama/images/bg_psd.vtex_c")]
    [InlineData("models/weapons/knife.vmdl", RootKind.Model, "models/weapons/knife.vmdl_c")]
    [InlineData("materials/models/knife.vmat", RootKind.Material, "materials/models/knife.vmat_c")]
    [InlineData("materials/models/knife_color.vtex", RootKind.Texture, "materials/models/knife_color.vtex_c")]
    [InlineData("particles/fx/spark.vpcf", RootKind.Particle, "particles/fx/spark.vpcf_c")]
    [InlineData("soundevents/addon.vsndevts", RootKind.SoundEvents, "soundevents/addon.vsndevts_c")]
    [InlineData("sounds/music/theme.wav", RootKind.Sound, "sounds/music/theme.vsnd_c")]
    [InlineData("sounds/ui/click.mp3", RootKind.Sound, "sounds/ui/click.vsnd_c")]
    [InlineData("scripts/items.vdata", RootKind.Vdata, "scripts/items.vdata_c")]
    public void RootsAndTheirOutputs(string source, RootKind kind, string output)
    {
        Assert.True(AssetRules.IsRoot(source));
        Assert.True(AssetRules.IsCompilable(source));
        Assert.Equal(kind, AssetRules.Kind(source));
        Assert.Equal([output], AssetRules.ExpectedOutputs(source));
        Assert.Null(AssetRules.UnsupportedReason(source));
        Assert.Contains(source, AssetRules.SourceCandidates(output), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapsAreUnsupportedRoots()
    {
        Assert.True(AssetRules.IsRoot("maps/de_test.vmap"));
        Assert.False(AssetRules.IsCompilable("maps/de_test.vmap"));
        Assert.Equal(RootKind.Map, AssetRules.Kind("maps/de_test.vmap"));
        Assert.Empty(AssetRules.ExpectedOutputs("maps/de_test.vmap"));
        Assert.Equal(AssetRules.MapUnsupportedReason, AssetRules.UnsupportedReason("maps/de_test.vmap"));
    }

    [Theory]
    [InlineData("materials/models/knife_color.png")]
    [InlineData("models/knife.fbx")]
    [InlineData("models/knife.dmx")]
    [InlineData("layout/hud.xml")]
    [InlineData("styles/hud.css")]
    [InlineData("scripts/hud.js")]
    [InlineData("music/theme.wav")]
    [InlineData("panorama/scripts/hud.ts")]
    [InlineData("panorama/images/readme.txt")]
    [InlineData("publish_rules.txt")]
    [InlineData("panorama/images")]
    public void DependenciesAndOtherFilesAreNotRoots(string path)
    {
        Assert.False(AssetRules.IsRoot(path));
        Assert.Null(AssetRules.Kind(path));
        Assert.Empty(AssetRules.ExpectedOutputs(path));
    }

    [Fact]
    public void MatchingIgnoresCaseAndBackslashes()
    {
        Assert.Equal(RootKind.Texture, AssetRules.Kind(@"Panorama\Images\Coin.PNG"));
        Assert.Equal(["Panorama/Images/Coin_png.vtex_c"], AssetRules.ExpectedOutputs(@"Panorama\Images\Coin.PNG"));
        Assert.Equal(RootKind.Sound, AssetRules.Kind("SOUNDS/a.WAV"));
        Assert.Equal(["Models/X.vmdl_c"], AssetRules.ExpectedOutputs("Models/X.VMDL"));
    }

    [Fact]
    public void SourceCandidatesFollowTheTable()
    {
        Assert.Equal(["sounds/a.wav", "sounds/a.mp3"], AssetRules.SourceCandidates("sounds/a.vsnd_c"));
        Assert.Equal(["panorama/images/coin.png", "panorama/images/coin_png.vtex"], AssetRules.SourceCandidates("panorama/images/coin_png.vtex_c"));
        Assert.Equal(["materials/a_png.vtex"], AssetRules.SourceCandidates("materials/a_png.vtex_c"));
        Assert.Empty(AssetRules.SourceCandidates("music/a.vsnd_c"));
        Assert.Empty(AssetRules.SourceCandidates("layout/hud.vxml_c"));
        Assert.Empty(AssetRules.SourceCandidates("maps/de_test.vpk"));
        Assert.Empty(AssetRules.SourceCandidates("models/x.vmdl"));
    }

    [Theory]
    [InlineData("tools_asset_info.bin", true)]
    [InlineData("readonly_tools_asset_info.bin", true)]
    [InlineData("TOOLS_ASSET_INFO.BIN", true)]
    [InlineData("_bakeresourcecache/a/b.bin", true)]
    [InlineData(@"_vrad3\lightmap.dat", true)]
    [InlineData("_vrad3", true)]
    [InlineData("models/tools_asset_info.bin", false)]
    [InlineData("panorama/_vrad3/x", false)]
    [InlineData("panorama/layout/hud.vxml_c", false)]
    public void ValveFiles(string path, bool expected)
    {
        Assert.Equal(expected, AssetRules.IsValveFile(path));
    }

    [Fact]
    public void TextRootsAndCompiledResources()
    {
        Assert.True(AssetRules.IsTextRoot("panorama/layout/hud.xml"));
        Assert.True(AssetRules.IsTextRoot("materials/a.vmat"));
        Assert.True(AssetRules.IsTextRoot("soundevents/a.vsndevts"));
        Assert.False(AssetRules.IsTextRoot("panorama/images/a.png"));
        Assert.False(AssetRules.IsTextRoot("panorama/images/a.svg"));
        Assert.False(AssetRules.IsTextRoot("maps/a.vmap"));

        Assert.True(AssetRules.IsCompiledResource("models/x.vmdl_c"));
        Assert.False(AssetRules.IsCompiledResource("models/x.vmdl"));
        Assert.False(AssetRules.IsCompiledResource("x._c"));
    }
}
