using AddonStudio.Core.Contracts;
using AddonStudio.Core.Watch;
using AddonStudio.Tests.Build;

namespace AddonStudio.Tests.Watch;

public sealed class WatchServiceTests
{
    [Fact]
    public void Filter_keeps_content_and_prebuilt_only()
    {
        string[] folders = ["content", "prebuilt"];

        Assert.Equal("content/panorama/a.xml", WatchService.Filter(folders, "content/panorama/a.xml"));
        Assert.Equal("prebuilt/models/x.vmdl_c", WatchService.Filter(folders, Path.Combine("prebuilt", "models", "x.vmdl_c")));
        Assert.Null(WatchService.Filter(folders, "workshop.json"));
        Assert.Null(WatchService.Filter(folders, "contentx/a.xml"));
        Assert.Null(WatchService.Filter(folders, ".git/index"));
        Assert.Null(WatchService.Filter(folders, "content/.addonstudio/x"));
    }

    [Fact]
    public async Task Changes_are_debounced_into_one_event()
    {
        using var cs2 = new FakeCs2(withCompiler: false);
        using var watch = new WatchService(cs2.Projects) { Debounce = TimeSpan.FromMilliseconds(300) };
        var received = new TaskCompletionSource<WatchChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        watch.Changed += change =>
        {
            Interlocked.Increment(ref count);
            received.TrySetResult(change);
        };

        Assert.True((await watch.SetAsync(cs2.Repo, true)).Enabled);
        await Task.Delay(300);

        cs2.WriteContent("panorama/a.css", ".a {}");
        cs2.WriteContent("panorama/b.css", ".b {}");
        File.WriteAllText(Path.Combine(cs2.Repo, "workshop.json"), "{}");

        var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(cs2.Repo, change.Path);
        Assert.Contains("content/panorama/a.css", change.Files);
        Assert.Contains("content/panorama/b.css", change.Files);
        Assert.DoesNotContain("workshop.json", change.Files);

        Assert.False((await watch.SetAsync(cs2.Repo, false)).Enabled);
    }
}
