namespace AddonStudio.Core.Contracts;

/// <summary>A file that goes into the VPK. Path is build-relative with forward slashes. Change is null when nothing was published yet.</summary>
public sealed record PackFile(string Path, long Size, FileChange? Change, PackOrigin Origin);

/// <summary>PackContents.byType entry, the same grouping as AddonContents.AssetTypes.</summary>
public sealed record PackTypeStat(string Label, int Count, long Size);

/// <summary>PackContents.lastPublish</summary>
public sealed record PackLastPublish(DateTimeOffset At, string WorkshopId, string? Commit);

/// <summary>PackContents.changes</summary>
public sealed record PackChanges(int Added, int Changed, int Removed);

/// <summary>PackContents.whitelistDropped entry: a build dir file the default mode packs and Valve's whitelist leaves out.</summary>
public sealed record PackWhitelistFile(string Path, long Size);

/// <summary>PackContents.skipped entry: a build dir file the default mode leaves out, and why.</summary>
public sealed record PackSkippedFile(string Path, long Size, PackSkipReason Reason);

/// <summary>
/// pack.contents result. Removed = paths in the last publish that are not packed now. LimitBytes = the maxPackBytes setting.
/// ValveWhitelist = the project's pack mode. WhitelistDropped = files the default mode packs that Valve's whitelist would
/// drop (packed in the default mode, not packed with valveWhitelist). Skipped = what the default mode leaves out (Valve's
/// tool files and hidden files are not listed); only meaningful in the default mode.
/// </summary>
public sealed record PackContents(
    IReadOnlyList<PackFile> Files,
    IReadOnlyList<string> Removed,
    int FileCount,
    long TotalSize,
    long LimitBytes,
    IReadOnlyList<PackTypeStat> ByType,
    PackLastPublish? LastPublish,
    PackChanges Changes,
    bool ValveWhitelist,
    IReadOnlyList<PackWhitelistFile> WhitelistDropped,
    IReadOnlyList<PackSkippedFile> Skipped);

/// <summary>pack.export result. Files are full paths of the written files (_dir.vpk, chunks, publish_data.txt).</summary>
public sealed record PackExportResult(string Dir, IReadOnlyList<string> Files, long TotalSize);
