namespace AddonStudio.Core.Contracts;

// Paths in build DTOs are forward-slash relative paths: sources and dependencies relative to content/,
// outputs, prebuilt entries, conflicts and orphans relative to the build dir.

/// <summary>A compile root (SPEC section 4 table) and its state.</summary>
public sealed record BuildRoot(
    string Source,
    RootKind Kind,
    RootStatus Status,
    string? Reason,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Outputs,
    RootResult? LastResult,
    DateTimeOffset? LastBuiltAt);

/// <summary>A file in prebuilt/ and whether the build dir copy is up to date.</summary>
public sealed record PrebuiltEntry(string Path, long Size, PrebuiltStatus Status);

/// <summary>A path that is both a compiled output of Source and in prebuilt/. Compiled wins.</summary>
public sealed record BuildConflict(string Path, string Source);

/// <summary>BuildPlan.counts</summary>
public sealed record BuildCounts(int New, int Changed, int Unchanged, int Deleted, int Unsupported, int Failed, int PrebuiltChanged);

/// <summary>build.plan result.</summary>
public sealed record BuildPlan(
    DateTimeOffset GeneratedAt,
    bool Cs2Ready,
    string? BuildDir,
    IReadOnlyList<BuildRoot> Roots,
    IReadOnlyList<PrebuiltEntry> Prebuilt,
    IReadOnlyList<BuildConflict> Conflicts,
    IReadOnlyList<string> Orphans,
    BuildCounts Counts);

/// <summary>The result of compiling one root. Log is capped at MaxLogChars.</summary>
public sealed record BuildItemResult(
    string Source,
    ItemStatus Status,
    long DurationMs,
    int? ExitCode,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string Log)
{
    /// <summary>64 KB</summary>
    public const int MaxLogChars = 64 * 1024;
}

/// <summary>build.run result. Removed = build-relative files deleted from the build dir (deleted roots, removed prebuilt files).</summary>
public sealed record BuildResult(
    bool Ok,
    bool Cancelled,
    long DurationMs,
    IReadOnlyList<BuildItemResult> Items,
    int PrebuiltCopied,
    IReadOnlyList<string> Removed);

/// <summary>build.clean result: how many files were deleted.</summary>
public sealed record CleanResult(int Removed);
