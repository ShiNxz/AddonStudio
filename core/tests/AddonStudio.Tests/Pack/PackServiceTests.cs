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
    private static FakeCs2 CreateProject()
    {
        var cs2 = new FakeCs2(withCompiler: false);
        cs2.Config = cs2.Config with { Pack = new PackConfig { Include = ["sounds/**"], Exclude = ["**/*.psd"] } };
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
