using AddonStudio.Core.Contracts;
using AddonStudio.Core.Git;
using AddonStudio.Core.Util;

namespace AddonStudio.Tests.Git;

public sealed class GitServiceTests
{
    private readonly GitService git = new();

    [Fact]
    public void ParseStatus_reads_every_entry_kind()
    {
        var output = string.Join('\0',
            "# branch.oid 1234567890abcdef1234567890abcdef12345678",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -3",
            "1 .M N... 100644 100644 100644 aaaa bbbb content/a b.txt",
            "2 R. N... 100644 100644 100644 aaaa aaaa R100 content/new.txt",
            "content/old.txt",
            "u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflict.txt",
            "? untracked/ü.txt",
            "! ignored.log",
            "");

        var (header, entries) = GitParsers.ParseStatus(output);

        Assert.Equal(new GitParsers.StatusHeader("main", "origin/main", 2, 3, true), header);
        Assert.Equal(
        [
            new GitParsers.StatusEntry("content/a b.txt", null, GitState.Unmodified, GitState.Modified),
            new GitParsers.StatusEntry("content/new.txt", "content/old.txt", GitState.Renamed, GitState.Unmodified),
            new GitParsers.StatusEntry("conflict.txt", null, GitState.Conflicted, GitState.Conflicted),
            new GitParsers.StatusEntry("untracked/ü.txt", null, GitState.Untracked, GitState.Untracked),
            new GitParsers.StatusEntry("ignored.log", null, GitState.Ignored, GitState.Ignored),
        ], entries);
    }

    [Fact]
    public async Task Status_shows_renames_untracked_spaces_unicode_and_lfs()
    {
        using var repo = new GitTestRepo();
        repo.Write(".gitattributes", "*.png filter=lfs diff=lfs merge=lfs -text\n");
        repo.Write("content/old name.txt", "hello\n");
        repo.Write("content/keep.txt", "keep\n");
        repo.CommitAll("first");

        repo.Git("mv", "content/old name.txt", "content/new name.txt");
        repo.Write("content/keep.txt", "changed\n");
        repo.Write("content/שלום עולם.txt", "unicode\n");
        repo.Write("content/images/coin.png", "png");

        var status = await git.StatusAsync(repo.Path);

        Assert.True(status.IsRepo);
        Assert.Equal("main", status.Branch);
        Assert.False(status.Merging);

        var renamed = Assert.Single(status.Files, file => file.Path == "content/new name.txt");
        Assert.Equal("content/old name.txt", renamed.OrigPath);
        Assert.Equal(GitState.Renamed, renamed.Index);
        Assert.True(renamed.Staged);

        var modified = Assert.Single(status.Files, file => file.Path == "content/keep.txt");
        Assert.Equal(GitState.Modified, modified.Worktree);
        Assert.False(modified.Staged);
        Assert.Equal(8, modified.Size);

        var unicode = Assert.Single(status.Files, file => file.Path == "content/שלום עולם.txt");
        Assert.Equal(GitState.Untracked, unicode.Index);

        var image = Assert.Single(status.Files, file => file.Path == "content/images/coin.png");
        Assert.True(image.Lfs);
        Assert.False(unicode.Lfs);

        var notRepo = await git.StatusAsync(repo.Root);
        Assert.False(notRepo.IsRepo);
    }

