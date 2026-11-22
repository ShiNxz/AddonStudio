using AddonStudio.Core.Contracts;
using AddonStudio.Core.Git;
using AddonStudio.Core.Publish;
using AddonStudio.Core.Util;
using AddonStudio.Tests.Git;
using CS2WorkshopManager;

namespace AddonStudio.Tests.Publish;

public sealed class PublishServiceTests
{
    private const string GameInfo = """
        "GameInfo"
        {
            AddonConfig
            {
                VpkDirectories
                {
                    include "panorama"
                    include "sounds"
                    include "a.vxml_c"
                }
            }
        }
        """;

    [Fact]
    public void Change_note_format()
    {
        const string head = "abcdef1234567890abcdef1234567890abcdef12";

        Assert.Equal("New HUD\n\n[abcdef1]", PublishService.BuildChangeNote("New HUD\n", head, dirty: false));
        Assert.Equal("New HUD\n\n[abcdef1] (uncommitted changes)", PublishService.BuildChangeNote("New HUD", head, dirty: true));
        Assert.Equal("[abcdef1]", PublishService.BuildChangeNote("", head, dirty: false));
        Assert.Equal("New HUD", PublishService.BuildChangeNote("New HUD", null, dirty: false));
        Assert.Equal("1.2.0", PublishService.NormalizeVersion("v1.2.0"));
        Assert.Throws<AddonStudioException>(() => PublishService.NormalizeVersion("1.2 beta"));
    }

