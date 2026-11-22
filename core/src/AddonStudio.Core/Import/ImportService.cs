using AddonStudio.Core.Contracts;
using AddonStudio.Core.Projects;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Import;

/// <summary>
/// SPEC 6.4: turns an addon that lives in CS2's csgo_addons folders into a project. Files are only ever copied; the
/// old content folder is renamed to the backup name when the project is linked, the old build dir is left alone.
/// </summary>
public sealed class ImportService : IImportService
{
    private const string ContentPrefix = "content/";
    private const string GamePrefix = "game/";

    private readonly IProjectService projects;
    private readonly IEnvironmentService env;

    public ImportService(IProjectService projects, IEnvironmentService env)
    {
        this.projects = projects;
        this.env = env;
    }

    public Task<ImportPlan> PlanAsync(string addon, string targetPath, bool decompile, CancellationToken ct = default)
    {
        return Task.Run(() => Classify(addon, targetPath, decompile, ct).Plan, ct);
    }

    public async Task<ImportResult> RunAsync(string addon, string targetPath, bool decompile, IEventSink events, CancellationToken ct = default)
    {
        var work = await Task.Run(() => Classify(addon, targetPath, decompile, ct), ct).ConfigureAwait(false);
        var plan = work.Plan;
        var repo = plan.Path;

        if (!File.Exists(Path.Combine(repo, WorkshopConfig.FileName)))
        {
            await projects.CreateAsync(repo, addon, Titleize(addon), ct).ConfigureAwait(false);
        }

        var warnings = plan.Warnings.ToList();
        var counts = await Task.Run(() => CopyAll(work, events, warnings, ct), ct).ConfigureAwait(false);

        string? backupPath = null;

        try
        {
            backupPath = (await projects.CreateLinkAsync(repo, backup: true, ct).ConfigureAwait(false)).BackupPath;
        }
        catch (AddonStudioException exception) when (exception.Code != ErrorCodes.Cancelled)
        {
            warnings.Add($"The files were imported, but linking into CS2 failed: {exception.Message}");
        }

        var project = await projects.OpenAsync(repo, ct).ConfigureAwait(false);

        return new ImportResult(project, counts.Copied, counts.Decompiled, counts.Prebuilt, counts.Skipped, backupPath, warnings);
    }

    /// <summary>Works out the plan: every content file is a source, every build dir file is skip, decompile or prebuilt.</summary>
    internal ImportWork Classify(string addon, string targetPath, bool decompile, CancellationToken ct)
    {
        if (!WorkshopConfigFile.IsValidAddon(addon))
        {
            throw AddonStudioException.InvalidParams($"Addon name \"{addon}\" is not valid: use 1 to 64 lowercase letters, digits or underscores.");
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw AddonStudioException.InvalidParams("The project folder is empty.");
        }

        var repo = Paths.Full(targetPath.Trim());
        var cs2 = env.RequireCs2Path();
        var contentDir = Path.Combine(cs2, "content", "csgo_addons", addon);
        var gameDir = Path.Combine(cs2, "game", "csgo_addons", addon);
        var contentExists = Directory.Exists(contentDir);
        var gameExists = Directory.Exists(gameDir);

        if (!contentExists && !gameExists)
        {
            throw AddonStudioException.NotFound($"There is no addon \"{addon}\" in {Path.Combine(cs2, "content", "csgo_addons")} or {Path.Combine(cs2, "game", "csgo_addons")}.");
        }

        CheckTarget(repo, addon);

        var warnings = new List<string>();
        var items = new List<ImportItem>();
        var repoContent = Path.Combine(repo, ProjectService.ContentDirName);
        var contentLink = ContentLink.ReadTarget(contentDir);
        var contentIsProject = contentLink != null && Paths.SamePath(contentLink, repoContent);

        if (contentLink != null && !contentIsProject)
        {
            warnings.Add($"content/csgo_addons/{addon} links to {contentLink}, its files are copied from there.");
        }

        var contentFiles = contentExists ? Paths.ListFiles(contentDir) : [];
        var contentSet = new HashSet<string>(contentFiles, Paths.RelComparer);

        foreach (var relative in contentFiles)
        {
            ct.ThrowIfCancellationRequested();

            var size = SizeOf(Path.Combine(contentDir, Paths.ToNative(relative)));

            items.Add(contentIsProject
                ? new ImportItem(ContentPrefix + relative, size, ImportAction.Skip, null, "already in the project's content folder")
                : new ImportItem(ContentPrefix + relative, size, ImportAction.CopySource, ContentPrefix + relative, null));
        }

        foreach (var relative in gameExists ? Paths.ListFiles(gameDir) : [])
        {
            ct.ThrowIfCancellationRequested();

            var full = Path.Combine(gameDir, Paths.ToNative(relative));
            items.Add(ClassifyBuildFile(relative, full, SizeOf(full), contentSet, decompile));
        }

        if (!gameExists)
        {
            warnings.Add($"game/csgo_addons/{addon} does not exist, only the sources are imported.");
        }

        var plan = new ImportPlan(
            addon,
            repo,
            items.Count(item => item.Action is ImportAction.CopySource or ImportAction.Decompile),
            items,
            items.Where(item => item.Action != ImportAction.Skip).Sum(item => item.Size),
            warnings);

        return new ImportWork(plan, contentDir, gameDir);
    }

