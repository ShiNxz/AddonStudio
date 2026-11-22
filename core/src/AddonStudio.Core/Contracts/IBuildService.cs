namespace AddonStudio.Core.Contracts;

/// <summary>Incremental compile with resourcecompiler.exe and the prebuilt mirror (SPEC 4 and 6.5).</summary>
public interface IBuildService
{
    /// <summary>build.plan</summary>
    Task<BuildPlan> PlanAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// build.run: emits build.started, build.itemStarted, build.log, build.item, build.finished. One build per project
    /// at a time (busy). Cancelling kills the compilers and returns a result with cancelled = true.
    /// </summary>
    Task<BuildResult> RunAsync(string path, BuildMode mode, IReadOnlyList<string>? files, IEventSink events, CancellationToken ct = default);

    /// <summary>build.clean: deletes the whole build dir except Valve's files. The UI runs a full build after it.</summary>
    Task<CleanResult> CleanAsync(string path, CancellationToken ct = default);
}