    [Fact]
    public async Task Commit_detects_autoPublish_and_log_show_carry_tags_deploys_and_numstat()
    {
        using var repo = new GitTestRepo();
        Json.WriteFile(Path.Combine(repo.Path, "workshop.json"), WorkshopConfig.CreateDefault("my_addon", "My Addon") with
        {
            AutoPublish = new AutoPublishConfig { Enabled = true },
        });
        repo.Write("content/a.txt", "one\ntwo\n");
        File.WriteAllBytes(Path.Combine(repo.Path, "content", "bin.dat"), [0, 1, 2, 0, 3]);

        var first = await git.CommitAsync(new CommitRequest(repo.Path, "initial", ["workshop.json", "content/a.txt", "content/bin.dat"]), NullEventSink.Instance);
        Assert.Null(first.AutoPublish);
        Assert.Empty(first.Status.Files);

        repo.Write("content/a.txt", "one\nthree\nfour\n");
        var second = await git.CommitAsync(new CommitRequest(repo.Path, "v1.4.0 new HUD\n\nlonger body", ["content/a.txt"]), NullEventSink.Instance);
        Assert.Equal("1.4.0", second.AutoPublish?.Version);

        await git.CreateTagAsync(repo.Path, "deploy/20261122-101500", DeployTags.Message("987654", 12, 3456, "new HUD"));
        await git.CreateTagAsync(repo.Path, "v1.4.0", "v1.4.0");

        var log = await git.LogAsync(repo.Path, 10, 0);
        Assert.Equal(2, log.Count);

        var head = log[0];
        Assert.Equal(second.Hash, head.Hash);
        Assert.Equal("v1.4.0 new HUD", head.Subject);
        Assert.Equal("longer body", head.Body);
        Assert.Equal("Test User", head.AuthorName);
        Assert.Equal("1.4.0", head.Version);
        Assert.Contains("main", head.Refs);
        Assert.Equal(["deploy/20261122-101500", "v1.4.0"], head.Tags.Order());
        var deploy = Assert.Single(head.Deploys);
        Assert.Equal("987654", deploy.WorkshopId);
        Assert.Equal("new HUD", deploy.Note);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 10, 15, 0, TimeSpan.Zero), deploy.At);
        Assert.Equal([first.Hash], head.Parents);
        Assert.Null(log[1].Version);

        var paged = await git.LogAsync(repo.Path, 1, 1);
        Assert.Equal(first.Hash, Assert.Single(paged).Hash);

        var detail = await git.ShowAsync(repo.Path, head.Short);
        var changed = Assert.Single(detail.Files);
        Assert.Equal(("content/a.txt", GitState.Modified, 2, 1, false), (changed.Path, changed.Status, changed.Additions, changed.Deletions, changed.Binary));

        var root = await git.ShowAsync(repo.Path, first.Hash);
        var binary = Assert.Single(root.Files, file => file.Path == "content/bin.dat");
        Assert.True(binary.Binary);
        Assert.Null(binary.Additions);
        Assert.Equal(GitState.Added, binary.Status);
    }

    [Fact]
    public async Task Push_pull_and_tags_through_a_bare_remote()
    {
        using var repo = new GitTestRepo();
        var remote = Path.Combine(repo.Root, "remote.git");
        GitTestRepo.Init(remote, bare: true);
        repo.Git("remote", "add", "origin", remote);
        repo.Write("a.txt", "a\n");
        repo.CommitAll("first");

        var events = new List<string>();
        var sink = new DelegateEventSink((name, _) => { lock (events) events.Add(name); });
        var pushed = await git.PushAsync(repo.Path, sink);
        Assert.Equal("origin/main", pushed.Upstream);
        Assert.Equal(remote, await git.RemoteUrlAsync(repo.Path));

        var other = Path.Combine(repo.Root, "other");
        await git.CloneAsync(remote, other, sink);
        GitTestRepo.Configure(other);
        File.WriteAllText(Path.Combine(other, "b.txt"), "b\n");
        GitTestRepo.Git(other, ["add", "-A"]);
        GitTestRepo.Git(other, ["commit", "-q", "-m", "second"]);
        await git.CreateTagAsync(other, "deploy/20260101-000000", "Published to Steam Workshop item 42");
        await git.PushAsync(other, NullEventSink.Instance);
        await git.PushTagsAsync(other, ["deploy/20260101-000000"]);

        var fetched = await git.FetchAsync(repo.Path, sink);
        Assert.Equal(1, fetched.Behind);

        var pulled = await git.PullAsync(repo.Path, sink);
        Assert.Equal(0, pulled.Behind);
        Assert.True(File.Exists(Path.Combine(repo.Path, "b.txt")));

        var tag = Assert.Single(await git.TagsAsync(repo.Path));
        Assert.True(tag.Annotated);
        Assert.Equal(await git.HeadAsync(repo.Path), tag.Commit);
        Assert.Contains(EventNames.GitProgress, events);
    }

    [Fact]
    public async Task Discard_restores_tracked_deletes_untracked_and_refuses_outside_paths()
    {
        using var repo = new GitTestRepo();
        repo.Write(".gitignore", "*.log\n");
        repo.Write("tracked.txt", "original\n");
        repo.Write("gone.txt", "gone\n");
        repo.CommitAll("first");

        repo.Write("tracked.txt", "edited\n");
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        repo.Write("new file.txt", "new\n");
        repo.Write("staged-new.txt", "staged\n");
        repo.Git("add", "staged-new.txt");
        repo.Write("keep.log", "ignored\n");
        var outside = Path.Combine(repo.Root, "outside.txt");
        File.WriteAllText(outside, "outside\n");

        await Assert.ThrowsAsync<AddonStudioException>(() => git.DiscardAsync(repo.Path, ["../outside.txt"]));
        await Assert.ThrowsAsync<AddonStudioException>(() => git.DiscardAsync(repo.Path, [outside]));
        await Assert.ThrowsAsync<AddonStudioException>(() => git.DiscardAsync(repo.Path, [".git/config"]));

        var status = await git.DiscardAsync(repo.Path, ["tracked.txt", "gone.txt", "new file.txt", "staged-new.txt", "keep.log"]);

        Assert.Empty(status.Files);
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(repo.Path, "tracked.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "gone.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "new file.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "staged-new.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "keep.log")));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task CommitAll_makes_the_first_commit_and_skips_when_nothing_changed()
    {
        using var repo = new GitTestRepo();
        repo.Write("workshop.json", "{}");
        repo.Write("content/panorama/layout/hud.xml", "<root/>");

        var first = await git.CommitAllAsync(repo.Path, "Import next_hud from CS2");
        Assert.NotNull(first);
        Assert.Equal(first, await git.HeadAsync(repo.Path));
        Assert.Contains("content/panorama/layout/hud.xml", repo.Git("show", "--name-only", "--format=", "HEAD"));

        Assert.Null(await git.CommitAllAsync(repo.Path, "nothing new"));
    }

    [Fact]
    public async Task PublishRepo_checks_owner_and_name_before_calling_gh()
    {
        using var repo = new GitTestRepo();

        var badName = await Assert.ThrowsAsync<AddonStudioException>(() => git.PublishRepoAsync(repo.Path, null, "bad name", true, null, NullEventSink.Instance));
        Assert.Equal(ErrorCodes.InvalidParams, badName.Code);

        var badOwner = await Assert.ThrowsAsync<AddonStudioException>(() => git.PublishRepoAsync(repo.Path, "-org", "next-hud", true, null, NullEventSink.Instance));
        Assert.Equal(ErrorCodes.InvalidParams, badOwner.Code);
    }
}
