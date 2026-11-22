using System.Globalization;
using System.Text;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Git;

/// <summary>git, git lfs and gh through GitRunner (SPEC 6.8).</summary>
public sealed class GitService : IGitService
{
    private static readonly TimeSpan LfsCacheTime = TimeSpan.FromMinutes(1);

    private readonly object lfsGate = new();
    private (bool Available, DateTimeOffset At)? lfsCache;

    public GitService()
    {
    }

    public async Task<GitStatus> StatusAsync(string repo, CancellationToken ct = default)
    {
        var lfs = await LfsAvailableAsync(ct).ConfigureAwait(false);

        if (!IsRepo(repo))
        {
            return GitStatus.NotARepo(lfs);
        }

        var output = await GitRunner.RunAsync(repo, ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"], read: true, ct: ct).ConfigureAwait(false);
        var (header, entries) = GitParsers.ParseStatus(output);
        var lfsPaths = await LfsPathsAsync(repo, entries.Select(entry => entry.Path).ToList(), ct).ConfigureAwait(false);
        var files = new List<GitFileChange>(entries.Count);

        foreach (var entry in entries)
        {
            files.Add(new GitFileChange(
                entry.Path,
                entry.OrigPath,
                entry.Index,
                entry.Worktree,
                GitParsers.IsStaged(entry.Index),
                lfsPaths.Contains(entry.Path),
                FileSize(repo, entry.Path)));
        }

        return new GitStatus(true, header.Branch, header.Upstream, header.Ahead, header.Behind, files, IsMerging(repo), lfs);
    }

    public async Task<GitStatus> StageAsync(string repo, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        RequireRepo(repo);
        var paths = SafePaths(repo, files);

        if (paths.Count > 0)
        {
            await GitRunner.RunBatchedAsync(repo, ["--literal-pathspecs", "add", "-A"], paths, ct).ConfigureAwait(false);
        }

        return await StatusAsync(repo, ct).ConfigureAwait(false);
    }

    public async Task<GitStatus> UnstageAsync(string repo, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        RequireRepo(repo);
        var paths = SafePaths(repo, files);

        if (paths.Count > 0)
        {
            var status = await StatusAsync(repo, ct).ConfigureAwait(false);
            var byPath = status.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var targets = new List<string>();

            foreach (var path in paths)
            {
                if (byPath.TryGetValue(path, out var file) && file.Staged)
                {
                    targets.Add(path);

                    // unstaging a rename also puts its old path back
                    if (file.OrigPath != null && !targets.Contains(file.OrigPath))
                    {
                        targets.Add(file.OrigPath);
                    }
                }
            }

            if (targets.Count > 0)
            {
                var hasCommits = await HeadAsync(repo, ct).ConfigureAwait(false) != null;

                string[] prefix = hasCommits
                    ? ["--literal-pathspecs", "reset", "-q", "HEAD"]
                    : ["--literal-pathspecs", "rm", "--cached", "-r", "-q", "--ignore-unmatch"];

                await GitRunner.RunBatchedAsync(repo, prefix, targets, ct).ConfigureAwait(false);
            }
        }

        return await StatusAsync(repo, ct).ConfigureAwait(false);
    }

    public async Task<GitStatus> DiscardAsync(string repo, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        RequireRepo(repo);
        var paths = SafePaths(repo, files);

        if (paths.Count == 0)
        {
            return await StatusAsync(repo, ct).ConfigureAwait(false);
        }

        var status = await StatusAsync(repo, ct).ConfigureAwait(false);
        var byPath = status.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var hasCommits = await HeadAsync(repo, ct).ConfigureAwait(false) != null;
        var restore = new List<string>();
        var dropFromIndex = new List<string>();
        var clean = new List<string>();

        // only files git lists as changed are touched, anything else (ignored, unchanged, unknown) is left alone
        foreach (var path in paths)
        {
            if (!byPath.TryGetValue(path, out var file) || file.Index == GitState.Ignored)
            {
                continue;
            }

            if (file.Index == GitState.Untracked)
            {
                clean.Add(path);
            }
            else if (!hasCommits)
            {
                // nothing to restore from: a staged file is new, unstage it and delete it like an untracked one
                dropFromIndex.Add(path);
                clean.Add(path);
            }
            else
            {
                restore.Add(path);

                if (file.OrigPath != null && !restore.Contains(file.OrigPath))
                {
                    restore.Add(file.OrigPath);
                }
            }
        }

        if (restore.Count > 0)
        {
            // no-overlay restore from HEAD: modified and deleted files come back, files added in the index go away
            await GitRunner.RunBatchedAsync(repo, ["--literal-pathspecs", "restore", "--source=HEAD", "--staged", "--worktree"], restore, ct).ConfigureAwait(false);
        }

        if (dropFromIndex.Count > 0)
        {
            await GitRunner.RunBatchedAsync(repo, ["--literal-pathspecs", "rm", "--cached", "-r", "-q", "--ignore-unmatch"], dropFromIndex, ct).ConfigureAwait(false);
        }

        if (clean.Count > 0)
        {
            await GitRunner.RunBatchedAsync(repo, ["--literal-pathspecs", "clean", "-f", "-q"], clean, ct).ConfigureAwait(false);
        }

        return await StatusAsync(repo, ct).ConfigureAwait(false);
    }

    public async Task<CommitResult> CommitAsync(CommitRequest request, IEventSink events, CancellationToken ct = default)
    {
        var repo = request.Path;
        RequireRepo(repo);

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw AddonStudioException.InvalidParams("The commit message is empty.");
        }

        if (request.Files != null)
        {
            var paths = SafePaths(repo, request.Files);

            if (paths.Count > 0)
            {
                await GitRunner.RunBatchedAsync(repo, ["--literal-pathspecs", "add", "-A"], paths, ct).ConfigureAwait(false);
            }
        }

        // the message goes through stdin, so any text and length is safe
        await CommitStdinAsync(repo, request.Message, ct).ConfigureAwait(false);

        var hash = await HeadAsync(repo, ct).ConfigureAwait(false) ?? throw AddonStudioException.GitFailed("The commit did not create a HEAD.");
        var pushed = false;

        if (request.Push)
        {
            try
            {
                await PushAsync(repo, events, ct).ConfigureAwait(false);
                pushed = true;
            }
            catch (AddonStudioException exception) when (exception.Code == ErrorCodes.GitFailed)
            {
                // the commit stands, the UI sees the failure in the progress log and in the ahead count
                Log.Warn($"Push after commit failed in {repo}: {exception.Message}");
                events.Emit(EventNames.GitProgress, new GitProgressEvent("push failed", null, exception.Message));
            }
        }

        AutoPublishMatch? autoPublish = null;
        var config = ReadConfig(repo);

        if (config is { AutoPublish.Enabled: true } && GitParsers.MatchVersion(config.AutoPublish.Pattern, GitParsers.Subject(request.Message)) is { } version)
        {
            autoPublish = new AutoPublishMatch(version);
        }

        var status = await StatusAsync(repo, ct).ConfigureAwait(false);

        return new CommitResult(hash, pushed, autoPublish, status);
    }

