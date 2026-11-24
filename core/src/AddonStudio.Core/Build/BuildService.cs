using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Build;

/// <summary>
/// SPEC section 4 and 6.5: roots, dependencies, build-state.json, incremental compiles with resourcecompiler.exe
/// through the content junction, the prebuilt/ mirror, deleted-root cleanup and build.clean.
/// </summary>
public sealed partial class BuildService : IBuildService
{
    /// <summary>Per root file (SPEC section 4).</summary>
    public static readonly TimeSpan DefaultCompileTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Outputs count as written by this job when their write time is at most this much before it started (coarse FS clocks).</summary>
    private static readonly TimeSpan FreshnessSlack = TimeSpan.FromMilliseconds(100);

    private static readonly string[] ChildImageExtensions = ["png", "tga", "jpg", "jpeg", "psd", "exr"];

    private const int MaxMessages = 200;

    private readonly IProjectService projects;
    private readonly IEnvironmentService env;
    private readonly ISettingsStore settings;
    private readonly ConcurrentDictionary<string, byte> running = new(Paths.FsComparer);

    public BuildService(IProjectService projects, IEnvironmentService env, ISettingsStore settings)
    {
        this.projects = projects;
        this.env = env;
        this.settings = settings;
    }

    /// <summary>The per-file compile timeout, settable for tests.</summary>
    public TimeSpan CompileTimeout { get; init; } = DefaultCompileTimeout;

