namespace AddonStudio.Core.Contracts;

/// <summary>
/// One file of an import plan. Path is relative to the CS2 folder it comes from, forward slashes, prefixed
/// "content/" for the addon's content folder and "game/" for its build dir. Target is repo-relative (forward slashes),
/// null for skip.
/// </summary>
public sealed record ImportItem(string Path, long Size, ImportAction Action, string? Target, string? Reason);

/// <summary>import.plan result. Path is the target repo folder.</summary>
public sealed record ImportPlan(
    string Addon,
    string Path,
    int SourceFiles,
    IReadOnlyList<ImportItem> Items,
    long TotalBytes,
    IReadOnlyList<string> Warnings);

/// <summary>import.run result.</summary>
public sealed record ImportResult(
    ProjectInfo Project,
    int Copied,
    int Decompiled,
    int Prebuilt,
    int Skipped,
    string? BackupPath,
    IReadOnlyList<string> Warnings);
