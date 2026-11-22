using System.Text.Json.Nodes;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Settings;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Settings;

public sealed class SettingsStoreTests
{
    [Fact]
    public void RoundTripsSettingsAndRecentProjectsAndKeepsUnknownKeys()
    {
        using var temp = new TempDir();
        var home = Path.Combine(temp.Path, "home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.json"), """{ "futureKey": { "a": 1 }, "theme": "light" }""");

        var store = new SettingsStore(home);
        var defaults = store.Get();
        Assert.Equal(Theme.Light, defaults.Theme);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount / 2), defaults.CompileParallelism);
        Assert.Equal("-nop4 -i \"{input}\"", defaults.CompilerArgs);
        Assert.Null(defaults.Cs2PathOverride);

        var project = temp.Write("p1/workshop.json", "{}");
        store.Set(new AppSettingsPatch { CompileParallelism = 3, Cs2PathOverride = @"D:\Steam\cs2", Theme = Theme.System });
        store.TouchRecent(Path.Combine(temp.Path, "gone"), "gone", "Gone");
        store.TouchRecent(Path.GetDirectoryName(project)!, "p1", "P1");

        Assert.Throws<AddonStudioException>(() => store.Set(new AppSettingsPatch { CompileParallelism = 0 }));
        Assert.Throws<AddonStudioException>(() => store.Set(new AppSettingsPatch { CompilerArgs = "-nop4" }));

        var reloaded = new SettingsStore(home).Get();
        Assert.Equal(3, reloaded.CompileParallelism);
        Assert.Equal(@"D:\Steam\cs2", reloaded.Cs2PathOverride);
        Assert.Equal(Theme.System, reloaded.Theme);
        Assert.Equal(["p1", "gone"], reloaded.RecentProjects.Select(recent => recent.Addon));
        Assert.True(reloaded.RecentProjects[0].Exists);
        Assert.False(reloaded.RecentProjects[1].Exists);

        // null clears the override, a patch without it leaves it alone
        var cleared = new SettingsStore(home);
        cleared.Set(new AppSettingsPatch { Theme = Theme.Dark });
        Assert.NotNull(cleared.Get().Cs2PathOverride);
        cleared.Set(new AppSettingsPatch { Cs2PathOverride = null });
        Assert.Null(cleared.Get().Cs2PathOverride);

        var remaining = cleared.RemoveRecent(Path.Combine(temp.Path, "gone"));
        Assert.Single(remaining);

        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "settings.json")))!.AsObject();
        Assert.Equal(1, (int)json["futureKey"]!["a"]!);
        Assert.Null(json["recentProjects"]![0]!["exists"]);
        Assert.Empty(Directory.GetFiles(home, "*.tmp"));
    }
}
