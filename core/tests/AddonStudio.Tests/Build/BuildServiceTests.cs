using AddonStudio.Core.Build;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Build;

public sealed class BuildServiceTests
{
    private const string HudXml = """
        <root>
            <styles><include src="file://{resources}/styles/hud.css" /></styles>
            <Panel><Image src="file://{images}/coin.png" /></Panel>
        </root>
        """;

    private static BuildService CreateService(FakeCs2 cs2) => new(cs2.Projects, cs2.Env, cs2.Settings);

    private static RootStatus StatusOf(BuildPlan plan, string source) => plan.Roots.Single(root => root.Source == source).Status;

    [UnixFact]
    public async Task Plan_statuses_dependencies_and_incremental_skip()
    {
        using var cs2 = new FakeCs2();
        cs2.WriteContent("panorama/layout/hud.xml", HudXml);
        cs2.WriteContent("panorama/styles/hud.css", ".a { color: red; }");
        cs2.WriteContent("panorama/images/coin.png", "png-1");
        cs2.WriteContent("maps/arena.vmap", "map");
        cs2.WriteContent("materials/readme.txt", "not a root");
        var service = CreateService(cs2);

        var plan = await service.PlanAsync(cs2.Repo);
        Assert.True(plan.Cs2Ready);
        Assert.Equal(3, plan.Counts.New);
        Assert.Equal(1, plan.Counts.Unsupported);
        Assert.Equal(RootStatus.Unsupported, StatusOf(plan, "maps/arena.vmap"));
        var hud = plan.Roots.Single(root => root.Source == "panorama/layout/hud.xml");
        Assert.Equal(["panorama/images/coin.png", "panorama/styles/hud.css"], hud.Dependencies);
        Assert.Equal(["panorama/layout/hud.vxml_c"], hud.Outputs);

        var sink = new RecordingSink();
        var first = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, sink);
        Assert.True(first.Ok);
        Assert.Equal(3, first.Items.Count);
        Assert.All(first.Items, item => Assert.Equal(ItemStatus.Ok, item.Status));
        Assert.True(File.Exists(cs2.BuildFile("panorama/images/coin_png.vtex_c")));
        Assert.Equal(EventNames.BuildStarted, sink.Events[0].Name);
        Assert.Equal(3, ((BuildStartedEvent)sink.Events[0].Data).Total);
        Assert.Equal(EventNames.BuildFinished, sink.Events[^1].Name);
        Assert.Equal(3, sink.Events.Count(e => e.Name == EventNames.BuildItem));
        Assert.Contains(sink.Events, e => e.Name == EventNames.BuildLog);
        Assert.True(File.Exists(StatePaths.BuildState(cs2.Repo)));

        plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal(3, plan.Counts.Unchanged);
        Assert.Empty(plan.Orphans);

        var second = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        Assert.Empty(second.Items);

        // a png the xml uses changes: the png and the xml rebuild, the css does not
        cs2.WriteContent("panorama/images/coin.png", "png-2");
        plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal(RootStatus.Changed, StatusOf(plan, "panorama/images/coin.png"));
        Assert.Equal(RootStatus.Changed, StatusOf(plan, "panorama/layout/hud.xml"));
        Assert.Equal(RootStatus.Unchanged, StatusOf(plan, "panorama/styles/hud.css"));

        var third = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        Assert.Equal(["panorama/images/coin.png", "panorama/layout/hud.xml"], third.Items.Select(item => item.Source).Order().ToList());

