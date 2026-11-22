using System.Text.Json.Nodes;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Projects;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Projects;

public sealed class ProjectServiceTests
{
    [Fact]
    public async Task CreateWritesTheLayoutAndRefusesANonEmptyFolder()
    {
        using var kit = new ProjectKit();
        var repo = kit.Repo("next_hud");

        var info = await kit.Projects.CreateAsync(repo, "next_hud", "Next HUD");

        Assert.Empty(info.ConfigErrors);
        Assert.Equal("next_hud", info.Config.Addon);
        Assert.Equal("Next HUD", info.Config.Title);
        Assert.Equal(Visibility.Private, info.Config.Visibility);
        Assert.Equal(["Custom"], info.Config.Tags);
        Assert.True(Directory.Exists(Path.Combine(repo, "content")));
        Assert.True(Directory.Exists(Path.Combine(repo, "prebuilt")));
        Assert.Equal(ProjectService.GitAttributesTemplate, File.ReadAllText(Path.Combine(repo, ".gitattributes")));
        Assert.Contains("prebuilt/** filter=lfs diff=lfs merge=lfs -text", ProjectService.GitAttributesTemplate);
        Assert.Equal(".addonstudio/\n", File.ReadAllText(Path.Combine(repo, ".gitignore")));
        Assert.Equal(LinkState.Missing, info.Link.State);
        Assert.Equal(kit.BuildDir("next_hud"), info.BuildDir);
        Assert.Contains(nameof(IGitService.InitAsync), ((FakeGit)kit.Git).Calls);

        var recent = await kit.Projects.ListAsync();
        Assert.Equal(repo, recent[0].Path);

        var error = await Assert.ThrowsAsync<AddonStudioException>(() => kit.Projects.CreateAsync(repo, "next_hud", "Again"));
        Assert.Equal(ErrorCodes.Conflict, error.Code);

        var badName = await Assert.ThrowsAsync<AddonStudioException>(() => kit.Projects.CreateAsync(kit.Repo("x"), "Bad-Name", "x"));
        Assert.Equal(ErrorCodes.InvalidParams, badName.Code);
    }