    public async Task<GitStatus> PushAsync(string repo, IEventSink events, CancellationToken ct = default)
    {
        RequireRepo(repo);
        var status = await StatusAsync(repo, ct).ConfigureAwait(false);
        List<string> args = ["push", "--follow-tags", "--progress"];

        if (status.Upstream == null)
        {
            var remote = await DefaultRemoteAsync(repo, ct).ConfigureAwait(false)
                ?? throw AddonStudioException.GitFailed("This repo has no remote to push to. Add one or put it on GitHub first.");

            if (status.Branch == null)
            {
                throw AddonStudioException.GitFailed("HEAD is detached, check out a branch to push.");
            }

            args.AddRange(["-u", remote, "HEAD"]);
        }

        await RunWithProgressAsync(repo, args, events, ct).ConfigureAwait(false);

        return await StatusAsync(repo, ct).ConfigureAwait(false);
    }

    public async Task<GitStatus> PullAsync(string repo, IEventSink events, CancellationToken ct = default)
    {
        RequireRepo(repo);
        await RunWithProgressAsync(repo, ["pull", "--ff-only", "--progress"], events, ct).ConfigureAwait(false);

        return await StatusAsync(repo, ct).ConfigureAwait(false);
    }

    public async Task<GitStatus> FetchAsync(string repo, IEventSink events, CancellationToken ct = default)
    {
        RequireRepo(repo);
        await RunWithProgressAsync(repo, ["fetch", "--progress"], events, ct).ConfigureAwait(false);

        return await StatusAsync(repo, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitCommit>> LogAsync(string repo, int limit, int skip, CancellationToken ct = default)
    {
        if (!IsRepo(repo) || await HeadAsync(repo, ct).ConfigureAwait(false) == null)
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 1000);
        skip = Math.Max(0, skip);

        var output = await GitRunner.RunAsync(repo,
            ["log", "-z", "--no-color", "--format=" + GitParsers.LogFormat, "-n", limit.ToString(CultureInfo.InvariantCulture),
                "--skip", skip.ToString(CultureInfo.InvariantCulture), "HEAD"],
            read: true, ct: ct).ConfigureAwait(false);

        var tags = await TagsAsync(repo, ct).ConfigureAwait(false);
        var pattern = ReadConfig(repo)?.AutoPublish.Pattern ?? AutoPublishConfig.DefaultPattern;

        return GitParsers.ParseLog(output).Select(raw => ToCommit(raw, tags, pattern)).ToList();
    }

    public async Task<GitCommitDetail> ShowAsync(string repo, string hash, CancellationToken ct = default)
    {
        RequireRepo(repo);

        if (string.IsNullOrWhiteSpace(hash) || hash.StartsWith('-'))
        {
            throw AddonStudioException.InvalidParams($"Not a commit: \"{hash}\".");
        }

        var resolved = await GitRunner.RunRawAsync(repo, ["rev-parse", "--verify", "--quiet", hash.Trim() + "^{commit}"], read: true, ct: ct).ConfigureAwait(false);

        if (!resolved.Succeeded || resolved.Stdout.Trim().Length == 0)
        {
            throw AddonStudioException.NotFound($"Commit \"{hash}\" was not found.");
        }

        var full = resolved.Stdout.Trim();
        var logOutput = await GitRunner.RunAsync(repo, ["log", "-1", "-z", "--no-color", "--format=" + GitParsers.LogFormat, full], read: true, ct: ct).ConfigureAwait(false);
        var raw = GitParsers.ParseLog(logOutput).FirstOrDefault() ?? throw AddonStudioException.NotFound($"Commit \"{hash}\" was not found.");
        var tags = await TagsAsync(repo, ct).ConfigureAwait(false);
        var pattern = ReadConfig(repo)?.AutoPublish.Pattern ?? AutoPublishConfig.DefaultPattern;

        // the first parent's diff (a merge shows what it brought in), the whole tree for a root commit
        string[] range = raw.Parents.Count > 0 ? [raw.Parents[0], full] : ["--root", full];
        var nameStatus = await GitRunner.RunAsync(repo, ["diff-tree", "--no-commit-id", "-r", "-z", "-M", "--name-status", .. range], read: true, ct: ct).ConfigureAwait(false);
        var numstat = await GitRunner.RunAsync(repo, ["diff-tree", "--no-commit-id", "-r", "-z", "-M", "--numstat", .. range], read: true, ct: ct).ConfigureAwait(false);

        return new GitCommitDetail(ToCommit(raw, tags, pattern), GitParsers.ParseCommitFiles(nameStatus, numstat));
    }

    public async Task InitAsync(string repo, CancellationToken ct = default)
    {
        Directory.CreateDirectory(repo);
        await GitRunner.RunAsync(repo, ["init", "-q"], ct: ct).ConfigureAwait(false);
    }

    public async Task LfsInstallLocalAsync(string repo, CancellationToken ct = default)
    {
        if (!await LfsAvailableAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        await GitRunner.RunAsync(repo, ["lfs", "install", "--local"], ct: ct).ConfigureAwait(false);
    }

    public async Task CloneAsync(string url, string path, IEventSink events, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || url.TrimStart().StartsWith('-'))
        {
            throw AddonStudioException.InvalidParams($"Not a repo URL: \"{url}\".");
        }

        var target = Paths.Full(path);
        var parent = Path.GetDirectoryName(target);

        if (parent != null)
        {
            Directory.CreateDirectory(parent);
        }

        await RunWithProgressAsync(parent, ["clone", "--progress", "--", url.Trim(), target], events, ct).ConfigureAwait(false);

        if (await LfsAvailableAsync(ct).ConfigureAwait(false))
        {
            await RunWithProgressAsync(target, ["lfs", "pull"], events, ct).ConfigureAwait(false);
        }
    }

    public async Task CreateTagAsync(string repo, string name, string message, string? target = null, CancellationToken ct = default)
    {
        RequireRepo(repo);

        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
        {
            throw AddonStudioException.InvalidParams($"Not a tag name: \"{name}\".");
        }

        List<string> args = ["tag", "-a", name, "-F", "-"];

        if (target != null)
        {
            if (target.StartsWith('-'))
            {
                throw AddonStudioException.InvalidParams($"Not a commit: \"{target}\".");
            }

            args.Add(target);
        }

        await GitRunner.RunAsync(repo, args, stdin: message, ct: ct).ConfigureAwait(false);
    }

    public async Task PushTagsAsync(string repo, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        RequireRepo(repo);

        if (tags.Count == 0)
        {
            return;
        }

        var remote = await DefaultRemoteAsync(repo, ct).ConfigureAwait(false) ?? throw AddonStudioException.GitFailed("This repo has no remote to push to.");

        await GitRunner.RunAsync(repo, ["push", remote, .. tags.Select(tag => "refs/tags/" + tag)], ct: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitTag>> TagsAsync(string repo, CancellationToken ct = default)
    {
        if (!IsRepo(repo))
        {
            return [];
        }

        var output = await GitRunner.RunAsync(repo, ["for-each-ref", "refs/tags", "--format=" + GitParsers.TagFormat], read: true, ct: ct).ConfigureAwait(false);

        return GitParsers.ParseTags(output);
    }

    public async Task<string?> RemoteUrlAsync(string repo, CancellationToken ct = default)
    {
        if (!IsRepo(repo))
        {
            return null;
        }

        var remote = await DefaultRemoteAsync(repo, ct).ConfigureAwait(false);

        if (remote == null)
        {
            return null;
        }

        var result = await GitRunner.RunRawAsync(repo, ["remote", "get-url", remote], read: true, ct: ct).ConfigureAwait(false);

        return result.Succeeded && result.Stdout.Trim().Length > 0 ? result.Stdout.Trim() : null;
    }

    public async Task<string?> HeadAsync(string repo, CancellationToken ct = default)
    {
        if (!IsRepo(repo))
        {
            return null;
        }

        var result = await GitRunner.RunRawAsync(repo, ["rev-parse", "--verify", "--quiet", "HEAD"], read: true, ct: ct).ConfigureAwait(false);
        var hash = result.Stdout.Trim();

        return result.Succeeded && hash.Length > 0 ? hash : null;
    }

    public async Task<ProjectGitInfo> InfoAsync(string repo, CancellationToken ct = default)
    {
        if (!IsRepo(repo))
        {
            return new ProjectGitInfo(false, null, null, false);
        }

        var branchResult = await GitRunner.RunRawAsync(repo, ["symbolic-ref", "--short", "-q", "HEAD"], read: true, ct: ct).ConfigureAwait(false);
        var branch = branchResult.Succeeded && branchResult.Stdout.Trim().Length > 0 ? branchResult.Stdout.Trim() : null;
        var remote = await RemoteUrlAsync(repo, ct).ConfigureAwait(false);
        var lfs = false;

        if (await LfsAvailableAsync(ct).ConfigureAwait(false))
        {
            var filter = await GitRunner.RunRawAsync(repo, ["config", "--get", "filter.lfs.process"], read: true, ct: ct).ConfigureAwait(false);
            lfs = filter.Succeeded && filter.Stdout.Trim().Length > 0;
        }

        return new ProjectGitInfo(true, branch, remote, lfs);
    }

    public async Task<bool> LfsAvailableAsync(CancellationToken ct = default)
    {
        lock (lfsGate)
        {
            if (lfsCache is { } cached && DateTimeOffset.UtcNow - cached.At < LfsCacheTime)
            {
                return cached.Available;
            }
        }

        bool available;

        try
        {
            var result = await GitRunner.RunRawAsync(null, ["lfs", "version"], read: true, ct: ct).ConfigureAwait(false);
            available = result.Succeeded;
        }
        catch (AddonStudioException)
        {
            available = false;
        }

        lock (lfsGate)
        {
            lfsCache = (available, DateTimeOffset.UtcNow);
        }

        return available;
    }

    public async Task<string> CreateGithubReleaseAsync(string repo, string tag, string title, string notes, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        RequireRepo(repo);

        if (string.IsNullOrWhiteSpace(tag) || tag.StartsWith('-'))
        {
            throw AddonStudioException.InvalidParams($"Not a tag name: \"{tag}\".");
        }

        List<string> args = ["release", "create", tag, .. files, "--title", title, "--notes", notes];

        // gh makes the tag on GitHub when it is not pushed yet: point it at the tag's own commit, never the default branch
        var commit = await GitRunner.RunRawAsync(repo, ["rev-parse", "--verify", "--quiet", "refs/tags/" + tag + "^{commit}"], read: true, ct: ct).ConfigureAwait(false);

        if (commit.Succeeded && commit.Stdout.Trim().Length > 0)
        {
            args.AddRange(["--target", commit.Stdout.Trim()]);
        }

        var result = await GitRunner.RunToolAsync("gh", repo, args, ct).ConfigureAwait(false)
            ?? throw AddonStudioException.GitFailed("gh (GitHub CLI) is not installed. Install it from https://cli.github.com and run \"gh auth login\" once.");

        if (!result.Succeeded)
        {
            throw AddonStudioException.GitFailed($"gh release create failed: {result.ErrorText}");
        }

        var url = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        return url ?? result.Stdout.Trim();
    }

    /// <summary>
    /// git.publishRepo: gh repo create [&lt;owner&gt;/]&lt;name&gt; --private|--public --source &lt;repo&gt; --remote origin --push.
    /// gh refuses to push a repo with no commits, so a fresh one gets its first commit here.
    /// </summary>
    public async Task PublishRepoAsync(string repo, string? owner, string name, bool isPrivate, string? description, IEventSink events, CancellationToken ct = default)
    {
        RequireRepo(repo);
        name = name.Trim();

        if (!RepoNamePattern.IsMatch(name))
        {
            throw AddonStudioException.InvalidParams($"\"{name}\" is not a valid repo name: use letters, digits, '-', '_' or '.', up to 100 characters.");
        }

        if (owner != null && !OwnerPattern.IsMatch(owner))
        {
            throw AddonStudioException.InvalidParams($"\"{owner}\" is not a valid GitHub account or organization name.");
        }

        if (await RemoteUrlAsync(repo, ct).ConfigureAwait(false) is { } existing)
        {
            throw new AddonStudioException(ErrorCodes.Conflict, $"This repo already has a remote ({existing}).");
        }

        if (await HeadAsync(repo, ct).ConfigureAwait(false) == null)
        {
            events.Emit(EventNames.GitProgress, new GitProgressEvent("commit", null, "Creating the first commit"));

            if (await CommitAllAsync(repo, "First commit", ct).ConfigureAwait(false) == null)
            {
                throw AddonStudioException.InvalidParams("The project has no files to push yet.");
            }
        }

        var fullName = owner == null ? name : $"{owner}/{name}";
        events.Emit(EventNames.GitProgress, new GitProgressEvent("create", null,
            $"Creating {(isPrivate ? "private" : "public")} GitHub repo {fullName} and pushing (big LFS files can take a while)"));

        var args = new List<string> { "repo", "create", fullName, isPrivate ? "--private" : "--public", "--source", Paths.Full(repo), "--remote", "origin", "--push" };

        if (!string.IsNullOrWhiteSpace(description))
        {
            args.AddRange(["--description", description.Trim()]);
        }

        var result = await GitRunner.RunToolAsync("gh", repo, args, ct).ConfigureAwait(false)
            ?? throw AddonStudioException.GitFailed(GhMissing);

        if (!result.Succeeded)
        {
            throw AddonStudioException.GitFailed($"gh repo create failed: {result.ErrorText}");
        }

        events.Emit(EventNames.GitProgress, new GitProgressEvent("done", 100, $"Created {fullName} on GitHub and pushed"));
    }

    /// <summary>git.githubOwners: gh api user for the login, gh api user/orgs for the orgs (empty when that call fails).</summary>
    public async Task<GithubOwners> GithubOwnersAsync(CancellationToken ct = default)
    {
        var user = await GitRunner.RunToolAsync("gh", null, ["api", "user", "--jq", ".login"], ct).ConfigureAwait(false)
            ?? throw AddonStudioException.GitFailed(GhMissing);

        if (!user.Succeeded || string.IsNullOrWhiteSpace(user.Stdout))
        {
            throw AddonStudioException.GitFailed($"gh is not logged in to github.com. Run \"gh auth login\" once. ({user.ErrorText})");
        }

        var orgs = await GitRunner.RunToolAsync("gh", null, ["api", "user/orgs", "--paginate", "--jq", ".[].login"], ct).ConfigureAwait(false);
        var orgList = orgs is { Succeeded: true }
            ? orgs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        if (orgs is { Succeeded: false })
        {
            Log.Warn($"gh api user/orgs failed, listing only the user: {orgs.ErrorText}");
        }

        return new GithubOwners(user.Stdout.Trim(), orgList);
    }

    /// <summary>git add -A, then a commit when anything is staged. Null when there was nothing to commit.</summary>
    public async Task<string?> CommitAllAsync(string repo, string message, CancellationToken ct = default)
    {
        RequireRepo(repo);
        await GitRunner.RunAsync(repo, ["add", "-A"], ct: ct).ConfigureAwait(false);

        // exit 0 = nothing staged (works on a branch with no commits too)
        var staged = await GitRunner.RunRawAsync(repo, ["diff", "--cached", "--quiet"], ct: ct).ConfigureAwait(false);

        if (staged.ExitCode == 0)
        {
            return null;
        }

        await CommitStdinAsync(repo, message, ct).ConfigureAwait(false);

        return await HeadAsync(repo, ct).ConfigureAwait(false);
    }

    /// <summary>git commit -F - with the message on stdin; a missing name/email becomes a clear error.</summary>
    private static async Task CommitStdinAsync(string repo, string message, CancellationToken ct)
    {
        var result = await GitRunner.RunRawAsync(repo, ["commit", "-q", "-F", "-"], stdin: message, ct: ct).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return;
        }

        var text = result.ErrorText;

        if (text.Contains("tell me who you are", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("empty ident", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable to auto-detect email", StringComparison.OrdinalIgnoreCase))
        {
            throw AddonStudioException.GitFailed(MissingIdentity);
        }

        throw GitRunner.Failed(["commit"], result);
    }

    internal const string MissingIdentity =
        "Git does not know your name and email yet, so it can not commit. Run these two once in a terminal, then commit again: " +
        "git config --global user.name \"Your Name\" and git config --global user.email \"you@example.com\" (the email of your GitHub account).";

    private const string GhMissing = "gh (GitHub CLI) is not installed. Install it from https://cli.github.com and run \"gh auth login\" once.";

    private static readonly System.Text.RegularExpressions.Regex RepoNamePattern = new(@"^[A-Za-z0-9._-]{1,100}$");

    private static readonly System.Text.RegularExpressions.Regex OwnerPattern = new(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$");

    private static GitCommit ToCommit(GitParsers.RawCommit raw, IReadOnlyList<GitTag> allTags, string pattern)
    {
        var tags = allTags.Where(tag => string.Equals(tag.Commit, raw.Hash, StringComparison.OrdinalIgnoreCase)).ToList();
        var version = tags.Select(tag => GitParsers.TagVersion(tag.Name)).FirstOrDefault(value => value != null)
            ?? GitParsers.MatchVersion(pattern, raw.Subject);
        var deploys = tags
            .Select(tag => DeployTags.Parse(tag.Name, tag.Message, tag.Date))
            .OfType<Deploy>()
            .OrderByDescending(deploy => deploy.At)
            .ToList();

        return new GitCommit(raw.Hash, raw.Short, raw.Subject, raw.Body, raw.AuthorName, raw.Date, raw.Parents, raw.Refs,
            tags.Select(tag => tag.Name).ToList(), version, deploys);
    }

    private static async Task RunWithProgressAsync(string? workingDirectory, IReadOnlyList<string> args, IEventSink events, CancellationToken ct)
    {
        string? last = null;

        void OnLine(string line)
        {
            if (line == last)
            {
                return;
            }

            last = line;

            if (GitParsers.ParseProgress(line) is { } progress)
            {
                events.Emit(EventNames.GitProgress, progress);
            }
        }

        await GitRunner.RunAsync(workingDirectory, args, onStderr: OnLine, ct: ct).ConfigureAwait(false);
    }

    /// <summary>origin when there is one, else the first remote, null without remotes.</summary>
    private static async Task<string?> DefaultRemoteAsync(string repo, CancellationToken ct)
    {
        var result = await GitRunner.RunRawAsync(repo, ["remote"], read: true, ct: ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return null;
        }

        var remotes = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return remotes.Contains("origin", StringComparer.Ordinal) ? "origin" : remotes.FirstOrDefault();
    }

    /// <summary>The paths among these that .gitattributes sends through the lfs filter (git check-attr filter).</summary>
    private static async Task<HashSet<string>> LfsPathsAsync(string repo, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var lfs = new HashSet<string>(StringComparer.Ordinal);

        if (paths.Count == 0)
        {
            return lfs;
        }

        var input = new StringBuilder();

        foreach (var path in paths)
        {
            input.Append(path).Append('\0');
        }

        var result = await GitRunner.RunRawAsync(repo, ["check-attr", "-z", "--stdin", "filter"], read: true, stdin: input.ToString(), ct: ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return lfs;
        }

        // path NUL attribute NUL value NUL
        var fields = result.Stdout.Split('\0');

        for (var index = 0; index + 2 < fields.Length; index += 3)
        {
            if (fields[index + 2] == "lfs")
            {
                lfs.Add(fields[index]);
            }
        }

        return lfs;
    }

    /// <summary>Repo-relative paths as git wants them. invalid_params for anything that could leave the repo or touch .git.</summary>
    private static List<string> SafePaths(string repo, IReadOnlyList<string>? files)
    {
        var result = new List<string>();

        foreach (var file in files ?? [])
        {
            var normalized = Paths.NormalizeRelative(file);
            Paths.SafeCombine(repo, normalized);

            if (normalized.Split('/')[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                throw AddonStudioException.InvalidParams($"\"{file}\" is inside .git.");
            }

            if (!result.Contains(normalized, StringComparer.Ordinal))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static bool IsRepo(string repo)
    {
        return !string.IsNullOrWhiteSpace(repo) && (Directory.Exists(Path.Combine(repo, ".git")) || File.Exists(Path.Combine(repo, ".git")));
    }

    private static void RequireRepo(string repo)
    {
        if (!IsRepo(repo))
        {
            throw AddonStudioException.GitFailed($"\"{repo}\" is not a git repo.");
        }
    }

    /// <summary>The git dir: .git, or the folder a .git file (worktree, submodule) points to.</summary>
    private static string? GitDir(string repo)
    {
        var dotGit = Path.Combine(repo, ".git");

        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        try
        {
            var line = File.ReadAllText(dotGit).Trim();

            if (line.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                var target = line["gitdir:".Length..].Trim();

                return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(repo, target));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool IsMerging(string repo)
    {
        var gitDir = GitDir(repo);

        return gitDir != null && File.Exists(Path.Combine(gitDir, "MERGE_HEAD"));
    }

    private static long? FileSize(string repo, string relative)
    {
        try
        {
            var info = new FileInfo(Path.Combine(repo, Paths.ToNative(relative)));

            return info.Exists ? info.Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>workshop.json for the autoPublish pattern, null when it is missing or broken.</summary>
    private static WorkshopConfig? ReadConfig(string repo)
    {
        try
        {
            return Json.TryReadFile<WorkshopConfig>(Path.Combine(repo, WorkshopConfig.FileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
