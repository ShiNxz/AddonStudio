using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Projects;

/// <summary>SPEC 6.3: addon projects, their CS2 content link and the recent list.</summary>
public sealed class ProjectService : IProjectService
{
    /// <summary>The .gitattributes a new project gets (SPEC section 3).</summary>
    public const string GitAttributesTemplate =
        "*.png filter=lfs diff=lfs merge=lfs -text\n" +
        "*.jpg filter=lfs diff=lfs merge=lfs -text\n" +
        "*.jpeg filter=lfs diff=lfs merge=lfs -text\n" +
        "*.tga filter=lfs diff=lfs merge=lfs -text\n" +
        "*.psd filter=lfs diff=lfs merge=lfs -text\n" +
        "*.exr filter=lfs diff=lfs merge=lfs -text\n" +
        "*.wav filter=lfs diff=lfs merge=lfs -text\n" +
        "*.mp3 filter=lfs diff=lfs merge=lfs -text\n" +
        "*.fbx filter=lfs diff=lfs merge=lfs -text\n" +
        "*.dmx filter=lfs diff=lfs merge=lfs -text\n" +
        "*.obj filter=lfs diff=lfs merge=lfs -text\n" +
        "*.vpk filter=lfs diff=lfs merge=lfs -text\n" +
        "prebuilt/** filter=lfs diff=lfs merge=lfs -text\n";

    /// <summary>The .gitignore a new project gets.</summary>
    public const string GitIgnoreTemplate = ".addonstudio/\n";

    public const string ContentDirName = "content";
    public const string PrebuiltDirName = "prebuilt";

    /// <summary>Folders in game/csgo_addons that belong to Valve's workshop manager, not addons.</summary>
    private static readonly string[] NonAddonFolders = ["vpks", "workshop_items"];

    private const string BackupMarker = ".addonstudio-backup-";

    private readonly ISettingsStore settings;
    private readonly IEnvironmentService env;
    private readonly IGitService git;

    public ProjectService(ISettingsStore settings, IEnvironmentService env, IGitService git)
    {
        this.settings = settings;
        this.env = env;
        this.git = git;
    }

    public Task<IReadOnlyList<RecentProject>> ListAsync(CancellationToken ct = default)
    {
        return Task.FromResult(settings.Get().RecentProjects);
    }

    public async Task<ProjectInfo> OpenAsync(string path, CancellationToken ct = default)
    {
        var repo = RequireRepo(path);
        var (config, errors) = WorkshopConfigFile.Read(repo);
        var info = await InfoAsync(repo, config, errors, ct).ConfigureAwait(false);

        settings.TouchRecent(repo, config.Addon, config.Title);

        return info;
    }

    public async Task<ProjectInfo> CreateAsync(string path, string addon, string title, CancellationToken ct = default)
    {
        if (!WorkshopConfigFile.IsValidAddon(addon))
        {
            throw AddonStudioException.InvalidParams($"Addon name \"{addon}\" is not valid: use 1 to 64 lowercase letters, digits or underscores.");
        }

        var repo = FullPath(path);
        RequireEmptyFolder(repo);

        await WriteLayoutAsync(repo, addon, string.IsNullOrWhiteSpace(title) ? addon : title.Trim(), ct).ConfigureAwait(false);

        return await OpenAsync(repo, ct).ConfigureAwait(false);
    }

    public async Task<ProjectInfo> CloneAsync(string url, string path, IEventSink events, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw AddonStudioException.InvalidParams("The repo URL is empty.");
        }

        var repo = FullPath(path);
        RequireEmptyFolder(repo);

        var parent = Path.GetDirectoryName(repo);

        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await git.CloneAsync(url.Trim(), repo, events, ct).ConfigureAwait(false);

        if (!File.Exists(Path.Combine(repo, WorkshopConfig.FileName)))
        {
            throw AddonStudioException.NotFound($"The repo was cloned to {repo}, but it has no {WorkshopConfig.FileName} at its root, so it is not an Addon Studio project.");
        }

