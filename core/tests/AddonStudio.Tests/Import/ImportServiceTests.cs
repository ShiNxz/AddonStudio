using AddonStudio.Core.Contracts;
using AddonStudio.Core.Import;
using AddonStudio.Tests.Projects;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Import;

public sealed class ImportServiceTests
{
    private static void WriteFakeAddon(ProjectKit kit, string addon)
    {
        void Write(string root, string relative, string text)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        var content = kit.ContentLinkPath(addon);
        Write(content, "panorama/layout/hud.xml", "<root/>");
        Write(content, "panorama/images/coin.png", "png");

        // compiled files are fake bytes: classification only looks at names, decompiling them fails
        var game = kit.BuildDir(addon);
        Write(game, "panorama/layout/hud.vxml_c", "compiled");
        Write(game, "panorama/images/coin_png.vtex_c", "compiled");
        Write(game, "panorama/layout/menu.vxml_c", "compiled");
        Write(game, "panorama/images/star_png.vtex_c", "compiled");
        Write(game, "panorama/images/photo_jpg.vtex_c", "compiled");
        Write(game, "sounds/beep.vsnd_c", "compiled");
        Write(game, "models/knife.vmdl_c", "compiled");
        Write(game, "tools_asset_info.bin", "valve");
        Write(game, "_bakeresourcecache/x.dat", "valve");
    }

    [Fact]
    public async Task PlanClassifiesSkipPrebuiltAndDecompile()
    {
        using var kit = new ProjectKit();
        WriteFakeAddon(kit, "hud");
        var import = new ImportService(kit.Projects, kit.Env, kit.Git);

        var plan = await import.PlanAsync("hud", kit.Repo("hud"), decompile: true);
        var actions = plan.Items.ToDictionary(item => item.Path, item => (item.Action, item.Target));

        Assert.Equal((ImportAction.CopySource, "content/panorama/layout/hud.xml"), actions["content/panorama/layout/hud.xml"]);
        Assert.Equal((ImportAction.Skip, (string?)null), actions["game/panorama/layout/hud.vxml_c"]);
        Assert.Equal((ImportAction.Skip, (string?)null), actions["game/panorama/images/coin_png.vtex_c"]);
        Assert.Equal((ImportAction.Skip, (string?)null), actions["game/tools_asset_info.bin"]);
        Assert.Equal((ImportAction.Skip, (string?)null), actions["game/_bakeresourcecache/x.dat"]);
        Assert.Equal((ImportAction.Decompile, "content/panorama/layout/menu.xml"), actions["game/panorama/layout/menu.vxml_c"]);
        Assert.Equal((ImportAction.Decompile, "content/panorama/images/star.png"), actions["game/panorama/images/star_png.vtex_c"]);
        Assert.Equal((ImportAction.Prebuilt, "prebuilt/panorama/images/photo_jpg.vtex_c"), actions["game/panorama/images/photo_jpg.vtex_c"]);
        // not a readable sound, so it can not be told apart as wav or mp3
        Assert.Equal((ImportAction.Prebuilt, "prebuilt/sounds/beep.vsnd_c"), actions["game/sounds/beep.vsnd_c"]);
        Assert.Equal((ImportAction.Prebuilt, "prebuilt/models/knife.vmdl_c"), actions["game/models/knife.vmdl_c"]);
        Assert.Equal(4, plan.SourceFiles);

        var plain = await import.PlanAsync("hud", kit.Repo("hud"), decompile: false);
        Assert.DoesNotContain(plain.Items, item => item.Action == ImportAction.Decompile);
        Assert.Equal(ImportAction.Prebuilt, plain.Items.Single(item => item.Path == "game/panorama/layout/menu.vxml_c").Action);
    }

    [UnixFact]
    public async Task RunCopiesFallsBackToPrebuiltBacksUpAndLinks()
    {
        using var kit = new ProjectKit();
        WriteFakeAddon(kit, "hud");
        var import = new ImportService(kit.Projects, kit.Env, kit.Git);
        var repo = kit.Repo("hud");
        var progress = new List<ImportProgressEvent>();
        var sink = new DelegateEventSink((name, data) =>
        {
            if (name == EventNames.ImportProgress)
            {
                lock (progress)
                {
                    progress.Add((ImportProgressEvent)data);
                }
            }
        });

        var result = await import.RunAsync("hud", repo, decompile: true, sink);

        Assert.Equal(2, result.Copied);
        Assert.Equal(0, result.Decompiled);
        Assert.Equal(5, result.Prebuilt);
        Assert.Equal(4, result.Skipped);
        Assert.Equal(2, result.Warnings.Count(warning => warning.StartsWith("Could not decompile")));
        Assert.Equal("<root/>", File.ReadAllText(Path.Combine(repo, "content", "panorama", "layout", "hud.xml")));
        Assert.True(File.Exists(Path.Combine(repo, "prebuilt", "panorama", "layout", "menu.vxml_c")));
        Assert.True(File.Exists(Path.Combine(repo, "prebuilt", "models", "knife.vmdl_c")));
        Assert.False(File.Exists(Path.Combine(repo, "prebuilt", "tools_asset_info.bin")));

        // the old content folder is renamed, the build dir is untouched, the link points at the repo
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(Path.Combine(result.BackupPath!, "panorama", "layout", "hud.xml")));
        Assert.True(File.Exists(Path.Combine(kit.BuildDir("hud"), "models", "knife.vmdl_c")));
        Assert.Equal(LinkState.Linked, result.Project.Link.State);
        Assert.Equal("hud", result.Project.Config.Addon);
        Assert.Equal("Hud", result.Project.Config.Title);
        Assert.Equal(7, progress[^1].Done);
        Assert.Equal(7, progress[^1].Total);
    }
}
