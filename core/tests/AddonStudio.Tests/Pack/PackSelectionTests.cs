using System.Text.Json.Nodes;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Pack;
using AddonStudio.Core.Projects;
using CS2WorkshopManager;

namespace AddonStudio.Tests.Pack;

public sealed class PackSelectionTests
{
    private static PackSelection Default(params AddonRules.Rule[] rules)
    {
        var addonRules = new AddonRules();

        foreach (var rule in rules)
        {
            addonRules.Add(rule);
        }

        return new PackSelection(valveWhitelist: false, addonRules, gameInfoPath: null);
    }

    [Theory]
    // compiled output and prebuilt files anywhere in the tree, whitelisted by Valve or not
    [InlineData("panorama/images/icons/skillgroups/skillgroup1_png.vtex_c", null)]
    [InlineData("characters/models/emote/dance.vmdl_c", null)]
    [InlineData("panorama/layout/custom_game/hud.vxml_c", null)]
    [InlineData("maps/de_test.vpk", null)]
    [InlineData("cfg/next.cfg", null)]
    [InlineData("resource/next_english.txt", null)]
    [InlineData("panorama/fonts/next.ttf", null)]
    [InlineData("scripts/vscripts/thing.js", null)]
    // AddonPackager matches blocked extensions anywhere in the name ("compact" holds ".com"), we match the extension
    [InlineData("panorama/images/x.compact_png.vtex_c", null)]
    // what the compiler reads
    [InlineData("panorama/layout/hud.xml", PackSkipReason.SourceOnly)]
    [InlineData("panorama/scripts/hud.js", PackSkipReason.SourceOnly)]
    [InlineData("panorama/styles/hud.CSS", PackSkipReason.SourceOnly)]
    [InlineData("icons/a.svg", PackSkipReason.SourceOnly)]
    [InlineData("materials/x.vmat", PackSkipReason.SourceOnly)]
    [InlineData("models/x.vmdl", PackSkipReason.SourceOnly)]
    [InlineData("models/x.fbx", PackSkipReason.SourceOnly)]
    [InlineData("materials/textures/x.tga", PackSkipReason.SourceOnly)]
    [InlineData("sounds/x.wav", PackSkipReason.SourceOnly)]
    [InlineData("maps/de_test.vmap", PackSkipReason.SourceOnly)]
    [InlineData("art/cover.psd", PackSkipReason.SourceOnly)]
    // junk
    [InlineData("Thumbs.db", PackSkipReason.Junk)]
    [InlineData("panorama/desktop.ini", PackSkipReason.Junk)]
    [InlineData("a.tmp", PackSkipReason.Junk)]
    [InlineData("panorama/layout/hud.vxml_c~", PackSkipReason.Junk)]
    [InlineData("compile.log", PackSkipReason.Junk)]
    [InlineData("old/hud.vxml_c.bak", PackSkipReason.Junk)]
    // never packed, Valve's tool refuses them
    [InlineData("bin/x.exe", PackSkipReason.Blocked)]
    [InlineData("x.ZIP", PackSkipReason.Blocked)]
    [InlineData("scripts/run.bat", PackSkipReason.Blocked)]
    public void Default_mode_skip_reasons(string path, PackSkipReason? expected)
    {
        Assert.Equal(expected, Default().SkipReason(path));
    }

    [Fact]
    public void Rules_go_first_and_the_first_match_decides()
    {
        // what PackService.BuildRules makes of exclude ["**/*.psd"], include ["art/", "logs/keep.log"]: excludes first
        var selection = Default(
            new AddonRules.Rule(Exclude: true, "art/cover.psd"),
            new AddonRules.Rule(Exclude: false, "art/"),
            new AddonRules.Rule(Exclude: false, "logs/keep.log"),
            new AddonRules.Rule(Exclude: false, "bin/"));

        Assert.Equal(PackSkipReason.Excluded, selection.SkipReason("art/cover.psd"));
        Assert.Null(selection.SkipReason("art/back.png"));
        Assert.Null(selection.SkipReason("logs/keep.log"));
        Assert.Equal(PackSkipReason.Junk, selection.SkipReason("logs/other.log"));
        Assert.Equal(PackSkipReason.Blocked, selection.SkipReason("bin/x.exe"));
        Assert.Equal(PackSkipReason.Excluded, Default(new AddonRules.Rule(Exclude: true, "characters/")).SkipReason("characters/models/a.vmdl_c"));
    }

    [Fact]
    public void Globs_expand_into_rules_the_selection_applies()
    {
        using var temp = new Support.TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "art"));
        File.WriteAllText(Path.Combine(temp.Path, "art", "cover.psd"), "psd");
        File.WriteAllText(Path.Combine(temp.Path, "art", "back.png"), "png");

        var selection = PackService.Selection(new PackConfig { Include = ["art/*.png"], Exclude = ["**/*.psd"] }, temp.Path, gameInfo: null);
        var walk = selection.Walk(temp.Path);

        Assert.False(selection.ValveWhitelist);
        Assert.Equal(["art/back.png"], walk.Packed.Select(file => Path.GetRelativePath(temp.Path, file.FullName).Replace('\\', '/')));
        Assert.Equal(PackSkipReason.Excluded, Assert.Single(walk.Skipped).Reason);
    }

    [Fact]
    public void Whitelist_mode_needs_gameinfo()
    {
        using var temp = new Support.TempDir();
        var selection = new PackSelection(valveWhitelist: true, new AddonRules(), gameInfoPath: Path.Combine(temp.Path, "missing.gi"));

        var error = Assert.Throws<AddonStudioException>(() => selection.Collect(temp.Path));
        Assert.Equal(ErrorCodes.Cs2Missing, error.Code);

        // the default mode does not read it
        Assert.Empty(new PackSelection(valveWhitelist: false, new AddonRules(), gameInfoPath: null).Collect(temp.Path));
    }

    [Fact]
    public void Workshop_json_reads_valve_whitelist()
    {
        WorkshopConfig Read(string json, List<string> errors) => WorkshopConfigFile.FromJson(JsonNode.Parse(json)!.AsObject(), errors);

        var errors = new List<string>();
        Assert.False(Read("""{ "addon": "a", "pack": { "exclude": [] } }""", errors).Pack.ValveWhitelist);
        Assert.False(Read("""{ "addon": "a" }""", errors).Pack.ValveWhitelist);
        Assert.True(Read("""{ "addon": "a", "pack": { "valveWhitelist": true } }""", errors).Pack.ValveWhitelist);
        Assert.Empty(errors);

        Assert.False(Read("""{ "addon": "a", "pack": { "valveWhitelist": "yes" } }""", errors).Pack.ValveWhitelist);
        Assert.Equal(["pack.valveWhitelist must be true or false."], errors);
    }
}