        return await OpenAsync(repo, ct).ConfigureAwait(false);
    }

    public async Task<ProjectInfo> SaveConfigAsync(string path, WorkshopConfig config, CancellationToken ct = default)
    {
        if (config == null)
        {
            throw AddonStudioException.InvalidParams("config is missing.");
        }

        var repo = FullPath(path);

        if (!Directory.Exists(repo))
        {
            throw AddonStudioException.NotFound($"{repo} does not exist.");
        }

        WorkshopConfigFile.Save(repo, config);

        var (saved, errors) = WorkshopConfigFile.Read(repo);

        return await InfoAsync(repo, saved, errors, ct).ConfigureAwait(false);
    }

    public async Task<ProjectInfo> LinkAsync(string path, bool backup, CancellationToken ct = default)
    {
        await CreateLinkAsync(path, backup, ct).ConfigureAwait(false);

        return await OpenAsync(path, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RecentProject>> RemoveAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(settings.RemoveRecent(FullPath(path)));
    }

    public Task<IReadOnlyList<Cs2Addon>> ListCs2AddonsAsync(CancellationToken ct = default)
    {
        var cs2 = env.RequireCs2Path();

        return Task.Run(() => ScanCs2Addons(cs2, ct), ct);
    }

    public async Task<LinkOutcome> CreateLinkAsync(string repo, bool backup, CancellationToken ct = default)
    {
        var paths = GetPaths(repo);

        if (paths.Cs2Path == null || paths.LinkPath == null)
        {
            throw new AddonStudioException(ErrorCodes.Cs2Missing, "Counter-Strike 2 was not found, so the project can not be linked. Install it or set the CS2 folder in Settings.");
        }

        Directory.CreateDirectory(paths.Content);

        var link = ContentLink.Inspect(paths.LinkPath, paths.Content);
        string? backupPath = null;

        switch (link.State)
        {
            case LinkState.Linked:
                return new LinkOutcome(link, null);
            case LinkState.WrongTarget:
                // only the junction goes, the folder it pointed at is untouched
                Log.Info($"Re-pointing {paths.LinkPath} from {link.Target} to {paths.Content}");
                ContentLink.Remove(paths.LinkPath);
                break;
            case LinkState.Conflict when !backup:
                throw new AddonStudioException(ErrorCodes.Conflict,
                    $"{paths.LinkPath} is a real folder. Link with backup to rename it to {StatePaths.LinkBackupName(paths.Addon)} first (nothing is deleted).",
                    new { linkPath = paths.LinkPath });
            case LinkState.Conflict:
                backupPath = ContentLink.Backup(paths.LinkPath, paths.Addon);
                Log.Info($"Renamed {paths.LinkPath} to {backupPath}");
                break;
        }

        await ContentLink.CreateAsync(paths.LinkPath, paths.Content, ct).ConfigureAwait(false);

        var after = ContentLink.Inspect(paths.LinkPath, paths.Content);

        if (after.State != LinkState.Linked)
        {
            throw AddonStudioException.Internal($"The link was created but reads back as {after.State}: {after.Detail}");
        }

        return new LinkOutcome(after, backupPath);
    }

    public LinkInfo GetLink(string repo)
    {
        var full = RequireRepo(repo);
        var config = WorkshopConfigFile.Read(full).Config;

        return LinkFor(PathsFor(full, config.Addon, strict: false));
    }

    public ProjectPaths GetPaths(string repo)
    {
        var full = RequireRepo(repo);
        var config = WorkshopConfigFile.Read(full).Config;

        return PathsFor(full, config.Addon, strict: true);
    }

    public WorkshopConfig LoadConfig(string repo)
    {
        return WorkshopConfigFile.Read(RequireRepo(repo)).Config;
    }

    /// <summary>
    /// The files of a new project: content/, prebuilt/, workshop.json, .gitattributes, .gitignore, then git init and
    /// git lfs install --local when git and git-lfs are there. Git failures are logged, the project still works.
    /// </summary>
    internal async Task WriteLayoutAsync(string repo, string addon, string title, CancellationToken ct)
    {
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(Path.Combine(repo, ContentDirName));
        Directory.CreateDirectory(Path.Combine(repo, PrebuiltDirName));

        var config = WorkshopConfig.CreateDefault(addon, title);

        // the template's preview.png only when there is one, so a new project opens without config errors
        if (config.Preview != null && !File.Exists(Path.Combine(repo, config.Preview)))
        {
            config = config with { Preview = null };
        }

        Json.WriteFile(Path.Combine(repo, WorkshopConfig.FileName), config);
        WriteIfMissing(Path.Combine(repo, ".gitattributes"), GitAttributesTemplate);
        WriteIfMissing(Path.Combine(repo, ".gitignore"), GitIgnoreTemplate);

        if (Directory.Exists(Path.Combine(repo, ".git")))
        {
            return;
        }

        try
        {
            await git.InitAsync(repo, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Warn($"git init failed in {repo}: {exception.Message}");

            return;
        }

        try
        {
            if (await git.LfsAvailableAsync(ct).ConfigureAwait(false))
            {
                await git.LfsInstallLocalAsync(repo, ct).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Warn($"git lfs install --local failed in {repo}: {exception.Message}");
        }
    }

    /// <summary>Every addon in &lt;cs2&gt;/content/csgo_addons and &lt;cs2&gt;/game/csgo_addons with file counts and sizes.</summary>
    internal static IReadOnlyList<Cs2Addon> ScanCs2Addons(string cs2, CancellationToken ct = default)
    {
        var contentRoot = Path.Combine(cs2, "content", "csgo_addons");
        var gameRoot = Path.Combine(cs2, "game", "csgo_addons");
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in FolderNames(contentRoot))
        {
            names.Add(name);
        }

        foreach (var name in FolderNames(gameRoot))
        {
            if (!NonAddonFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        var result = new List<Cs2Addon>();

        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();

            if (name.Contains(BackupMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var contentDir = Path.Combine(contentRoot, name);
            var gameDir = Path.Combine(gameRoot, name);
            var linkedTo = ContentLink.ReadTarget(contentDir);
            var hasContent = Directory.Exists(contentDir);
            var hasGame = Directory.Exists(gameDir);
            var (contentFiles, contentBytes) = hasContent ? Measure(contentDir) : (0, 0L);
            var (gameFiles, gameBytes) = hasGame ? Measure(gameDir) : (0, 0L);

            result.Add(new Cs2Addon(name, hasContent, hasGame, contentFiles, gameFiles, contentBytes + gameBytes, linkedTo));
        }

        return result;
    }

    private async Task<ProjectInfo> InfoAsync(string repo, WorkshopConfig config, List<string> errors, CancellationToken ct)
    {
        var paths = PathsFor(repo, config.Addon, strict: false);
        var link = LinkFor(paths);
        ProjectGitInfo gitInfo;

        try
        {
            gitInfo = await git.InfoAsync(repo, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Warn($"Could not read git info of {repo}: {exception.Message}");
            gitInfo = new ProjectGitInfo(Directory.Exists(Path.Combine(repo, ".git")), null, null, false);
        }

        return new ProjectInfo(repo, config, errors, paths.Content, paths.Prebuilt, paths.BuildDir, link, gitInfo);
    }

    /// <summary>
    /// The project's paths. An addon name that is not a valid folder name never becomes a CS2 path: strict throws
    /// invalid_params, otherwise LinkPath and BuildDir are null.
    /// </summary>
    private ProjectPaths PathsFor(string repo, string addon, bool strict)
    {
        var valid = WorkshopConfigFile.IsValidAddon(addon);

        if (!valid && strict)
        {
            throw AddonStudioException.InvalidParams($"The addon name \"{addon}\" in workshop.json is not valid. Fix it in the project settings.");
        }

        var cs2 = env.Cs2Path();
        string? linkPath = null;
        string? buildDir = null;

        if (cs2 != null && valid)
        {
            linkPath = Path.Combine(cs2, "content", "csgo_addons", addon);
            buildDir = Path.Combine(cs2, "game", "csgo_addons", addon);
        }

        return new ProjectPaths(
            repo,
            Path.Combine(repo, ContentDirName),
            Path.Combine(repo, PrebuiltDirName),
            StatePaths.Dir(repo),
            addon,
            cs2,
            linkPath,
            buildDir);
    }

    private static LinkInfo LinkFor(ProjectPaths paths)
    {
        if (paths.Cs2Path == null)
        {
            return new LinkInfo(LinkState.NoCs2, null, null, "Counter-Strike 2 was not found.");
        }

        if (paths.LinkPath == null)
        {
            return new LinkInfo(LinkState.Missing, null, null, "The addon name in workshop.json is not valid, fix it to link the project.");
        }

        return ContentLink.Inspect(paths.LinkPath, paths.Content);
    }

    private static string FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw AddonStudioException.InvalidParams("The project path is empty.");
        }

        try
        {
            return Paths.Full(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw AddonStudioException.InvalidParams($"\"{path}\" is not a valid path: {exception.Message}");
        }
    }

    private static string RequireRepo(string path)
    {
        var repo = FullPath(path);

        if (!Directory.Exists(repo))
        {
            throw AddonStudioException.NotFound($"{repo} does not exist.");
        }

        if (!File.Exists(Path.Combine(repo, WorkshopConfig.FileName)))
        {
            throw AddonStudioException.NotFound($"{repo} has no {WorkshopConfig.FileName}, it is not an Addon Studio project.");
        }

        return repo;
    }

    private static void RequireEmptyFolder(string folder)
    {
        if (File.Exists(folder))
        {
            throw new AddonStudioException(ErrorCodes.Conflict, $"{folder} is a file.", new { path = folder });
        }

        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new AddonStudioException(ErrorCodes.Conflict, $"{folder} is not empty. Pick an empty or new folder.", new { path = folder });
        }
    }

    private static void WriteIfMissing(string path, string text)
    {
        if (!File.Exists(path))
        {
            Json.WriteTextAtomic(path, text);
        }
    }

    private static IEnumerable<string> FolderNames(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not list {root}: {exception.Message}");

            return [];
        }
    }

    private static (int Files, long Bytes) Measure(string root)
    {
        var files = Paths.ListFiles(root);
        long bytes = 0;

        foreach (var relative in files)
        {
            try
            {
                bytes += new FileInfo(Path.Combine(root, Paths.ToNative(relative))).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (files.Count, bytes);
    }
}
