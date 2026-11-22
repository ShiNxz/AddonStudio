using AddonStudio.Core.Contracts;
using AddonStudio.Core.Pack;
using AddonStudio.Core.Util;
using AddonStudio.Tests.Build;
using AddonStudio.Tests.Support;
using CS2WorkshopManager;
using ValvePak;

namespace AddonStudio.Tests.Pack;

public sealed class PackServiceTests
{
    /// <summary>A project on Valve's whitelist (pack.valveWhitelist), FakeCs2's gameinfo.gi lists panorama/, models/ and materials/.</summary>
    private static FakeCs2 CreateProject()
    {
        var cs2 = new FakeCs2(withCompiler: false);
        cs2.Config = cs2.Config with { Pack = new PackConfig { ValveWhitelist = true, Include = ["sounds/**"], Exclude = ["**/*.psd"] } };
        cs2.WriteContent("panorama/layout/hud.xml", "<root/>");
        cs2.WritePrebuilt("models/coin.vmdl_c", "model-v2");
        cs2.WriteBuild("panorama/layout/hud.vxml_c", "hud");
        cs2.WriteBuild("models/coin.vmdl_c", "model-v2");
        cs2.WriteBuild("sounds/ui/click.vsnd_c", "click");
        cs2.WriteBuild("materials/art.psd", "psd");
        cs2.WriteBuild("materials/skip/x.vmat_c", "skipped by gameinfo");
        cs2.WriteBuild("particles/x.vpcf_c", "not in VpkDirectories");
        cs2.WriteBuild("tools_asset_info.bin", "valve");

        return cs2;
    }

    [Fact]
    public async Task Contents_follow_the_rules_and_diff_against_the_last_publish()
    {
        using var cs2 = CreateProject();
        var service = new PackService(cs2.Projects, cs2.Env);

        var before = await service.ContentsAsync(cs2.Repo);
        Assert.Equal(["models/coin.vmdl_c", "panorama/layout/hud.vxml_c", "sounds/ui/click.vsnd_c"], before.Files.Select(file => file.Path).Order().ToList());
        Assert.All(before.Files, file => Assert.Null(file.Change));
        Assert.Null(before.LastPublish);
        Assert.Equal(AddonPackager.MaxTotalSize, before.LimitBytes);

        new LastPublish
        {
            At = DateTimeOffset.UtcNow,
            WorkshopId = "123",
            Commit = "abc",
            Files = new Dictionary<string, LastPublishEntry>
            {
                ["panorama/layout/hud.vxml_c"] = new(3, FileHasher.HashText("hud")),
                ["models/coin.vmdl_c"] = new(8, FileHasher.HashText("model-v1")),
                ["materials/old.vmat_c"] = new(1, FileHasher.HashText("o")),
            },
        }.Write(cs2.Repo);

        var contents = await service.ContentsAsync(cs2.Repo);
        PackFile File(string path) => contents.Files.Single(file => file.Path == path);

        Assert.Equal(FileChange.Same, File("panorama/layout/hud.vxml_c").Change);
        Assert.Equal(PackOrigin.Compiled, File("panorama/layout/hud.vxml_c").Origin);
        Assert.Equal(FileChange.Changed, File("models/coin.vmdl_c").Change);
        Assert.Equal(PackOrigin.Prebuilt, File("models/coin.vmdl_c").Origin);
        Assert.Equal(FileChange.Added, File("sounds/ui/click.vsnd_c").Change);
        Assert.Equal(PackOrigin.Other, File("sounds/ui/click.vsnd_c").Origin);
        Assert.Equal(["materials/old.vmat_c"], contents.Removed);
        Assert.Equal(new PackChanges(1, 1, 1), contents.Changes);
        Assert.Equal(3, contents.FileCount);
        Assert.Equal(3 + 8 + 5, contents.TotalSize);
        Assert.Equal(3, contents.ByType.Count);
        Assert.Equal("123", contents.LastPublish!.WorkshopId);

        // the whitelist mode still says what it leaves out
        Assert.True(contents.ValveWhitelist);
        Assert.Equal(["materials/skip/x.vmat_c", "particles/x.vpcf_c"], contents.WhitelistDropped.Select(file => file.Path).ToList());
    }

    private const string CustomGameWhitelist = """
        "GameInfo"
        {
            AddonConfig
            {
                VpkDirectories
                {
                    exclude "materials/skip/"
                    include "panorama/layout/custom_game/"
                    include "panorama/images/custom_game/"
                    include "models/"
                    include "materials/"
                    include "maps/"
                }
            }
        }
        """;

