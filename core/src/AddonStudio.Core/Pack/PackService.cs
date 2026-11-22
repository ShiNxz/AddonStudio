using AddonStudio.Core.Build;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;
using CS2WorkshopManager;

namespace AddonStudio.Core.Pack;

/// <summary>
/// SPEC section 5 and 6.6: what AddonPackager.CollectFiles packs from the build dir (Valve's gameinfo rules and skips,
/// workshop.json globs first), each file's origin from build-state.json, the diff against last-publish.json, and
/// exporting the VPK chunks with our own copy of AddonPackager.Stage.
/// </summary>
public sealed class PackService : IPackService
{
    private readonly IProjectService projects;
    private readonly IEnvironmentService env;
    private readonly Func<long> limitBytes;

    /// <param name="limitBytes">The pack size cap, default AddonPackager.MaxTotalSize (3 GiB). The host can pass the maxPackBytes setting here.</param>
    public PackService(IProjectService projects, IEnvironmentService env, Func<long>? limitBytes = null)
    {
        this.projects = projects;
        this.env = env;
        this.limitBytes = limitBytes ?? (() => AddonPackager.MaxTotalSize);
    }

    public Task<PackContents> ContentsAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() => Contents(path, ct), ct);
    }

    public Task<PackExportResult> ExportAsync(string path, string? outDir, CancellationToken ct = default)
    {
        return Task.Run(() => Export(path, outDir, ct), ct);
    }

    public AddonRules BuildRules(string path)
    {
        var paths = projects.GetPaths(Paths.Full(path));
        var config = projects.LoadConfig(paths.Repo);

        return BuildRules(config.Pack, paths.BuildDir);
    }

    /// <summary>
    /// workshop.json pack globs as AddonRules: excludes first, then includes (the first matching rule decides, so an
    /// exclude wins). A glob becomes one rule per build dir file it matches; a pattern without wildcards is kept as the
    /// prefix it already is.
    /// </summary>
    internal static AddonRules BuildRules(PackConfig pack, string? buildDir)
    {
        var rules = new AddonRules();
        List<string>? files = null;

        void AddAll(IEnumerable<string> patterns, bool exclude)
        {
            foreach (var raw in patterns)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var pattern = PackGlob.Normalize(raw);

                if (!PackGlob.HasWildcards(pattern))
                {
                    if (pattern.Length > 0)
                    {
                        rules.Add(new AddonRules.Rule(exclude, pattern));
                    }

                    continue;
                }

                files ??= buildDir != null && Directory.Exists(buildDir)
                    ? AddonPackager.ListFiles(buildDir).Select(file => AddonPackager.GetRelativePath(Paths.Full(buildDir), file.FullName)).ToList()
                    : [];

                var regex = PackGlob.ToRegex(pattern);

                foreach (var file in files.Where(file => regex.IsMatch(file)))
                {
                    rules.Add(new AddonRules.Rule(exclude, file));
                }
            }
        }

        AddAll(pack.Exclude ?? [], exclude: true);
        AddAll(pack.Include ?? [], exclude: false);

        return rules;
    }

    private PackContents Contents(string path, CancellationToken ct)
    {
        var paths = projects.GetPaths(Paths.Full(path));
        var repo = paths.Repo;
        var (buildDir, gameInfo) = RequireInputs(paths, requireBuildDir: false);
        var config = projects.LoadConfig(repo);

        var files = Directory.Exists(buildDir)
            ? AddonPackager.CollectFiles(buildDir, gameInfo, BuildRules(config.Pack, buildDir))
            : [];

        var fullBuildDir = Paths.Full(buildDir);
        var state = BuildState.Load(repo);
        var compiled = state.RecordedOutputs();
        var contentFiles = new HashSet<string>(Paths.ListFiles(paths.Content), Paths.RelComparer);
        var prebuilt = new HashSet<string>(Paths.ListFiles(paths.Prebuilt), Paths.RelComparer);
        prebuilt.UnionWith(state.Prebuilt.Keys);

        var last = LastPublish.Read(repo);
        var hasher = FileHasher.ForRepo(repo);
        var packed = new List<PackFile>(files.Count);
        var current = new HashSet<string>(Paths.RelComparer);
        int added = 0, changed = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relative = AddonPackager.GetRelativePath(fullBuildDir, file.FullName);
            current.Add(relative);

            // compiled wins over prebuilt, like the build does
            var origin = compiled.Contains(relative) || AssetRules.SourceCandidates(relative).Any(contentFiles.Contains)
                ? PackOrigin.Compiled
                : prebuilt.Contains(relative) ? PackOrigin.Prebuilt : PackOrigin.Other;

            FileChange? change = null;

            if (last != null)
            {
                if (!last.Files.TryGetValue(relative, out var published))
                {
                    change = FileChange.Added;
                    added++;
                }
                else if (published.Size != file.Length || !string.Equals(published.Sha256, SafeHash(hasher, file.FullName), StringComparison.OrdinalIgnoreCase))
                {
                    change = FileChange.Changed;
                    changed++;
                }
                else
                {
                    change = FileChange.Same;
                }
            }

            packed.Add(new PackFile(relative, file.Length, change, origin));
        }

        hasher.Save();

        var removed = last == null
            ? []
            : last.Files.Keys.Where(published => !current.Contains(published)).Order(StringComparer.OrdinalIgnoreCase).ToList();

        var byType = AddonContents.FromFiles(files).AssetTypes
            .Select(type => new PackTypeStat(type.Label, type.FileCount, type.Size))
            .ToList();

        return new PackContents(
            packed,
            removed,
            packed.Count,
            packed.Sum(file => file.Size),
            limitBytes(),
            byType,
            last == null ? null : new PackLastPublish(last.At, last.WorkshopId, last.Commit),
            new PackChanges(added, changed, removed.Count));
    }

    private PackExportResult Export(string path, string? outDir, CancellationToken ct)
    {
        var paths = projects.GetPaths(Paths.Full(path));
        var repo = paths.Repo;
        var (buildDir, gameInfo) = RequireInputs(paths, requireBuildDir: true);
        var config = projects.LoadConfig(repo);

        var directory = string.IsNullOrWhiteSpace(outDir)
            ? StatePaths.NewExportDir(repo)
            : Paths.Full(Path.IsPathRooted(outDir) ? outDir : Path.Combine(repo, outDir));

        // Valve names the VPK after the workshop item; before the first publish there is no id, so the addon name is used
        var name = config.WorkshopId is { Length: > 0 } id && id.All(char.IsAsciiDigit) ? id : config.Addon;
        var addon = string.IsNullOrWhiteSpace(config.Addon) ? paths.Addon : config.Addon;

        var written = AddonVpk.Stage(
            buildDir,
            gameInfo,
            directory,
            name,
            addon,
            config.Title,
            DateTimeOffset.UtcNow,
            BuildRules(config.Pack, buildDir),
            limitBytes(),
            ct);

        return new PackExportResult(directory, written, written.Sum(file => new FileInfo(file).Length));
    }

    private (string BuildDir, string GameInfo) RequireInputs(ProjectPaths paths, bool requireBuildDir)
    {
        if (paths.BuildDir == null)
        {
            throw new AddonStudioException(ErrorCodes.Cs2Missing, "CS2 was not found, so there is no build folder to pack.");
        }

        var gameInfo = paths.GameInfoPath is { } own && File.Exists(own) ? own : env.GameInfoPath();

        if (gameInfo == null || !File.Exists(gameInfo))
        {
            throw new AddonStudioException(ErrorCodes.Cs2Missing, "CS2's game/csgo/gameinfo.gi was not found, it holds the packing rules.");
        }

        if (requireBuildDir && !Directory.Exists(paths.BuildDir))
        {
            throw AddonStudioException.NotFound($"The build folder {paths.BuildDir} does not exist yet. Build the project first.");
        }

        return (paths.BuildDir, gameInfo);
    }

    private static string? SafeHash(FileHasher hasher, string path)
    {
        try
        {
            return hasher.Hash(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