    /// <summary>skip (a source we have builds it, or Valve's file), decompile (only when asked and possible) or prebuilt.</summary>
    internal static ImportItem ClassifyBuildFile(string buildRel, string fullPath, long size, IReadOnlySet<string> contentFiles, bool decompile)
    {
        var path = GamePrefix + buildRel;

        if (AssetRules.IsValveFile(buildRel))
        {
            return new ImportItem(path, size, ImportAction.Skip, null, "Valve's file, the tools make it");
        }

        var source = AssetRules.SourceCandidates(buildRel).FirstOrDefault(contentFiles.Contains);

        if (source != null)
        {
            return new ImportItem(path, size, ImportAction.Skip, null, $"built from content/{source}");
        }

        if (decompile && Decompiler.SourceFor(buildRel, fullPath) is { } decompiled)
        {
            return new ImportItem(path, size, ImportAction.Decompile, ContentPrefix + decompiled, $"decompiled to content/{decompiled}");
        }

        return new ImportItem(path, size, ImportAction.Prebuilt, ProjectService.PrebuiltDirName + "/" + buildRel,
            AssetRules.IsCompiledResource(buildRel) ? "no source for it, copied as-is" : "not a compiled file, copied as-is");
    }

    private static Counts CopyAll(ImportWork work, IEventSink events, List<string> warnings, CancellationToken ct)
    {
        var repo = work.Plan.Path;
        var total = work.Plan.Items.Count(item => item.Action != ImportAction.Skip);
        var counts = new Counts();
        var done = 0;

        foreach (var item in work.Plan.Items)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Action == ImportAction.Skip || item.Target == null)
            {
                counts.Skipped++;
                continue;
            }

            events.Emit(EventNames.ImportProgress, new ImportProgressEvent(done, total, item.Path));

            var source = SourcePath(work, item.Path);

            switch (item.Action)
            {
                case ImportAction.CopySource:
                    CopyFile(source, Paths.SafeCombine(repo, item.Target));
                    counts.Copied++;
                    break;
                case ImportAction.Decompile:
                    var target = Paths.SafeCombine(repo, item.Target);

                    try
                    {
                        var data = Decompiler.Decompile(source, Paths.Extension(target));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, data);
                        counts.Decompiled++;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        var buildRel = item.Path[GamePrefix.Length..];
                        warnings.Add($"Could not decompile {item.Path} ({exception.Message}), kept it in prebuilt/ instead.");
                        CopyFile(source, Paths.SafeCombine(repo, ProjectService.PrebuiltDirName + "/" + buildRel));
                        counts.Prebuilt++;
                    }

                    break;
                case ImportAction.Prebuilt:
                    CopyFile(source, Paths.SafeCombine(repo, item.Target));
                    counts.Prebuilt++;
                    break;
            }

            done++;
        }

        events.Emit(EventNames.ImportProgress, new ImportProgressEvent(done, total, ""));

        return counts;
    }

    /// <summary>The target must be empty, new, or a project for the same addon.</summary>
    private static void CheckTarget(string repo, string addon)
    {
        if (File.Exists(repo))
        {
            throw new AddonStudioException(ErrorCodes.Conflict, $"{repo} is a file.", new { path = repo });
        }

        if (!Directory.Exists(repo) || !Directory.EnumerateFileSystemEntries(repo).Any())
        {
            return;
        }

        if (!File.Exists(Path.Combine(repo, WorkshopConfig.FileName)))
        {
            throw new AddonStudioException(ErrorCodes.Conflict, $"{repo} is not empty and not an Addon Studio project. Pick an empty or new folder.", new { path = repo });
        }

        var existing = WorkshopConfigFile.Read(repo).Config.Addon;

        if (!string.Equals(existing, addon, StringComparison.Ordinal))
        {
            throw AddonStudioException.InvalidParams($"{repo} is the project of addon \"{existing}\", not \"{addon}\".");
        }
    }

    private static string SourcePath(ImportWork work, string itemPath)
    {
        if (itemPath.StartsWith(ContentPrefix, StringComparison.Ordinal))
        {
            return Path.Combine(work.ContentDir, Paths.ToNative(itemPath[ContentPrefix.Length..]));
        }

        return Path.Combine(work.GameDir, Paths.ToNative(itemPath[GamePrefix.Length..]));
    }

    private static void CopyFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>"next_hud" -> "Next Hud"</summary>
    internal static string Titleize(string addon)
    {
        var words = addon.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]);
        var title = string.Join(' ', words);

        return title.Length == 0 ? addon : title;
    }

    internal sealed record ImportWork(ImportPlan Plan, string ContentDir, string GameDir);

    private sealed class Counts
    {
        public int Copied { get; set; }
        public int Decompiled { get; set; }
        public int Prebuilt { get; set; }
        public int Skipped { get; set; }
    }
}
