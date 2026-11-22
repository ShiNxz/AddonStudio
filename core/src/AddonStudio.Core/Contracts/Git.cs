namespace AddonStudio.Core.Contracts;

// Paths in git DTOs are repo-relative with forward slashes, as git prints them (core.quotepath=off).

/// <summary>A changed file. Index and Worktree are the X and Y of git status. Staged = Index is not unmodified/untracked/ignored.</summary>
public sealed record GitFileChange(
    string Path,
    string? OrigPath,
    GitState Index,
    GitState Worktree,
    bool Staged,
    bool Lfs,
    long? Size);

/// <summary>git.status result (also what stage/unstage/discard/push/pull/fetch return).</summary>
public sealed record GitStatus(
    bool IsRepo,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<GitFileChange> Files,
    bool Merging,
    bool LfsAvailable)
{
    /// <summary>The status of a folder that is not a git repo.</summary>
    public static GitStatus NotARepo(bool lfsAvailable) => new(false, null, null, 0, 0, [], false, lfsAvailable);
}

/// <summary>A deploy/* tag on a commit: when it was published and to which item.</summary>
public sealed record Deploy(string Tag, DateTimeOffset At, string? WorkshopId, string? Note);

/// <summary>A commit in git.log. Refs = branch and remote names pointing at it, Tags = tag names on it.</summary>
public sealed record GitCommit(
    string Hash,
    string Short,
    string Subject,
    string Body,
    string AuthorName,
    DateTimeOffset Date,
    IReadOnlyList<string> Parents,
    IReadOnlyList<string> Refs,
    IReadOnlyList<string> Tags,
    string? Version,
    IReadOnlyList<Deploy> Deploys);

/// <summary>GitCommitDetail.files entry. Additions/Deletions are null for binary files.</summary>
public sealed record GitCommitFile(string Path, GitState Status, int? Additions, int? Deletions, bool Binary);

/// <summary>git.show result.</summary>
public sealed record GitCommitDetail(GitCommit Commit, IReadOnlyList<GitCommitFile> Files);

/// <summary>git.commit params. Files: stage these first, null = commit what is staged.</summary>
public sealed record CommitRequest(string Path, string Message, IReadOnlyList<string>? Files = null, bool Push = false);

/// <summary>CommitResult.autoPublish: the version the commit subject matched.</summary>
public sealed record AutoPublishMatch(string Version);

/// <summary>git.commit result. AutoPublish is set when workshop.json autoPublish is enabled and the subject matches the pattern.</summary>
public sealed record CommitResult(string Hash, bool Pushed, AutoPublishMatch? AutoPublish, GitStatus Status);

/// <summary>A tag as IGitService.TagsAsync lists it (not sent over RPC). Message and Date only for annotated tags.</summary>
public sealed record GitTag(string Name, string Commit, bool Annotated, string? Message, DateTimeOffset? Date);