    /// <summary>
    /// The case that lost files: rank badges under panorama/images/icons/ and an emote model under characters/, both outside
    /// Valve's custom_game whitelist. Plus junk, sources, an exe, hidden and Valve files the default mode must leave out.
    /// </summary>
    private static FakeCs2 CreateEverythingProject()
    {
        var cs2 = new FakeCs2(withCompiler: false);
        File.WriteAllText(cs2.GameInfoPath, CustomGameWhitelist);
        cs2.Config = cs2.Config with { Pack = new PackConfig { Include = ["panorama/images/keep.png"], Exclude = ["**/*.psd"] } };

        cs2.WriteContent("panorama/layout/custom_game/hud.xml", "<root/>");
        cs2.WriteContent("panorama/images/icons/skillgroups/skillgroup1.png", "png");
        cs2.WritePrebuilt("characters/models/emote/dance.vmdl_c", "emote");
        cs2.WritePrebuilt("maps/test.vpk", "map");

        cs2.WriteBuild("panorama/layout/custom_game/hud.vxml_c", "hud");
        cs2.WriteBuild("panorama/images/icons/skillgroups/skillgroup1_png.vtex_c", "badge");
        cs2.WriteBuild("characters/models/emote/dance.vmdl_c", "emote");
        cs2.WriteBuild("maps/test.vpk", "map");
        cs2.WriteBuild("models/coin.vmdl_c", "coin");
        cs2.WriteBuild("materials/skip/x.vmat_c", "mat");
        cs2.WriteBuild("panorama/images/keep.png", "kept by an include rule");

        cs2.WriteBuild("Thumbs.db", "junk");
        cs2.WriteBuild("build.tmp", "junk");
        cs2.WriteBuild("panorama/layout/custom_game/notes.log", "junk");
        cs2.WriteBuild("panorama/layout/custom_game/hud.xml", "<root/>");
        cs2.WriteBuild("models/src/body.fbx", "fbx");
        cs2.WriteBuild("materials/art.psd", "psd");
        cs2.WriteBuild("bin/tool.exe", "exe");

        cs2.WriteBuild(".DS_Store", "mac");
        cs2.WriteBuild("panorama/.DS_Store", "mac");
        cs2.WriteBuild("tools_asset_info.bin", "valve");
        cs2.WriteBuild("panorama/tools_thumbnail_cache.bin", "valve");
        cs2.WriteBuild("_bakeresourcecache/x.dat", "valve");

        return cs2;
    }

    private static readonly string[] EverythingPacked =
    [
        "characters/models/emote/dance.vmdl_c",
        "maps/test.vpk",
        "materials/skip/x.vmat_c",
        "models/coin.vmdl_c",
        "panorama/images/icons/skillgroups/skillgroup1_png.vtex_c",
        "panorama/images/keep.png",
        "panorama/layout/custom_game/hud.vxml_c",
    ];

    private static readonly string[] DroppedByWhitelist =
    [
        "characters/models/emote/dance.vmdl_c",
        "materials/skip/x.vmat_c",
        "panorama/images/icons/skillgroups/skillgroup1_png.vtex_c",
    ];

