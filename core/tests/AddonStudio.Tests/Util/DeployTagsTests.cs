using AddonStudio.Core.Util;

namespace AddonStudio.Tests.Util;

public sealed class DeployTagsTests
{
    [Fact]
    public void NameAndMessageRoundTrip()
    {
        var at = new DateTimeOffset(2026, 11, 22, 15, 4, 5, TimeSpan.FromHours(2));
        var name = DeployTags.Name(at);

        Assert.Equal("deploy/20261122-130405", name);

        var message = DeployTags.Message("3389501123", 12, 34567, "\n  Fixed the HUD  \nsecond line");
        Assert.Equal("Published to Steam Workshop item 3389501123\n\nfiles: 12\nsize: 34567\nnote: Fixed the HUD", message);

        var deploy = DeployTags.Parse(name, message)!;
        Assert.Equal(name, deploy.Tag);
        Assert.Equal(at, deploy.At);
        Assert.Equal("3389501123", deploy.WorkshopId);
        Assert.Equal("Fixed the HUD", deploy.Note);
    }

    [Fact]
    public void ParseHandlesOtherTags()
    {
        Assert.Null(DeployTags.Parse("v1.0.0", "x"));
        Assert.Null(DeployTags.Parse("deploy/garbage", null));

        var fallback = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var deploy = DeployTags.Parse("deploy/garbage", null, fallback)!;
        Assert.Equal(fallback, deploy.At);
        Assert.Null(deploy.WorkshopId);
        Assert.Null(deploy.Note);
    }

    [Fact]
    public void StatePathsNames()
    {
        var repo = Path.Combine("x", "repo");
        Assert.Equal(Path.Combine(repo, ".addonstudio", "build-state.json"), StatePaths.BuildState(repo));
        Assert.Equal(Path.Combine(repo, ".addonstudio", "hash-cache.json"), StatePaths.HashCache(repo));
        Assert.Equal(Path.Combine(repo, ".addonstudio", "logs"), StatePaths.Logs(repo));

        var local = new DateTimeOffset(2026, 11, 22, 9, 8, 7, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 22, 9, 8, 7)));
        Assert.Equal("next_hud.addonstudio-backup-20261122-090807", StatePaths.LinkBackupName("next_hud", local));
        Assert.Equal(Path.Combine(repo, ".addonstudio", "export", "20261122-090807"), StatePaths.NewExportDir(repo, local));
    }
}
