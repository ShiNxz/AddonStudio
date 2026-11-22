namespace AddonStudio.Core.Contracts;

/// <summary>
/// Event names (SPEC section 7). Services emit the records below through IEventSink.Emit(name, data).
/// The records have no requestId: the host adds "requestId" to the data of every event a request's sink emits.
/// </summary>
public static class EventNames
{
    public const string BuildStarted = "build.started";
    public const string BuildItemStarted = "build.itemStarted";
    public const string BuildLog = "build.log";
    public const string BuildItem = "build.item";
    public const string BuildFinished = "build.finished";
    public const string PublishProgress = "publish.progress";
    public const string GitProgress = "git.progress";
    public const string ImportProgress = "import.progress";
    public const string WatchChanged = "watch.changed";

    public static readonly IReadOnlyList<string> All =
    [
        BuildStarted, BuildItemStarted, BuildLog, BuildItem, BuildFinished,
        PublishProgress, GitProgress, ImportProgress, WatchChanged,
    ];
}

/// <summary>build.started: Total = roots that will be compiled.</summary>
public sealed record BuildStartedEvent(string Path, int Total);

/// <summary>build.itemStarted: Index is 0-based, Source is content-relative.</summary>
public sealed record BuildItemStartedEvent(string Source, int Index, int Total);

/// <summary>build.log: one compiler output line.</summary>
public sealed record BuildLogEvent(string Source, string Line);

/// <summary>build.item: a root finished. Index is 0-based.</summary>
public sealed record BuildItemEvent(int Index, int Total, BuildItemResult Item);

/// <summary>build.finished</summary>
public sealed record BuildFinishedEvent(BuildResult Result);

/// <summary>publish.progress: Fraction 0..1 within the stage, null when unknown.</summary>
public sealed record PublishProgressEvent(PublishStage Stage, double? Fraction, string? Message);

/// <summary>git.progress: Phase like "Receiving objects", Percent 0..100 or null, Line = the raw progress line.</summary>
public sealed record GitProgressEvent(string Phase, int? Percent, string Line);

/// <summary>import.progress: Current = the file being copied or decompiled.</summary>
public sealed record ImportProgressEvent(int Done, int Total, string Current);

/// <summary>watch.changed (a global event, no requestId): Path = the repo, Files = repo-relative changed paths, forward slashes.</summary>
public sealed record WatchChangedEvent(string Path, IReadOnlyList<string> Files);