    [Fact]
    public async Task Default_packs_everything_but_junk_and_lists_what_the_whitelist_would_drop()
    {
        using var cs2 = CreateEverythingProject();
        var service = new PackService(cs2.Projects, cs2.Env);

        var contents = await service.ContentsAsync(cs2.Repo);

        Assert.False(contents.ValveWhitelist);
        Assert.Equal(EverythingPacked, contents.Files.Select(file => file.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(PackOrigin.Compiled, contents.Files.Single(file => file.Path.EndsWith("skillgroup1_png.vtex_c", StringComparison.Ordinal)).Origin);
        Assert.Equal(PackOrigin.Prebuilt, contents.Files.Single(file => file.Path.StartsWith("characters/", StringComparison.Ordinal)).Origin);
        Assert.Equal(DroppedByWhitelist, contents.WhitelistDropped.Select(file => file.Path).ToArray());
        Assert.Equal(5, contents.WhitelistDropped.Single(file => file.Path.EndsWith("skillgroup1_png.vtex_c", StringComparison.Ordinal)).Size);

        // hidden files and Valve's tool files are not listed, the rest says why it was left out
        Assert.Equal(
            [
                ("bin/tool.exe", PackSkipReason.Blocked),
                ("build.tmp", PackSkipReason.Junk),
                ("materials/art.psd", PackSkipReason.Excluded),
                ("models/src/body.fbx", PackSkipReason.SourceOnly),
                ("panorama/layout/custom_game/hud.xml", PackSkipReason.SourceOnly),
                ("panorama/layout/custom_game/notes.log", PackSkipReason.Junk),
                ("Thumbs.db", PackSkipReason.Junk),
            ],
            contents.Skipped.Select(file => (file.Path, file.Reason)).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList());

        using var output = new TempDir();
        await service.ExportAsync(cs2.Repo, output.Path);
        Assert.Equal(EverythingPacked, VpkEntries(Path.Combine(output.Path, "test_addon_dir.vpk")));
    }

    [Fact]
    public async Task Valve_whitelist_only_drops_those_files_and_still_lists_them()
    {
        using var cs2 = CreateEverythingProject();
        cs2.Config = cs2.Config with { Pack = cs2.Config.Pack with { ValveWhitelist = true } };
        var service = new PackService(cs2.Projects, cs2.Env);

        var contents = await service.ContentsAsync(cs2.Repo);
        var packed = contents.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);

        Assert.True(contents.ValveWhitelist);
        Assert.Equal(DroppedByWhitelist, contents.WhitelistDropped.Select(file => file.Path).ToArray());
        Assert.All(DroppedByWhitelist, path => Assert.DoesNotContain(path, packed));
        Assert.Contains("panorama/layout/custom_game/hud.vxml_c", packed);
        Assert.Contains("panorama/images/keep.png", packed);
        Assert.DoesNotContain("materials/art.psd", packed);

        // exactly what CS2WorkshopManager would pack
        var valve = AddonPackager.CollectFiles(cs2.BuildDir, cs2.GameInfoPath, PackService.Selection(cs2.Config.Pack, cs2.BuildDir, cs2.GameInfoPath).Rules)
            .Select(file => AddonPackager.GetRelativePath(Paths.Full(cs2.BuildDir), file.FullName))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(valve, packed);

        using var output = new TempDir();
        await service.ExportAsync(cs2.Repo, output.Path);
        Assert.Equal(valve.Order(StringComparer.Ordinal), VpkEntries(Path.Combine(output.Path, "test_addon_dir.vpk")));
    }

    private static string[] VpkEntries(string directoryFile)
    {
        using var package = new Package();
        package.Read(directoryFile);

        return package.Entries!.Values.SelectMany(entries => entries).Select(entry => entry.GetFullPath()).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task Export_writes_a_vpk_valvepak_reads_back()
    {
        using var cs2 = CreateProject();
        using var output = new TempDir();
        var service = new PackService(cs2.Projects, cs2.Env);

        var result = await service.ExportAsync(cs2.Repo, output.Path);

        Assert.Equal(Paths.Full(output.Path), result.Dir);
        Assert.Contains(result.Files, file => Path.GetFileName(file) == "test_addon_dir.vpk");
        Assert.Contains(result.Files, file => Path.GetFileName(file) == AddonPackager.PublishDataFileName);
        Assert.Equal("test_addon", AddonPackager.ReadPublishedSourceFolder(output.Path));

        using var package = new Package();
        package.Read(Path.Combine(output.Path, "test_addon_dir.vpk"));
        var names = package.Entries!.Values.SelectMany(entries => entries).Select(entry => entry.GetFullPath()).Order().ToList();
        Assert.Equal(["models/coin.vmdl_c", "panorama/layout/hud.vxml_c", "sounds/ui/click.vsnd_c"], names);

        package.ReadEntry(package.FindEntry("panorama/layout/hud.vxml_c")!, out byte[] data);
        Assert.Equal("hud", System.Text.Encoding.UTF8.GetString(data));

        // a second export into the same folder replaces its own files only
        System.IO.File.WriteAllText(Path.Combine(output.Path, "keep.txt"), "mine");
        var again = await service.ExportAsync(cs2.Repo, output.Path);
        Assert.True(System.IO.File.Exists(Path.Combine(output.Path, "keep.txt")));
        Assert.DoesNotContain(again.Files, file => file.EndsWith("keep.txt", StringComparison.Ordinal));

        var capped = new PackService(cs2.Projects, cs2.Env, () => 5);
        var error = await Assert.ThrowsAsync<AddonStudioException>(() => capped.ExportAsync(cs2.Repo, output.Path));
        Assert.Equal(ErrorCodes.InvalidParams, error.Code);
    }

    [Theory]
    [InlineData("**/*.psd", "a.psd", true)]
    [InlineData("**/*.psd", "materials/x/a.PSD", true)]
    [InlineData("materials/*.vmat_c", "materials/x/a.vmat_c", false)]
    [InlineData("materials/**", "materials/x/a.vmat_c", true)]
    [InlineData("sounds/?.vsnd_c", "sounds/a.vsnd_c", true)]
    public void Globs_match_build_relative_paths(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PackGlob.IsMatch(pattern, path));
    }
}