    [Fact]
    public async Task SaveKeepsUnknownKeysAndBadValuesBecomeConfigErrors()
    {
        using var kit = new ProjectKit();
        var repo = kit.Repo("hud");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "workshop.json"), """
            {
              // hand edited
              "addon": "hud",
              "workshopId": 123456789,
              "title": "Hud",
              "visibility": "everyone",
              "preview": "missing.png",
              "autoPublish": { "enabled": true, "pattern": "(unclosed", "extra": 5 },
              "custom": { "keep": [1, 2] },
            }
            """);

        var opened = await kit.Projects.OpenAsync(repo);

        Assert.Equal("123456789", opened.Config.WorkshopId);
        Assert.Equal(Visibility.Private, opened.Config.Visibility);
        Assert.True(opened.Config.AutoPublish.Enabled);
        Assert.Contains(opened.ConfigErrors, error => error.Contains("visibility"));
        Assert.Contains(opened.ConfigErrors, error => error.Contains("missing.png"));
        Assert.Contains(opened.ConfigErrors, error => error.Contains("autoPublish.pattern"));

        File.WriteAllText(Path.Combine(repo, "preview.png"), "png");
        var saved = await kit.Projects.SaveConfigAsync(repo, opened.Config with
        {
            Addon = "Bad Name",
            Visibility = Visibility.FriendsOnly,
            Preview = "preview.png",
            AutoPublish = opened.Config.AutoPublish with { Pattern = AutoPublishConfig.DefaultPattern },
        });

        Assert.StartsWith("addon", Assert.Single(saved.ConfigErrors));
        Assert.Null(saved.BuildDir);

        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "workshop.json")))!.AsObject();
        Assert.Equal("friendsOnly", (string?)json["visibility"]);
        Assert.Equal("123456789", (string?)json["workshopId"]);
        Assert.Equal(2, json["custom"]!["keep"]!.AsArray().Count);
        Assert.Equal(5, (int)json["autoPublish"]!["extra"]!);
        Assert.Equal(AutoPublishConfig.DefaultPattern, (string?)json["autoPublish"]!["pattern"]);

        var paths = Assert.Throws<AddonStudioException>(() => kit.Projects.GetPaths(repo));
        Assert.Equal(ErrorCodes.InvalidParams, paths.Code);
    }

    [UnixFact]
    public async Task LinkStatesWithAFakeCs2Tree()
    {
        using var kit = new ProjectKit();
        var repo = kit.Repo("coins");
        var info = await kit.Projects.CreateAsync(repo, "coins", "Coins");
        Assert.Equal(LinkState.Missing, info.Link.State);

        var linked = await kit.Projects.LinkAsync(repo, backup: false);
        Assert.Equal(LinkState.Linked, linked.Link.State);
        Assert.Equal(kit.ContentLinkPath("coins"), linked.Link.LinkPath);
        File.WriteAllText(Path.Combine(repo, "content", "a.txt"), "a");
        Assert.True(File.Exists(Path.Combine(kit.ContentLinkPath("coins"), "a.txt")));

        // a link somewhere else is re-pointed, its target is left alone
        var elsewhere = Path.Combine(kit.Temp.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "keep");
        ContentLink.Remove(kit.ContentLinkPath("coins"));
        Directory.CreateSymbolicLink(kit.ContentLinkPath("coins"), elsewhere);
        Assert.Equal(LinkState.WrongTarget, kit.Projects.GetLink(repo).State);
        Assert.Equal(LinkState.Linked, (await kit.Projects.LinkAsync(repo, backup: false)).Link.State);
        Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));

        // a real folder is a conflict: refused without backup, renamed (never deleted) with it
        ContentLink.Remove(kit.ContentLinkPath("coins"));
        Directory.CreateDirectory(kit.ContentLinkPath("coins"));
        File.WriteAllText(Path.Combine(kit.ContentLinkPath("coins"), "old.xml"), "old");
        Assert.Equal(LinkState.Conflict, kit.Projects.GetLink(repo).State);

        var refused = await Assert.ThrowsAsync<AddonStudioException>(() => kit.Projects.LinkAsync(repo, backup: false));
        Assert.Equal(ErrorCodes.Conflict, refused.Code);

        var outcome = await kit.Projects.CreateLinkAsync(repo, backup: true);
        Assert.Equal(LinkState.Linked, outcome.Link.State);
        Assert.NotNull(outcome.BackupPath);
        Assert.Matches(@"coins\.addonstudio-backup-\d{8}-\d{6}$", outcome.BackupPath);
        Assert.Equal("old", File.ReadAllText(Path.Combine(outcome.BackupPath!, "old.xml")));

        // ListCs2Addons sees the link and skips the backup folder
        Directory.CreateDirectory(Path.Combine(kit.BuildDir("coins"), "panorama"));
        File.WriteAllText(Path.Combine(kit.BuildDir("coins"), "panorama", "x.vxml_c"), "12345");
        Directory.CreateDirectory(Path.Combine(kit.Cs2, "game", "csgo_addons", "vpks"));
        Directory.CreateDirectory(Path.Combine(kit.Cs2, "game", "csgo_addons", "other"));

        var addons = await kit.Projects.ListCs2AddonsAsync();
        Assert.Equal(["coins", "other"], addons.Select(addon => addon.Name));
        var coins = addons[0];
        Assert.True(coins.HasContent && coins.HasGame);
        Assert.Equal(1, coins.ContentFiles);
        Assert.Equal(1, coins.GameFiles);
        Assert.Equal(6, coins.SizeBytes);
        Assert.Equal(Path.Combine(repo, "content"), coins.LinkedTo);
        Assert.False(addons[1].HasContent);
    }

    [Fact]
    public async Task NoCs2MeansNoLinkPath()
    {
        using var kit = new ProjectKit(withCs2: false);
        var repo = kit.Repo("solo");
        var info = await kit.Projects.CreateAsync(repo, "solo", "");

        Assert.Equal("solo", info.Config.Title);
        Assert.Equal(LinkState.NoCs2, info.Link.State);
        Assert.Null(info.BuildDir);
    }
}