    public Task<BuildPlan> PlanAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() => Analyze(path, ct).ToPlan(), ct);
    }

    public async Task<BuildResult> RunAsync(string path, BuildMode mode, IReadOnlyList<string>? files, IEventSink events, CancellationToken ct = default)
    {
        var repo = projects.GetPaths(Paths.Full(path)).Repo;

        if (!running.TryAdd(repo, 0))
        {
            throw AddonStudioException.Busy("A build is already running for this project.");
        }

        try
        {
            return await Task.Run(() => RunLockedAsync(repo, mode, files, events ?? NullEventSink.Instance, ct), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            running.TryRemove(repo, out _);
        }
    }

    public Task<CleanResult> CleanAsync(string path, CancellationToken ct = default)
    {
        var paths = projects.GetPaths(Paths.Full(path));

        if (paths.BuildDir == null)
        {
            throw new AddonStudioException(ErrorCodes.Cs2Missing, "CS2 was not found, so there is no build folder to clean.");
        }

        if (!running.TryAdd(paths.Repo, 0))
        {
            throw AddonStudioException.Busy("A build is running for this project.");
        }

        try
        {
            var removed = Directory.Exists(paths.BuildDir) ? CleanDirectory(paths.BuildDir, paths.BuildDir, ct) : 0;
            new BuildState().Save(paths.Repo);

            return Task.FromResult(new CleanResult(removed));
        }
        finally
        {
            running.TryRemove(paths.Repo, out _);
        }
    }

    // ---------------------------------------------------------------- analysis

    private sealed record RootInfo(
        string Source,
        RootKind Kind,
        RootStatus Status,
        string? Reason,
        IReadOnlyList<string> Deps,
        IReadOnlyList<string> Outputs,
        string? Hash,
        BuildStateRoot? Previous)
    {
        public BuildRoot ToContract() => new(Source, Kind, Status, Reason, Deps, Outputs, Previous?.Result, Previous?.BuiltAt);
    }

    private sealed record PrebuiltInfo(string Path, long Size, DateTimeOffset Mtime, PrebuiltStatus Status, bool Conflict);

    private sealed class Analysis
    {
        public required ProjectPaths Paths { get; init; }
        public required BuildState State { get; init; }
        public required bool Cs2Ready { get; init; }
        public required List<RootInfo> Roots { get; init; }
        public required List<PrebuiltInfo> Prebuilt { get; init; }
        public required List<BuildConflict> Conflicts { get; init; }
        public required List<string> Orphans { get; init; }

        /// <summary>Build-relative outputs of the current compilable roots.</summary>
        public required HashSet<string> CompiledOutputs { get; init; }

        public BuildPlan ToPlan()
        {
            int Count(RootStatus status) => Roots.Count(root => root.Status == status);

            var counts = new BuildCounts(
                Count(RootStatus.New),
                Count(RootStatus.Changed),
                Count(RootStatus.Unchanged),
                Count(RootStatus.Deleted),
                Count(RootStatus.Unsupported),
                Count(RootStatus.Failed),
                Prebuilt.Count(entry => !entry.Conflict && entry.Status != PrebuiltStatus.Unchanged));

            return new BuildPlan(
                DateTimeOffset.UtcNow,
                Cs2Ready,
                Paths.BuildDir,
                Roots.Select(root => root.ToContract()).ToList(),
                Prebuilt.Select(entry => new PrebuiltEntry(entry.Path, entry.Size, entry.Status)).ToList(),
                Conflicts,
                Orphans,
                counts);
        }
    }

    private Analysis Analyze(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var paths = projects.GetPaths(Paths.Full(path));
        var repo = paths.Repo;
        var state = BuildState.Load(repo);
        var compiler = env.CompilerPath();
        var fingerprint = compiler == null ? null : Fingerprint(compiler, settings.Get().CompilerArgs);
        var fingerprintChanged = fingerprint != null && state.Roots.Count > 0 && !string.Equals(state.CompilerFingerprint, fingerprint, StringComparison.Ordinal);
        var buildDir = paths.BuildDir;
        var hasher = FileHasher.ForRepo(repo);

        var contentFiles = Paths.ListFiles(paths.Content);
        var fileMap = new Dictionary<string, string>(Paths.RelComparer);

        foreach (var file in contentFiles)
        {
            fileMap.TryAdd(file, file);
        }

        var roots = new List<RootInfo>();
        var current = new HashSet<string>(Paths.RelComparer);

        foreach (var source in contentFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (AssetRules.Kind(source) is not { } kind || !current.Add(source))
            {
                continue;
            }

            state.Roots.TryGetValue(source, out var previous);

            if (kind == RootKind.Map)
            {
                roots.Add(new RootInfo(source, kind, RootStatus.Unsupported, AssetRules.UnsupportedReason(source), [], [], null, previous));
                continue;
            }

            var deps = DependencyScanner.ScanFile(paths.Content, source, fileMap);
            string hash;

            try
            {
                hash = RootHash(hasher, paths.Content, source, deps);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // gone or locked right now: counts as changed so a build looks at it
                hash = "";
            }

            var outputs = AssetRules.ExpectedOutputs(source);
            var (status, reason) = RootState(previous, hash, deps, fingerprintChanged, buildDir, outputs);
            roots.Add(new RootInfo(source, kind, status, reason, deps, outputs, hash, previous));
        }

        foreach (var (source, previous) in state.Roots)
        {
            if (!current.Contains(source))
            {
                roots.Add(new RootInfo(source, AssetRules.Kind(source) ?? RootKind.Other, RootStatus.Deleted, "the source was deleted", previous.Deps, previous.Outputs, null, previous));
            }
        }

        roots.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Source, b.Source));

        // compiled outputs and the root that makes each, for conflicts with prebuilt/
        var compiledBy = new Dictionary<string, string>(Paths.RelComparer);

        foreach (var root in roots.Where(root => root.Status is not (RootStatus.Deleted or RootStatus.Unsupported)))
        {
            foreach (var output in root.Outputs.Concat(root.Previous?.Outputs ?? []))
            {
                compiledBy.TryAdd(output, root.Source);
            }
        }

        var prebuiltFiles = Paths.ListFiles(paths.Prebuilt);
        var prebuiltSet = new HashSet<string>(prebuiltFiles, Paths.RelComparer);
        var prebuilt = new List<PrebuiltInfo>();
        var conflicts = new List<BuildConflict>();

        foreach (var file in prebuiltFiles)
        {
            var info = new FileInfo(Path.Combine(paths.Prebuilt, Paths.ToNative(file)));

            if (!info.Exists)
            {
                continue;
            }

            var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            var conflict = compiledBy.TryGetValue(file, out var conflictSource);

            if (conflict)
            {
                conflicts.Add(new BuildConflict(file, conflictSource!));
            }

            PrebuiltStatus status;

            if (!state.Prebuilt.TryGetValue(file, out var recorded))
            {
                status = PrebuiltStatus.New;
            }
            else if (recorded.Size != info.Length || recorded.Mtime.UtcTicks != mtime.UtcTicks)
            {
                status = PrebuiltStatus.Changed;
            }
            else if (buildDir != null && !SameSizeFileExists(Path.Combine(buildDir, Paths.ToNative(file)), info.Length))
            {
                status = PrebuiltStatus.Changed;
            }
            else
            {
                status = PrebuiltStatus.Unchanged;
            }

            prebuilt.Add(new PrebuiltInfo(file, info.Length, mtime, status, conflict));
        }

        foreach (var (file, recorded) in state.Prebuilt)
        {
            if (!prebuiltSet.Contains(file))
            {
                prebuilt.Add(new PrebuiltInfo(file, recorded.Size, recorded.Mtime, PrebuiltStatus.Deleted, compiledBy.ContainsKey(file)));
            }
        }

        prebuilt.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));

        var orphans = new List<string>();

        if (buildDir != null && Directory.Exists(buildDir))
        {
            var explained = new HashSet<string>(compiledBy.Keys, Paths.RelComparer);
            explained.UnionWith(state.RecordedOutputs());
            explained.UnionWith(prebuiltSet);
            explained.UnionWith(state.Prebuilt.Keys);

            var images = new HashSet<string>(Paths.RelComparer);

            foreach (var root in roots.Where(root => root.Status != RootStatus.Deleted))
            {
                images.UnionWith(root.Deps);
                images.Add(root.Source);
            }

            foreach (var file in Paths.ListFiles(buildDir, AssetRules.ValveDirectories.ToList()))
            {
                if (!AssetRules.IsValveFile(file) && !explained.Contains(file) && !IsChildTexture(file, images))
                {
                    orphans.Add(file);
                }
            }
        }

        hasher.Save();

        return new Analysis
        {
            Paths = paths,
            State = state,
            Cs2Ready = paths.Cs2Path != null && compiler != null,
            Roots = roots,
            Prebuilt = prebuilt,
            Conflicts = conflicts,
            Orphans = orphans,
            CompiledOutputs = new HashSet<string>(compiledBy.Keys, Paths.RelComparer),
        };
    }

    private static (RootStatus Status, string? Reason) RootState(
        BuildStateRoot? previous, string hash, IReadOnlyList<string> deps, bool fingerprintChanged, string? buildDir, IReadOnlyList<string> outputs)
    {
        if (previous == null)
        {
            return (RootStatus.New, null);
        }

        if (previous.Result == RootResult.Error)
        {
            return (RootStatus.Failed, "the last build of it failed");
        }

        if (hash.Length == 0 || !string.Equals(previous.Hash, hash, StringComparison.Ordinal))
        {
            var sameDeps = previous.Deps.Count == deps.Count && previous.Deps.All(dep => deps.Contains(dep, Paths.RelComparer));

            return (RootStatus.Changed, sameDeps ? "the source or a dependency changed" : "its dependencies changed");
        }

        if (fingerprintChanged)
        {
            return (RootStatus.Changed, "the compiler or the compiler arguments changed");
        }

        if (buildDir != null)
        {
            foreach (var output in outputs)
            {
                if (!File.Exists(Path.Combine(buildDir, Paths.ToNative(output))))
                {
                    return (RootStatus.Changed, $"output missing: {output}");
                }
            }
        }

        return (RootStatus.Unchanged, null);
    }

    /// <summary>SHA-256 over the root's bytes plus every dependency's path and bytes, dependencies sorted.</summary>
    private static string RootHash(FileHasher hasher, string contentDir, string source, IReadOnlyList<string> deps)
    {
        var parts = new List<string> { hasher.Hash(Path.Combine(contentDir, Paths.ToNative(source))) };

        foreach (var dep in deps.OrderBy(dep => dep, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add(dep.ToLowerInvariant());

            try
            {
                parts.Add(hasher.Hash(Path.Combine(contentDir, Paths.ToNative(dep))));
            }
            catch (FileNotFoundException)
            {
                parts.Add("missing");
            }
        }

        return FileHasher.Combine(parts);
    }

    /// <summary>size + mtime of resourcecompiler.exe + compilerArgs (SPEC section 4).</summary>
    internal static string Fingerprint(string compiler, string compilerArgs)
    {
        var info = new FileInfo(compiler);

        return FileHasher.HashText($"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{compilerArgs}");
    }

    /// <summary>
    /// A texture the compiler makes for an image a material uses, e.g. materials/x/foo_png_1a2b3c4d.vtex_c for
    /// materials/x/foo.png. Explained by the root that uses the image.
    /// </summary>
    private static bool IsChildTexture(string buildRel, HashSet<string> images)
    {
        const string suffix = ".vtex_c";

        if (!buildRel.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = buildRel[..^suffix.Length];

        foreach (var extension in ChildImageExtensions)
        {
            var at = stem.LastIndexOf("_" + extension, StringComparison.OrdinalIgnoreCase);

            if (at > 0 && images.Contains(stem[..at] + "." + extension))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The child textures of a root's images that are in the build dir now.</summary>
    private static List<string> FindChildTextures(string buildDir, RootInfo root)
    {
        var result = new List<string>();

        foreach (var image in root.Deps.Append(root.Source))
        {
            var extension = Paths.Extension(image);

            if (extension.Length < 2 || !ChildImageExtensions.Contains(extension[1..]))
            {
                continue;
            }

            var slash = image.LastIndexOf('/');
            var folder = slash < 0 ? "" : image[..slash];
            var directory = Path.Combine(buildDir, Paths.ToNative(folder));

            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                var pattern = $"{Path.GetFileNameWithoutExtension(image)}_{extension[1..]}*.vtex_c";
                result.AddRange(Directory.EnumerateFiles(directory, pattern).Select(file => Paths.Relative(buildDir, file)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private static bool SameSizeFileExists(string path, long size)
    {
        var info = new FileInfo(path);

        return info.Exists && info.Length == size;
    }

    // ---------------------------------------------------------------- run

    private async Task<BuildResult> RunLockedAsync(string repo, BuildMode mode, IReadOnlyList<string>? files, IEventSink events, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var analysis = Analyze(repo, ct);
        var paths = analysis.Paths;
        var state = analysis.State;
        var stateGate = new object();

        if (paths.BuildDir == null || paths.LinkPath == null)
        {
            throw new AddonStudioException(ErrorCodes.Cs2Missing, "CS2 was not found. Set its folder in Settings or install it through Steam.");
        }

        var buildDir = paths.BuildDir;
        var linkPath = paths.LinkPath;
        var current = analysis.Roots.Where(root => root.Status != RootStatus.Deleted).ToList();

        var jobs = mode switch
        {
            BuildMode.Full => current.Where(root => root.Status != RootStatus.Unsupported).ToList(),
            BuildMode.Files => SelectFiles(current, files),
            _ => current.Where(root => root.Status is RootStatus.New or RootStatus.Changed or RootStatus.Failed).ToList(),
        };

        var appSettings = settings.Get();
        var compilerArgs = appSettings.CompilerArgs;
        var parallelism = Math.Clamp(appSettings.CompileParallelism, 1, 64);
        var compiler = "";

        if (jobs.Count > 0)
        {
            compiler = env.RequireCompilerPath();
            RequireLinked(paths.Repo);

            var fingerprint = Fingerprint(compiler, compilerArgs);

            if (!string.Equals(state.CompilerFingerprint, fingerprint, StringComparison.Ordinal))
            {
                // a new compiler invalidates every root, also the ones this build does not get to (cancel, files mode)
                if (state.CompilerFingerprint != null)
                {
                    foreach (var key in state.Roots.Keys.ToList())
                    {
                        state.Roots[key] = state.Roots[key] with { Hash = "" };
                    }
                }

                state.CompilerFingerprint = fingerprint;
                SaveState(state, paths.Repo);
            }
        }

        events.Emit(EventNames.BuildStarted, new BuildStartedEvent(paths.Repo, jobs.Count));

        var results = new BuildItemResult?[jobs.Count];
        var next = -1;

        async Task Worker()
        {
            while (!ct.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);

                if (index >= jobs.Count)
                {
                    return;
                }

                var job = jobs[index];
                events.Emit(EventNames.BuildItemStarted, new BuildItemStartedEvent(job.Source, index, jobs.Count));

                var (item, record) = await CompileAsync(compiler, compilerArgs, linkPath, buildDir, job, events, ct).ConfigureAwait(false);
                results[index] = item;

                if (record != null)
                {
                    lock (stateGate)
                    {
                        state.Roots[job.Source] = record;
                        SaveState(state, paths.Repo);
                    }
                }

                WriteItemLog(paths.Repo, item);
                events.Emit(EventNames.BuildItem, new BuildItemEvent(index, jobs.Count, item));
            }
        }

        if (jobs.Count > 0)
        {
            var workers = Enumerable.Range(0, Math.Min(parallelism, jobs.Count)).Select(_ => Task.Run(Worker, CancellationToken.None));
            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        var cancelled = ct.IsCancellationRequested;
        var items = results.Where(item => item != null).Select(item => item!).ToList();
        var removed = new List<string>();
        var prebuiltCopied = 0;

        if (!cancelled && mode != BuildMode.Files)
        {
            lock (stateGate)
            {
                prebuiltCopied = MirrorPrebuilt(analysis, buildDir, removed, items);
                RemoveDeletedRoots(analysis, buildDir, removed);
                SaveState(state, paths.Repo);
            }
        }

        var ok = !cancelled && items.All(item => item.Status != ItemStatus.Error);
        var result = new BuildResult(ok, cancelled, stopwatch.ElapsedMilliseconds, items, prebuiltCopied, removed);
        events.Emit(EventNames.BuildFinished, new BuildFinishedEvent(result));

        return result;
    }

    private static List<RootInfo> SelectFiles(List<RootInfo> current, IReadOnlyList<string>? files)
    {
        if (files == null || files.Count == 0)
        {
            throw AddonStudioException.InvalidParams("Mode \"files\" needs the content-relative roots to build in files.");
        }

        var byPath = new Dictionary<string, RootInfo>(Paths.RelComparer);

        foreach (var root in current)
        {
            byPath.TryAdd(root.Source, root);
        }

        var selected = new List<RootInfo>();
        var seen = new HashSet<string>(Paths.RelComparer);

        foreach (var file in files)
        {
            var normalized = Paths.NormalizeRelative(file);

            if (!byPath.TryGetValue(normalized, out var root) && normalized.StartsWith("content/", StringComparison.OrdinalIgnoreCase))
            {
                byPath.TryGetValue(normalized["content/".Length..], out root);
            }

            if (root == null)
            {
                throw AddonStudioException.InvalidParams($"\"{file}\" is not a compile root in content/.");
            }

            if (root.Status == RootStatus.Unsupported)
            {
                throw AddonStudioException.InvalidParams($"\"{file}\" can not be compiled: {root.Reason}.");
            }

            if (seen.Add(root.Source))
            {
                selected.Add(root);
            }
        }

        return selected;
    }

    /// <summary>
    /// The compiler output of one root in .addonstudio/logs/&lt;source with / as _&gt;.log (SPEC 3), kept for bug reports.
    /// A failed write only logs: the build result has the same text.
    /// </summary>
    internal static void WriteItemLog(string repo, BuildItemResult item)
    {
        if (item.Status == ItemStatus.Skipped && item.Log.Length == 0)
        {
            return;
        }

        try
        {
            var name = item.Source.Replace('/', '_').Replace('\\', '_') + ".log";
            var file = StatePaths.LogFile(repo, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var header = $"{item.Source}\nstatus: {item.Status.ToString().ToLowerInvariant()}, exit code: {item.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, {item.DurationMs} ms\n\n";
            File.WriteAllText(file, header + item.Log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not write the build log of {item.Source}: {exception.Message}");
        }
    }

    private void RequireLinked(string repo)
    {
        var link = projects.GetLink(repo);

        switch (link.State)
        {
            case LinkState.Linked:
                return;
            case LinkState.NoCs2:
                throw new AddonStudioException(ErrorCodes.Cs2Missing, "CS2 was not found.", link);
            case LinkState.Missing:
                throw new AddonStudioException(ErrorCodes.NotFound, "content/ is not linked into CS2 yet. Use \"Link to CS2\" first.", link);
            default:
                throw new AddonStudioException(ErrorCodes.Conflict, $"The CS2 content folder is not a link to this project ({link.Detail ?? link.State.ToString()}).", link);
        }
    }

    private async Task<(BuildItemResult Item, BuildStateRoot? Record)> CompileAsync(
        string compiler, string compilerArgs, string linkPath, string buildDir, RootInfo job, IEventSink events, CancellationToken ct)
    {
        // the compiler works out the addon from the file's location, so it gets the path through the junction
        var input = Path.Combine(linkPath, Paths.ToNative(job.Source));
        var arguments = BuildArguments(compilerArgs, input);
        var log = new LogBuffer();
        var errors = new List<string>();
        var warnings = new List<string>();
        var gate = new object();

        void OnLine(string line)
        {
            lock (gate)
            {
                log.AppendLine(line);

                if (IsErrorLine(line))
                {
                    if (errors.Count < MaxMessages)
                    {
                        errors.Add(line.Trim());
                    }
                }
                else if (IsWarningLine(line) && warnings.Count < MaxMessages)
                {
                    warnings.Add(line.Trim());
                }
            }

            events.Emit(EventNames.BuildLog, new BuildLogEvent(job.Source, line));
        }

        // only an unchanged root may keep outputs resourcecompiler calls up to date; any other must write them now
        var mustRewrite = job.Status != RootStatus.Unchanged;

        if (mustRewrite)
        {
            foreach (var line in RemoveOldOutputs(buildDir, job.Outputs))
            {
                log.AppendLine(line);
                events.Emit(EventNames.BuildLog, new BuildLogEvent(job.Source, line));
            }
        }

        var started = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        int? exitCode = null;
        var timedOut = false;

        try
        {
            var result = await ProcessRunner.RunAsync(compiler, arguments, new ProcessOptions
            {
                WorkingDirectory = Path.GetDirectoryName(compiler),
                OnStdout = OnLine,
                OnStderr = OnLine,
                Timeout = CompileTimeout,
                MaxCaptureChars = BuildItemResult.MaxLogChars,
            }, ct).ConfigureAwait(false);

            exitCode = result.ExitCode;
            timedOut = result.TimedOut;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (gate)
            {
                log.AppendLine("[cancelled]");

                return (new BuildItemResult(job.Source, ItemStatus.Skipped, stopwatch.ElapsedMilliseconds, null, [], [.. errors], [.. warnings], log.ToString()), null);
            }
        }
        catch (ProcessStartException exception)
        {
            lock (gate)
            {
                errors.Add(exception.Message);
                log.AppendLine(exception.Message);
            }
        }

        stopwatch.Stop();

        ItemStatus status;
        BuildItemResult item;

        lock (gate)
        {
            var notWritten = job.Outputs.Where(output => !IsFresh(buildDir, output, started)).ToList();
            var upToDate = ReportsUpToDate(log.ToString());

            // resourcecompiler leaves outputs it thinks are up to date alone. For an unchanged root (a full rebuild,
            // or a files build of it) exit 0, every output there and its summary saying it skipped them is a success
            // too. A new, changed or failed root never gets this: resourcecompiler has called changed sources up to
            // date and kept their old outputs, which then got packed and published.
            if (!mustRewrite && exitCode == 0 && notWritten.Count > 0 && errors.Count == 0 && upToDate
                && notWritten.All(output => File.Exists(Path.Combine(buildDir, Paths.ToNative(output)))))
            {
                notWritten.Clear();
            }

            if (timedOut)
            {
                errors.Add($"timed out after {CompileTimeout.TotalMinutes:0.#} minutes");
                status = ItemStatus.Error;
            }
            else if (exitCode is null)
            {
                status = ItemStatus.Error;
            }
            else if (exitCode != 0)
            {
                if (errors.Count == 0)
                {
                    errors.Add($"resourcecompiler exited with code {exitCode}");
                }

                status = ItemStatus.Error;
            }
            else if (notWritten.Count > 0)
            {
                errors.AddRange(notWritten.Select(output => NotWrittenError(buildDir, output, upToDate)));
                status = ItemStatus.Error;
            }
            else
            {
                status = warnings.Count > 0 ? ItemStatus.Warning : ItemStatus.Ok;
            }

            IReadOnlyList<string> written = status == ItemStatus.Error ? [] : job.Outputs;
            item = new BuildItemResult(job.Source, status, stopwatch.ElapsedMilliseconds, exitCode, written, [.. errors], [.. warnings], log.ToString());
        }

        // what this root leaves in the build dir: its outputs, the textures made for its images, and on a failure
        // whatever the last build recorded (so deleting the root later still cleans it up)
        var recorded = new HashSet<string>(job.Outputs, Paths.RelComparer);
        recorded.UnionWith(FindChildTextures(buildDir, job));

        if (status == ItemStatus.Error && job.Previous != null)
        {
            recorded.UnionWith(job.Previous.Outputs);
        }

        var record = new BuildStateRoot
        {
            Hash = job.Hash ?? "",
            Deps = job.Deps,
            Outputs = recorded.OrderBy(output => output, StringComparer.OrdinalIgnoreCase).ToList(),
            Result = status switch
            {
                ItemStatus.Error => RootResult.Error,
                ItemStatus.Warning => RootResult.Warning,
                _ => RootResult.Ok,
            },
            BuiltAt = DateTimeOffset.UtcNow,
        };

        return (item, record);
    }

    /// <summary>compilerArgs with {input} replaced; an unquoted {input} (or none at all) gets the path quoted for Windows.</summary>
    internal static string BuildArguments(string template, string input)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            template = AppSettings.DefaultCompilerArgs;
        }

        if (!template.Contains("{input}", StringComparison.Ordinal))
        {
            return template.TrimEnd() + " " + ProcessRunner.QuoteWindowsArgument(input);
        }

        return template
            .Replace("\"{input}\"", "\"" + input + "\"", StringComparison.Ordinal)
            .Replace("{input}", ProcessRunner.QuoteWindowsArgument(input), StringComparison.Ordinal);
    }

    /// <summary>SPEC: ERROR, Error:, FAILED, failed to compile. Upper-case words match exactly so "0 failed" summaries do not count.</summary>
    internal static bool IsErrorLine(string line)
    {
        return line.Contains("ERROR", StringComparison.Ordinal)
            || line.Contains("FAILED", StringComparison.Ordinal)
            || line.Contains("error:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed to compile", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWarningLine(string line)
    {
        return line.Contains("WARNING", StringComparison.Ordinal) || line.Contains("warning:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The compiler said it skipped files ("0 compiled, 0 failed, 1 skipped") or that they are up to date.</summary>
    internal static bool ReportsUpToDate(string log)
    {
        return UpToDatePattern().IsMatch(log);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b0\s+compiled\b.*\b[1-9]\d*\s+skipped\b|\bup[\s-]to[\s-]date\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UpToDatePattern();

    /// <summary>
    /// Deletes a root's outputs from the build dir before it compiles, so resourcecompiler has to write them instead of
    /// keeping old ones it calls up to date. Folders are kept (another job may be writing into them). A file that can
    /// not be deleted stays, gets a log line back, and the freshness check after the compile makes it an error.
    /// </summary>
    internal static List<string> RemoveOldOutputs(string buildDir, IReadOnlyList<string> outputs)
    {
        var lines = new List<string>();

        foreach (var output in outputs)
        {
            try
            {
                SafeDelete(buildDir, output, pruneEmptyFolders: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lines.Add($"[Addon Studio] could not delete the old {output} before compiling: {exception.Message}");
            }
        }

        return lines;
    }

    /// <summary>The error for an expected output this compile did not write: missing, or an old file left in place.</summary>
    internal static string NotWrittenError(string buildDir, string output, bool compilerSaidUpToDate)
    {
        if (!File.Exists(Path.Combine(buildDir, Paths.ToNative(output))))
        {
            return $"expected output not written: {output}";
        }

        return compilerSaidUpToDate
            ? $"old output not rebuilt: {output} (resourcecompiler called it up to date and kept the old file)"
            : $"old output not rebuilt: {output} (it is older than this build)";
    }

    private static bool IsFresh(string buildDir, string output, DateTime startedUtc)
    {
        var info = new FileInfo(Path.Combine(buildDir, Paths.ToNative(output)));

        return info.Exists && info.LastWriteTimeUtc >= startedUtc - FreshnessSlack;
    }

    private static void SaveState(BuildState state, string repo)
    {
        try
        {
            state.Save(repo);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not save the build state of {repo}: {exception.Message}");
        }
    }

    // ---------------------------------------------------------------- prebuilt mirror and cleanup

    /// <summary>Copies new and changed prebuilt files, removes deleted ones. Conflicts (compiled wins) are left alone. Failures become error items.</summary>
    private static int MirrorPrebuilt(Analysis analysis, string buildDir, List<string> removed, List<BuildItemResult> items)
    {
        var state = analysis.State;
        var copied = 0;

        foreach (var entry in analysis.Prebuilt)
        {
            if (entry.Conflict)
            {
                state.Prebuilt.Remove(entry.Path);
                continue;
            }

            try
            {
                switch (entry.Status)
                {
                    case PrebuiltStatus.New or PrebuiltStatus.Changed:
                        if (CopyPrebuilt(analysis.Paths.Prebuilt, buildDir, entry))
                        {
                            state.Prebuilt[entry.Path] = new BuildStatePrebuilt(entry.Size, entry.Mtime);
                            copied++;
                        }

                        break;
                    case PrebuiltStatus.Deleted:
                        if (SafeDelete(buildDir, entry.Path))
                        {
                            removed.Add(entry.Path);
                        }

                        state.Prebuilt.Remove(entry.Path);
                        break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var message = $"prebuilt/{entry.Path}: {exception.Message}";
                items.Add(new BuildItemResult("prebuilt/" + entry.Path, ItemStatus.Error, 0, null, [], [message], [], message));
            }
        }

        return copied;
    }

    private static bool CopyPrebuilt(string prebuiltDir, string buildDir, PrebuiltInfo entry)
    {
        if (!Paths.TryNormalizeRelative(entry.Path, out var relative) || AssetRules.IsValveFile(relative))
        {
            return false;
        }

        var source = Path.Combine(prebuiltDir, Paths.ToNative(relative));
        var target = Path.GetFullPath(Path.Combine(buildDir, Paths.ToNative(relative)));
        var folder = Path.GetDirectoryName(target)!;

        if (!Paths.IsUnder(buildDir, target) || !NoLinkBetween(buildDir, folder))
        {
            return false;
        }

        Directory.CreateDirectory(folder);

        if (File.Exists(target))
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }

        File.Copy(source, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, entry.Mtime.UtcDateTime);

        return true;
    }

    /// <summary>Removes the recorded outputs of deleted roots, except files a current root or prebuilt/ still provides.</summary>
    private static void RemoveDeletedRoots(Analysis analysis, string buildDir, List<string> removed)
    {
        var keep = new HashSet<string>(analysis.CompiledOutputs, Paths.RelComparer);
        keep.UnionWith(analysis.Prebuilt.Where(entry => entry.Status != PrebuiltStatus.Deleted).Select(entry => entry.Path));

        foreach (var root in analysis.Roots.Where(root => root.Status == RootStatus.Deleted))
        {
            var failed = false;

            foreach (var output in root.Outputs)
            {
                if (keep.Contains(output))
                {
                    continue;
                }

                try
                {
                    if (SafeDelete(buildDir, output))
                    {
                        removed.Add(output);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Could not remove {output} from the build folder: {exception.Message}");
                    failed = true;
                }
            }

            if (!failed)
            {
                analysis.State.Roots.Remove(root.Source);
            }
        }
    }

    /// <summary>
    /// Deletes one build-relative file when it is really inside the build dir (no "..", no junction or symlink on the
    /// way) and not one of Valve's, then (unless told not to) removes the folders it left empty. True when a file was deleted.
    /// </summary>
    internal static bool SafeDelete(string buildDir, string buildRel, bool pruneEmptyFolders = true)
    {
        if (!Paths.TryNormalizeRelative(buildRel, out var relative) || AssetRules.IsValveFile(relative))
        {
            return false;
        }

        var target = Path.GetFullPath(Path.Combine(buildDir, Paths.ToNative(relative)));
        var folder = Path.GetDirectoryName(target)!;

        if (!Paths.IsUnder(buildDir, target) || !NoLinkBetween(buildDir, folder) || !File.Exists(target))
        {
            return false;
        }

        var info = new FileInfo(target);

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        if (info.IsReadOnly)
        {
            info.IsReadOnly = false;
        }

        info.Delete();

        var directory = folder;

        while (pruneEmptyFolders && Paths.IsUnder(buildDir, directory) && !AssetRules.IsValveFile(Paths.Relative(buildDir, directory)))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    break;
                }

                Directory.Delete(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                break;
            }

            directory = Path.GetDirectoryName(directory)!;
        }

        return true;
    }

    /// <summary>True when no folder below root down to folder (inclusive) is a junction or symlink.</summary>
    private static bool NoLinkBetween(string root, string folder)
    {
        var current = Paths.Full(folder);

        while (Paths.IsUnder(root, current))
        {
            var info = new DirectoryInfo(current);

            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            current = Path.GetDirectoryName(current)!;
        }

        return true;
    }

    /// <summary>Deletes everything under the build dir but Valve's files and any junction or symlink (left as it is). Returns the files removed.</summary>
    private static int CleanDirectory(string buildDir, string directory, CancellationToken ct)
    {
        var removed = 0;

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList())
        {
            ct.ThrowIfCancellationRequested();

            var relative = Paths.Relative(buildDir, entry.FullName);

            if (AssetRules.IsValveFile(relative) || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is DirectoryInfo sub)
            {
                removed += CleanDirectory(buildDir, sub.FullName, ct);

                try
                {
                    if (!sub.EnumerateFileSystemInfos().Any())
                    {
                        sub.Delete();
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }

                continue;
            }

            var file = (FileInfo)entry;

            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }

            file.Delete();
            removed++;
        }

        return removed;
    }

    /// <summary>The compiler log, capped at BuildItemResult.MaxLogChars.</summary>
    private sealed class LogBuffer
    {
        private const string TruncatedNote = "[log truncated]";
        private readonly StringBuilder builder = new();
        private bool truncated;

        public void AppendLine(string line)
        {
            if (truncated)
            {
                return;
            }

            var room = BuildItemResult.MaxLogChars - TruncatedNote.Length - 1 - builder.Length;

            if (line.Length + 1 > room)
            {
                builder.Append(line.AsSpan(0, Math.Clamp(room, 0, line.Length)));
                builder.Append('\n').Append(TruncatedNote);
                truncated = true;
                return;
            }

            builder.Append(line).Append('\n');
        }

        public override string ToString() => builder.ToString();
    }
}
