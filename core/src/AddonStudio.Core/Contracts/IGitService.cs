namespace AddonStudio.Core.Contracts;

/// <summary>
/// Shells out to git, git lfs and gh (SPEC 6.8): always -c core.quotepath=off, --no-optional-locks for read commands,
/// CreateNoWindow, UTF-8. Failures are git_failed with git's stderr in the message. Repo = the repo folder.
/// </summary>
public interface IGitService
{
    /// <summary>git.status (--porcelain=v2 -z --branch). A folder that is not a repo gives GitStatus.NotARepo.</summary>
    Task<GitStatus> StatusAsync(string repo, CancellationToken ct = default);

    /// <summary>git.stage</summary>
    Task<GitStatus> StageAsync(string repo, IReadOnlyList<string> files, CancellationToken ct = default);

    /// <summary>git.unstage</summary>
    Task<GitStatus> UnstageAsync(string repo, IReadOnlyList<string> files, CancellationToken ct = default);

    /// <summary>git.discard: restores tracked files, deletes untracked ones with git clean -f -- &lt;files&gt; (the UI confirms first).</summary>
    Task<GitStatus> DiscardAsync(string repo, IReadOnlyList<string> files, CancellationToken ct = default);

    /// <summary>git.commit: stages request.files first (null = what is staged), commits, pushes when asked, checks autoPublish.</summary>
    Task<CommitResult> CommitAsync(CommitRequest request, IEventSink events, CancellationToken ct = default);

    /// <summary>git.push (--follow-tags), git.progress events.</summary>
    Task<GitStatus> PushAsync(string repo, IEventSink events, CancellationToken ct = default);

    /// <summary>git.pull (--ff-only), git.progress events.</summary>
    Task<GitStatus> PullAsync(string repo, IEventSink events, CancellationToken ct = default);

    /// <summary>git.fetch, git.progress events.</summary>
    Task<GitStatus> FetchAsync(string repo, IEventSink events, CancellationToken ct = default);

    /// <summary>git.log: newest first, with tags, version and deploys.</summary>
    Task<IReadOnlyList<GitCommit>> LogAsync(string repo, int limit, int skip, CancellationToken ct = default);

    /// <summary>git.show: the commit and its files (--numstat, binary when "-").</summary>
    Task<GitCommitDetail> ShowAsync(string repo, string hash, CancellationToken ct = default);

    /// <summary>git init</summary>
    Task InitAsync(string repo, CancellationToken ct = default);

    /// <summary>git lfs install --local (no-op without git-lfs).</summary>
    Task LfsInstallLocalAsync(string repo, CancellationToken ct = default);

    /// <summary>git clone --progress (git.progress events from stderr), then git lfs pull.</summary>
    Task CloneAsync(string url, string path, IEventSink events, CancellationToken ct = default);

    /// <summary>Annotated tag on target (null = HEAD).</summary>
    Task CreateTagAsync(string repo, string name, string message, string? target = null, CancellationToken ct = default);

    /// <summary>Pushes the given tags to the default remote.</summary>
    Task PushTagsAsync(string repo, IReadOnlyList<string> tags, CancellationToken ct = default);

    /// <summary>Every tag with its commit, and the message and date for annotated ones.</summary>
    Task<IReadOnlyList<GitTag>> TagsAsync(string repo, CancellationToken ct = default);

    /// <summary>URL of the default remote (origin, else the first one), null when there is none.</summary>
    Task<string?> RemoteUrlAsync(string repo, CancellationToken ct = default);

    /// <summary>HEAD's full hash, null when the repo has no commits (or is not a repo).</summary>
    Task<string?> HeadAsync(string repo, CancellationToken ct = default);

    /// <summary>isRepo, branch, remote and lfs (git-lfs installed and set up for the repo) for ProjectInfo.git.</summary>
    Task<ProjectGitInfo> InfoAsync(string repo, CancellationToken ct = default);

    /// <summary>git-lfs is installed.</summary>
    Task<bool> LfsAvailableAsync(CancellationToken ct = default);

    /// <summary>gh release create &lt;tag&gt; &lt;files&gt; --title &lt;title&gt; --notes &lt;notes&gt;, returns the release URL. git_failed when gh is missing or not logged in.</summary>
    Task<string> CreateGithubReleaseAsync(string repo, string tag, string title, string notes, IReadOnlyList<string> files, CancellationToken ct = default);

    /// <summary>
    /// git.publishRepo: gh repo create [&lt;owner&gt;/]&lt;name&gt; --private|--public --source &lt;repo&gt; --remote origin --push,
    /// with git.progress events around it. A repo without commits gets its first commit before. git_failed with gh's
    /// message when gh is missing or not logged in.
    /// </summary>
    Task PublishRepoAsync(string repo, string? owner, string name, bool isPrivate, string? description, IEventSink events, CancellationToken ct = default);

    /// <summary>git.githubOwners: gh's logged-in account plus its orgs. git_failed when gh is missing or not logged in.</summary>
    Task<GithubOwners> GithubOwnersAsync(CancellationToken ct = default);

    /// <summary>
    /// Stages everything (git add -A) and commits it. Null when there was nothing to commit. A missing git name/email is
    /// git_failed with the two config commands to run.
    /// </summary>
    Task<string?> CommitAllAsync(string repo, string message, CancellationToken ct = default);
}