    [Fact]
    public async Task Publish_uploads_writes_back_tags_and_records_last_publish()
    {
        using var repo = new GitTestRepo();
        var cs2 = Path.Combine(repo.Root, "cs2");
        var buildDir = Path.Combine(cs2, "game", "csgo_addons", "my_addon");
        Directory.CreateDirectory(Path.Combine(cs2, "game", "csgo"));
        File.WriteAllText(Path.Combine(cs2, "game", "csgo", "gameinfo.gi"), GameInfo);
        WriteFile(Path.Combine(buildDir, "panorama", "layout", "hud.vxml_c"), "compiled");
        WriteFile(Path.Combine(buildDir, "sounds", "coin.vsnd_c"), "sound!");
        WriteFile(Path.Combine(buildDir, "tools_asset_info.bin"), "valve");

        Json.WriteFile(Path.Combine(repo.Path, "workshop.json"), WorkshopConfig.CreateDefault("my_addon", "My Addon") with { Preview = null });
        repo.Write(".gitignore", ".addonstudio/\n");
        repo.CommitAll("first");

        var projects = new FakeProjects(cs2);
        var uploader = new FakeUploader(777123);
        var git = new GitService();
        var events = new List<PublishProgressEvent>();
        var sink = new DelegateEventSink((_, data) => { if (data is PublishProgressEvent progress) lock (events) events.Add(progress); });
        var now = new DateTimeOffset(2026, 11, 22, 9, 30, 15, TimeSpan.Zero);
        var publish = new PublishService(projects, new NoEnvironment(), new OkBuild(), new FakePack(), git, null!, uploader, () => now);

        var result = await publish.RunAsync(new PublishRequest(repo.Path, "First release", "1.2.0", Push: true), sink);

        var head = (await git.HeadAsync(repo.Path))!;
        Assert.Equal("777123", result.WorkshopId);
        Assert.True(result.Created);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=777123", result.Url);
        Assert.Equal(["deploy/20261122-093015", "v1.2.0"], result.Tags);
        Assert.Contains("The repo has no remote, the tags were not pushed.", result.Warnings);

        var sent = Assert.Single(uploader.Requests);
        Assert.Null(sent.PublishedFileId);
        Assert.Equal("my_addon", sent.AddonName);
        Assert.Equal($"First release\n\n[{head[..7]}]", sent.ChangeNote);
        Assert.Equal(cs2, sent.Cs2Path);

        Assert.Equal("777123", projects.LoadConfig(repo.Path).WorkshopId);

        var last = LastPublish.Read(repo.Path)!;
        Assert.Equal(now, last.At);
        Assert.Equal(head, last.Commit);
        Assert.Equal(["panorama/layout/hud.vxml_c", "sounds/coin.vsnd_c"], last.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(FileHasher.HashText("sound!"), last.Files["sounds/coin.vsnd_c"].Sha256);

        var tags = await git.TagsAsync(repo.Path);
        var deployTag = Assert.Single(tags, tag => tag.Name == "deploy/20261122-093015");
        Assert.Equal(head, deployTag.Commit);
        Assert.Equal("Published to Steam Workshop item 777123\n\nfiles: 2\nsize: 14\nnote: First release", deployTag.Message);

        var stages = events.Select(progress => progress.Stage).Distinct().ToList();
        Assert.Equal([PublishStage.Build, PublishStage.Pack, PublishStage.Upload, PublishStage.Tag, PublishStage.Push, PublishStage.Done], stages);

        // second publish: workshop.json now carries the id (and is uncommitted), v1.2.0 exists already
        var again = await publish.RunAsync(new PublishRequest(repo.Path, "Fix", "1.2.0", SkipBuild: true), NullEventSink.Instance);

        Assert.False(again.Created);
        Assert.Equal(777123ul, uploader.Requests[1].PublishedFileId);
        Assert.EndsWith("(uncommitted changes)", uploader.Requests[1].ChangeNote);
        Assert.Contains("Tag v1.2.0 already exists, it was not created again.", again.Warnings);
        Assert.Contains("Tag deploy/20261122-093015 already exists, it was not created again.", again.Warnings);
    }

    [Fact]
    public async Task Source_folder_conflict_is_a_conflict_error()
    {
        using var repo = new GitTestRepo();
        var cs2 = Path.Combine(repo.Root, "cs2");
        WriteFile(Path.Combine(cs2, "game", "csgo", "gameinfo.gi"), GameInfo);
        WriteFile(Path.Combine(cs2, "game", "csgo_addons", "my_addon", "a.vxml_c"), "x");
        Json.WriteFile(Path.Combine(repo.Path, "workshop.json"), WorkshopConfig.CreateDefault("my_addon", "My Addon") with { WorkshopId = "55", Preview = null });

        var uploader = new FakeUploader(55) { Throw = new SourceFolderConflictException(55, "old_addon", "my_addon") };
        var publish = new PublishService(new FakeProjects(cs2), new NoEnvironment(), new OkBuild(), new FakePack(), new GitService(), null!, uploader);

        var error = await Assert.ThrowsAsync<AddonStudioException>(() => publish.RunAsync(new PublishRequest(repo.Path, "x", SkipBuild: true), NullEventSink.Instance));

        Assert.Equal(ErrorCodes.Conflict, error.Code);
        Assert.False(File.Exists(StatePaths.LastPublish(repo.Path)));
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }

    private sealed class FakeUploader(ulong id) : IWorkshopUploader
    {
        public List<UploadRequest> Requests { get; } = [];

        public Exception? Throw { get; init; }

        public void EnsureReady()
        {
        }

        public Task<UploadResult> UploadAsync(UploadRequest request, IProgress<float> progress, CancellationToken ct)
        {
            Requests.Add(request);

            if (Throw != null)
            {
                throw Throw;
            }

            progress.Report(0.5f);
            progress.Report(1f);

            return Task.FromResult(new UploadResult(id, false));
        }
    }

    private sealed class FakeProjects(string cs2) : IProjectService
    {
        public ProjectPaths GetPaths(string repo)
        {
            var addon = LoadConfig(repo).Addon;

            return new ProjectPaths(repo, Path.Combine(repo, "content"), Path.Combine(repo, "prebuilt"), StatePaths.Dir(repo), addon, cs2,
                Path.Combine(cs2, "content", "csgo_addons", addon), Path.Combine(cs2, "game", "csgo_addons", addon));
        }

        public WorkshopConfig LoadConfig(string repo) => Json.ReadFile<WorkshopConfig>(Path.Combine(repo, WorkshopConfig.FileName))!;

        public Task<ProjectInfo> SaveConfigAsync(string path, WorkshopConfig config, CancellationToken ct = default)
        {
            Json.WriteFile(Path.Combine(path, WorkshopConfig.FileName), config);

            return Task.FromResult(new ProjectInfo(path, config, [], "", "", null, GetLink(path), new ProjectGitInfo(true, null, null, false)));
        }

        public LinkInfo GetLink(string repo) => new(LinkState.NoCs2, null, null, null);

        public Task<IReadOnlyList<RecentProject>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ProjectInfo> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ProjectInfo> CreateAsync(string path, string addon, string title, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ProjectInfo> CloneAsync(string url, string path, IEventSink events, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ProjectInfo> LinkAsync(string path, bool backup, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RecentProject>> RemoveAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<Cs2Addon>> ListCs2AddonsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LinkOutcome> CreateLinkAsync(string repo, bool backup, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class OkBuild : IBuildService
    {
        public Task<BuildPlan> PlanAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<BuildResult> RunAsync(string path, BuildMode mode, IReadOnlyList<string>? files, IEventSink events, CancellationToken ct = default)
        {
            Assert.Equal(BuildMode.Changed, mode);

            return Task.FromResult(new BuildResult(true, false, 1, [], 0, []));
        }

        public Task<CleanResult> CleanAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakePack : IPackService
    {
        public Task<PackContents> ContentsAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PackExportResult> ExportAsync(string path, string? outDir, CancellationToken ct = default) => throw new NotSupportedException();

        public AddonRules BuildRules(string path) => new();
    }

    private sealed class NoEnvironment : IEnvironmentService
    {
        public Os Os => Platform.Os;

        public Task<DoctorReport> DoctorAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public string? Cs2Path() => null;

        public string? CompilerPath() => null;

        public string? GameInfoPath() => null;

        public string RequireCs2Path() => throw new NotSupportedException();

        public string RequireCompilerPath() => throw new NotSupportedException();

        public bool CanCompile() => false;

        public bool CanPublish() => false;

        public void Invalidate()
        {
        }
    }
}