        // a changed compiler command line makes every root changed
        cs2.Settings.Settings = cs2.Settings.Settings with { CompilerArgs = "-nop4 -f -i \"{input}\"" };
        plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal(3, plan.Counts.Changed);
    }

    [UnixFact]
    public async Task Failed_roots_rebuild_and_deleted_roots_are_cleaned()
    {
        using var cs2 = new FakeCs2();
        cs2.WriteContent("panorama/layout/bad.xml", "<root>FAILME</root>");
        cs2.WriteContent("panorama/styles/old.css", ".a {}");
        cs2.WriteContent("panorama/scripts/warn.js", "// WARNME");
        var service = CreateService(cs2);

        var first = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        Assert.False(first.Ok);
        var bad = first.Items.Single(item => item.Source == "panorama/layout/bad.xml");
        Assert.Equal(ItemStatus.Error, bad.Status);
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains(bad.Errors, line => line.Contains("failed to compile", StringComparison.Ordinal));
        Assert.Equal(ItemStatus.Warning, first.Items.Single(item => item.Source == "panorama/scripts/warn.js").Status);
        // "0 failed" in the summary line is not an error
        Assert.Empty(first.Items.Single(item => item.Source == "panorama/styles/old.css").Errors);

        // every compiled root leaves its compiler output in .addonstudio/logs/
        var badLog = StatePaths.LogFile(cs2.Repo, "panorama_layout_bad.xml.log");
        Assert.True(File.Exists(badLog));
        Assert.Contains("failed to compile", File.ReadAllText(badLog), StringComparison.Ordinal);

        var plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal(RootStatus.Failed, StatusOf(plan, "panorama/layout/bad.xml"));
        Assert.Equal(1, plan.Counts.Failed);

        // failed roots are built again by a changed build even when nothing changed
        var retry = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        Assert.Equal(["panorama/layout/bad.xml"], retry.Items.Select(item => item.Source).ToList());

        cs2.WriteContent("panorama/layout/bad.xml", "<root>fixed</root>");
        var fixedRun = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        Assert.True(fixedRun.Ok);

        // delete a root: its output goes, nothing else does
        File.Delete(cs2.ContentFile("panorama/styles/old.css"));
        plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal(RootStatus.Deleted, StatusOf(plan, "panorama/styles/old.css"));

        var cleanup = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        Assert.Equal(["panorama/styles/old.vcss_c"], cleanup.Removed);
        Assert.False(File.Exists(cs2.BuildFile("panorama/styles/old.vcss_c")));
        Assert.False(Directory.Exists(cs2.BuildFile("panorama/styles")));
        Assert.True(File.Exists(cs2.BuildFile("panorama/layout/bad.vxml_c")));
        Assert.DoesNotContain("panorama/styles/old.css", BuildState.Load(cs2.Repo).Roots.Keys);
    }

    [UnixFact]
    public async Task Prebuilt_is_mirrored_compiled_wins_and_orphans_are_listed()
    {
        using var cs2 = new FakeCs2();
        cs2.WriteContent("panorama/layout/hud.xml", "<root>compiled</root>");
        cs2.WritePrebuilt("models/coin.vmdl_c", "model");
        cs2.WritePrebuilt("panorama/layout/hud.vxml_c", "prebuilt copy");
        cs2.WriteBuild("materials/stray.vmat_c", "nobody made me");
        cs2.WriteBuild("tools_asset_info.bin", "valve");
        cs2.WriteBuild("_bakeresourcecache/x.bin", "valve");
        var service = CreateService(cs2);

        var plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal([new BuildConflict("panorama/layout/hud.vxml_c", "panorama/layout/hud.xml")], plan.Conflicts);
        Assert.Equal(["materials/stray.vmat_c"], plan.Orphans);
        Assert.Equal(PrebuiltStatus.New, plan.Prebuilt.Single(entry => entry.Path == "models/coin.vmdl_c").Status);
        Assert.Equal(1, plan.Counts.PrebuiltChanged);

        var result = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        Assert.True(result.Ok);
        Assert.Equal(1, result.PrebuiltCopied);
        Assert.Equal("model", File.ReadAllText(cs2.BuildFile("models/coin.vmdl_c")));
        Assert.Equal("<root>compiled</root>", File.ReadAllText(cs2.BuildFile("panorama/layout/hud.vxml_c")));

        plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal(PrebuiltStatus.Unchanged, plan.Prebuilt.Single(entry => entry.Path == "models/coin.vmdl_c").Status);
        Assert.Equal(0, plan.Counts.PrebuiltChanged);

        File.Delete(Path.Combine(cs2.Prebuilt, "models", "coin.vmdl_c"));
        var removed = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        Assert.Equal(["models/coin.vmdl_c"], removed.Removed);
        Assert.False(File.Exists(cs2.BuildFile("models/coin.vmdl_c")));
        Assert.True(File.Exists(cs2.BuildFile("tools_asset_info.bin")));
    }

    [UnixFact]
    public async Task Cancel_kills_the_compiler_and_keeps_finished_items()
    {
        using var cs2 = new FakeCs2();
        cs2.Settings.Settings = cs2.Settings.Settings with { CompileParallelism = 1 };
        cs2.WriteContent("panorama/a_first.css", ".a {}");
        cs2.WriteContent("panorama/b_slow.css", "/* SLOWME */");
        cs2.WriteContent("panorama/c_never.css", ".c {}");
        var service = CreateService(cs2);

        using var cts = new CancellationTokenSource();
        var sink = new RecordingSink();
        sink.Emitted += (name, data) =>
        {
            if (name == EventNames.BuildItemStarted && ((BuildItemStartedEvent)data).Source == "panorama/b_slow.css")
            {
                cts.CancelAfter(300);
            }
        };

        var started = DateTime.UtcNow;
        var result = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, sink, cts.Token);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
        Assert.True(result.Cancelled);
        Assert.False(result.Ok);
        Assert.Equal(ItemStatus.Ok, result.Items.Single(item => item.Source == "panorama/a_first.css").Status);
        Assert.Equal(ItemStatus.Skipped, result.Items.Single(item => item.Source == "panorama/b_slow.css").Status);
        Assert.DoesNotContain(result.Items, item => item.Source == "panorama/c_never.css");
        Assert.Equal(EventNames.BuildFinished, sink.Events[^1].Name);

        // the finished item was saved, the others still need a build
        var plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal(RootStatus.Unchanged, StatusOf(plan, "panorama/a_first.css"));
        Assert.Equal(RootStatus.New, StatusOf(plan, "panorama/c_never.css"));
    }

    [UnixFact]
    public async Task Clean_keeps_valve_files_and_resets_the_state()
    {
        using var cs2 = new FakeCs2();
        cs2.WriteContent("panorama/layout/hud.xml", "<root/>");
        var service = CreateService(cs2);
        await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        cs2.WriteBuild("materials/stray.vmat_c", "x");
        cs2.WriteBuild("tools_asset_info.bin", "valve");
        cs2.WriteBuild("readonly_tools_asset_info.bin", "valve");
        cs2.WriteBuild("_vrad3/cache.dat", "valve");

        var clean = await service.CleanAsync(cs2.Repo);

        Assert.Equal(2, clean.Removed);
        Assert.Equal(["_vrad3/cache.dat", "readonly_tools_asset_info.bin", "tools_asset_info.bin"], Paths.ListFiles(cs2.BuildDir).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(cs2.BuildFile("panorama")));
        Assert.True(File.Exists(cs2.ContentFile("panorama/layout/hud.xml")));
        Assert.Equal(RootStatus.New, StatusOf(await service.PlanAsync(cs2.Repo), "panorama/layout/hud.xml"));
    }

    [UnixFact]
    public async Task Changed_and_new_roots_with_old_outputs_are_recompiled_even_when_the_compiler_calls_them_up_to_date()
    {
        using var cs2 = new FakeCs2();
        cs2.WriteContent("panorama/styles/hud.css", ".card { top: 0; }");
        var service = CreateService(cs2);
        Assert.True((await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance)).Ok);
        Assert.Equal(".card { top: 0; }", File.ReadAllText(cs2.BuildFile("panorama/styles/hud.vcss_c")));
        AgeOutput(cs2, "panorama/styles/hud.vcss_c");

        // from here on the compiler keeps any output that exists, like resourcecompiler did with the live addon
        cs2.CompilerSkipsExisting = true;
        cs2.WriteContent("panorama/styles/hud.css", ".card { bottom: 0; }");
        // a new root whose output is already in the build folder from some older build
        cs2.WriteContent("panorama/styles/feed.css", ".feed { left: 0; }");
        cs2.WriteBuild("panorama/styles/feed.vcss_c", ".feed { old: 1; }");
        AgeOutput(cs2, "panorama/styles/feed.vcss_c");

        var plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal(RootStatus.Changed, StatusOf(plan, "panorama/styles/hud.css"));
        Assert.Equal(RootStatus.New, StatusOf(plan, "panorama/styles/feed.css"));

        var result = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);

        Assert.True(result.Ok);
        Assert.All(result.Items, item => Assert.Equal(ItemStatus.Ok, item.Status));
        Assert.Equal(".card { bottom: 0; }", File.ReadAllText(cs2.BuildFile("panorama/styles/hud.vcss_c")));
        Assert.Equal(".feed { left: 0; }", File.ReadAllText(cs2.BuildFile("panorama/styles/feed.vcss_c")));
    }

    [UnixFact]
    public async Task Changed_root_whose_old_output_survives_the_compile_is_an_error()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cs2 = new FakeCs2();
        cs2.WriteContent("panorama/styles/hud.css", ".card { top: 0; }");
        var service = CreateService(cs2);
        Assert.True((await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance)).Ok);
        AgeOutput(cs2, "panorama/styles/hud.vcss_c");

        cs2.CompilerSkipsExisting = true;
        cs2.WriteContent("panorama/styles/hud.css", ".card { bottom: 0; }");

        // the old output can not be deleted (a read-only folder here, a file the game holds open on Windows)
        var folder = cs2.BuildFile("panorama/styles");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        BuildResult result;

        try
        {
            result = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // never a silent success: publish refuses a build with error items
        Assert.False(result.Ok);
        var item = Assert.Single(result.Items);
        Assert.Equal(ItemStatus.Error, item.Status);
        Assert.Empty(item.Outputs);
        Assert.Equal(["old output not rebuilt: panorama/styles/hud.vcss_c (resourcecompiler called it up to date and kept the old file)"], item.Errors);
        Assert.Contains("could not delete the old panorama/styles/hud.vcss_c", item.Log, StringComparison.Ordinal);
        Assert.Equal(".card { top: 0; }", File.ReadAllText(cs2.BuildFile("panorama/styles/hud.vcss_c")));
        Assert.Equal(RootStatus.Failed, StatusOf(await service.PlanAsync(cs2.Repo), "panorama/styles/hud.css"));

        // once the file can go, the next build rewrites it
        var retry = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);
        Assert.True(retry.Ok);
        Assert.Equal(".card { bottom: 0; }", File.ReadAllText(cs2.BuildFile("panorama/styles/hud.vcss_c")));
    }

    [UnixFact]
    public async Task Changed_root_whose_output_the_compiler_does_not_write_is_an_error()
    {
        using var cs2 = new FakeCs2();
        cs2.WriteContent("panorama/styles/hud.css", ".card { top: 0; }");
        var service = CreateService(cs2);
        Assert.True((await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance)).Ok);

        // a compiler that exits 0 and says it skipped the file, without writing anything
        File.WriteAllText(cs2.Compiler, "#!/bin/sh\necho \"0 compiled, 0 failed, 1 skipped\"\nexit 0\n");
        cs2.WriteContent("panorama/styles/hud.css", ".card { bottom: 0; }");

        var result = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);

        Assert.False(result.Ok);
        var item = Assert.Single(result.Items);
        Assert.Equal(ItemStatus.Error, item.Status);
        Assert.Equal(["expected output not written: panorama/styles/hud.vcss_c"], item.Errors);
        Assert.False(File.Exists(cs2.BuildFile("panorama/styles/hud.vcss_c")));
    }

    [UnixFact]
    public async Task Unchanged_root_the_compiler_calls_up_to_date_is_kept_on_a_full_build()
    {
        using var cs2 = new FakeCs2();
        cs2.WriteContent("panorama/styles/hud.css", ".card { top: 0; }");
        var service = CreateService(cs2);
        Assert.True((await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance)).Ok);
        AgeOutput(cs2, "panorama/styles/hud.vcss_c");
        var before = File.GetLastWriteTimeUtc(cs2.BuildFile("panorama/styles/hud.vcss_c"));

        cs2.CompilerSkipsExisting = true;
        var result = await service.RunAsync(cs2.Repo, BuildMode.Full, null, NullEventSink.Instance);

        Assert.True(result.Ok);
        var item = Assert.Single(result.Items);
        Assert.Equal(ItemStatus.Ok, item.Status);
        Assert.Equal(["panorama/styles/hud.vcss_c"], item.Outputs);
        Assert.Equal(before, File.GetLastWriteTimeUtc(cs2.BuildFile("panorama/styles/hud.vcss_c")));
    }

    [UnixFact]
    public async Task Build_state_from_version_1_rebuilds_every_root_and_still_cleans_deleted_ones()
    {
        using var cs2 = new FakeCs2();
        cs2.WriteContent("panorama/styles/hud.css", ".card { bottom: 0; }");
        cs2.WriteContent("panorama/styles/old.css", ".old {}");
        var service = CreateService(cs2);
        Assert.True((await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance)).Ok);

        // what a version 1 build could leave: the new hash recorded as ok next to the old output
        cs2.WriteBuild("panorama/styles/hud.vcss_c", ".card { top: 0; }");
        AgeOutput(cs2, "panorama/styles/hud.vcss_c");
        var stateFile = StatePaths.BuildState(cs2.Repo);
        var json = File.ReadAllText(stateFile);
        var downgraded = System.Text.RegularExpressions.Regex.Replace(json, "\"version\"\\s*:\\s*2", "\"version\":1");
        Assert.NotEqual(json, downgraded);
        File.WriteAllText(stateFile, downgraded);
        // and a source removed before the first build with the new version
        File.Delete(cs2.ContentFile("panorama/styles/old.css"));

        cs2.CompilerSkipsExisting = true;
        var plan = await service.PlanAsync(cs2.Repo);
        Assert.Equal(RootStatus.Changed, StatusOf(plan, "panorama/styles/hud.css"));
        Assert.Equal(RootStatus.Deleted, StatusOf(plan, "panorama/styles/old.css"));

        var result = await service.RunAsync(cs2.Repo, BuildMode.Changed, null, NullEventSink.Instance);

        Assert.True(result.Ok);
        Assert.Equal(".card { bottom: 0; }", File.ReadAllText(cs2.BuildFile("panorama/styles/hud.vcss_c")));
        // the old output of the removed source is not left behind to be packed
        Assert.Equal(["panorama/styles/old.vcss_c"], result.Removed);
        Assert.False(File.Exists(cs2.BuildFile("panorama/styles/old.vcss_c")));
        Assert.Equal(BuildState.CurrentVersion, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(stateFile))!["version"]!.GetValue<int>());
    }

    /// <summary>Moves a build output's write time into the past, so it is clearly older than the next compile job.</summary>
    private static void AgeOutput(FakeCs2 cs2, string buildRel)
    {
        File.SetLastWriteTimeUtc(cs2.BuildFile(buildRel), DateTime.UtcNow.AddMinutes(-10));
    }

    [Fact]
    public void Compiler_up_to_date_summary_counts_as_success()
    {
        Assert.True(BuildService.ReportsUpToDate("0 compiled, 0 failed, 1 skipped"));
        Assert.True(BuildService.ReportsUpToDate("panorama/layout/hud.vxml is up to date"));
        Assert.False(BuildService.ReportsUpToDate("1 compiled, 0 failed, 0 skipped"));
        Assert.False(BuildService.ReportsUpToDate("0 compiled, 1 failed, 0 skipped"));
    }

    [Fact]
    public void Dependency_scanner_reads_panorama_and_kv3_references()
    {
        var files = new[]
        {
            "panorama/images/icons/Coin.png", "panorama/styles/base.css", "panorama/scripts/hud.js",
            "materials/x/color.tga", "sounds/ui/click.wav", "models/x/body.fbx",
        }.ToDictionary(file => file, file => file, Paths.RelComparer);

        var css = DependencyScanner.Scan("panorama/styles/hud.css", """
            @import url("file://{resources}/styles/base.css");
            .a { background-image: url("s2r://panorama/images/icons/coin_png.vtex"); }
            """, files);
        Assert.Equal(["panorama/images/icons/Coin.png", "panorama/styles/base.css"], css);

        var xml = DependencyScanner.Scan("panorama/layout/hud.xml", """<scripts><include src="file://{resources}/scripts/hud.js" /></scripts>""", files);
        Assert.Equal(["panorama/scripts/hud.js"], xml);

        var vmat = DependencyScanner.Scan("materials/x/x.vmat", """
            TextureColor "materials/x/color.tga"
            Missing "materials/x/missing.png"
            """, files);
        Assert.Equal(["materials/x/color.tga"], vmat);

        var events = DependencyScanner.Scan("soundevents/ui.vsndevts", """{ vsnd_files = [ "sounds/ui/click.vsnd" ] }""", files);
        Assert.Equal(["sounds/ui/click.wav"], events);
    }
}
