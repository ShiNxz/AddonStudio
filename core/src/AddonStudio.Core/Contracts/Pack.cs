namespace AddonStudio.Core.Contracts;

/// <summary>A file that goes into the VPK. Path is build-relative with forward slashes. Change is null when nothing was published yet.</summary>
public sealed record PackFile(string Path, long Size, FileChange? Change, PackOrigin Origin);

/// <summary>PackContents.byType entry, the same grouping as AddonContents.AssetTypes.</summary>
public sealed record PackTypeStat(string Label, int Count, long Size);

/// <summary>PackContents.lastPublish</summary>
public sealed record PackLastPublish(DateTimeOffset At, string WorkshopId, string? Commit);

/// <summary>PackContents.changes</summary>
public sealed record PackChanges(int Added, int Changed, int Removed);

/// <summary>pack.contents result. Removed = paths in the last publish that are not packed now. LimitBytes = AddonPackager.MaxTotalSize (3 GiB).</summary>
public sealed record PackContents(
    IReadOnlyList<PackFile> Files,
    IReadOnlyList<string> Removed,
    int FileCount,
    long TotalSize,
    long LimitBytes,
    IReadOnlyList<PackTypeStat> ByType,
    PackLastPublish? LastPublish,
    PackChanges Changes);

/// <summary>pack.export result. Files are full paths of the written files (_dir.vpk, chunks, publish_data.txt).</summary>
public sealed record PackExportResult(string Dir, IReadOnlyList<string> Files, long TotalSize);
