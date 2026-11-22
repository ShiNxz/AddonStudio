using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Steam;
using AddonStudio.Core.Util;
using CS2WorkshopManager;

namespace AddonStudio.Core.Publish;

/// <summary>
/// publish.run (SPEC 5): changed build, upload through the Steam client, workshopId write-back, last-publish.json,
/// deploy and version tags, tag push, GitHub release. publish.progress events per stage. Once the upload finished the
/// remaining steps never fail the publish, their problems come back as warnings.
/// </summary>
public sealed partial class PublishService : IPublishService
{
    private static readonly ConcurrentDictionary<string, byte> Running = new(Paths.FsComparer);

    private readonly IProjectService projects;
    private readonly IEnvironmentService env;
    private readonly IBuildService build;
    private readonly IPackService pack;
    private readonly IGitService git;
    private readonly ISteamService steam;
    private readonly IWorkshopUploader uploader;
    private readonly Func<DateTimeOffset> clock;

    /// <param name="maxPackBytes">The pack size cap (the maxPackBytes setting), default AddonPackager.MaxTotalSize (3 GiB).</param>
    public PublishService(IProjectService projects, IEnvironmentService env, IBuildService build, IPackService pack, IGitService git, ISteamService steam,
        Func<long>? maxPackBytes = null)
        : this(projects, env, build, pack, git, steam, new SteamWorkshopUploader(maxPackBytes ?? (() => AddonPackager.MaxTotalSize)))
    {
    }

    internal PublishService(IProjectService projects, IEnvironmentService env, IBuildService build, IPackService pack, IGitService git, ISteamService steam,
        IWorkshopUploader uploader, Func<DateTimeOffset>? clock = null)
    {
        this.projects = projects;
        this.env = env;
        this.build = build;
        this.pack = pack;
        this.git = git;
        this.steam = steam;
        this.uploader = uploader;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<PublishResult> RunAsync(PublishRequest request, IEventSink events, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw AddonStudioException.InvalidParams("path is required.");
        }

        var repo = Paths.Full(request.Path);

        if (!Running.TryAdd(repo, 0))
        {
            throw AddonStudioException.Busy("This project is already being published.");
        }

        try
        {
            return await RunCoreAsync(repo, request with { Version = NormalizeVersion(request.Version) }, events, ct).ConfigureAwait(false);
        }
        finally
        {
            Running.TryRemove(repo, out _);
        }
    }

    private async Task<PublishResult> RunCoreAsync(string repo, PublishRequest request, IEventSink events, CancellationToken ct)
    {
        var paths = projects.GetPaths(repo);
        var config = projects.LoadConfig(repo);
        var note = request.ChangeNote ?? "";
        var warnings = new List<string>();

        if (!Regex.IsMatch(config.Addon, WorkshopConfig.AddonPattern))
        {
            throw AddonStudioException.InvalidParams($"workshop.json addon \"{config.Addon}\" is not a valid addon name.");
        }

        ulong? publishedId = null;

        if (!string.IsNullOrWhiteSpace(config.WorkshopId))
        {
            publishedId = ulong.TryParse(config.WorkshopId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed != 0
                ? parsed
                : throw AddonStudioException.InvalidParams($"workshop.json workshopId \"{config.WorkshopId}\" is not a Workshop id.");
        }

        var cs2Path = paths.Cs2Path ?? env.Cs2Path() ?? throw new AddonStudioException(ErrorCodes.Cs2Missing, "Counter-Strike 2 was not found. Set its folder in the app settings.");
        var buildDir = paths.BuildDir ?? Path.Combine(cs2Path, "game", "csgo_addons", config.Addon);
        var gameInfo = paths.GameInfoPath ?? Path.Combine(cs2Path, "game", "csgo", "gameinfo.gi");

        // fail before a long build when the upload can not work here anyway
        uploader.EnsureReady();

        // 1. build
        if (request.SkipBuild)
        {
            Progress(events, PublishStage.Build, 1, "Build skipped");
        }
        else
        {
            Progress(events, PublishStage.Build, null, "Building changed files");
            var result = await build.RunAsync(repo, BuildMode.Changed, null, events, ct).ConfigureAwait(false);

            if (result.Cancelled)
            {
                throw new OperationCanceledException("The build was cancelled.", ct);
            }

            var failed = result.Items.Where(item => item.Status == ItemStatus.Error).Select(item => item.Source).ToList();

            if (!result.Ok || failed.Count > 0)
            {
                throw new AddonStudioException(ErrorCodes.Internal,
                    failed.Count > 0
                        ? $"The build has errors in {failed.Count} file(s), nothing was published. Fix them or publish without building."
                        : "The build failed, nothing was published.",
                    new { failed });
            }

            Progress(events, PublishStage.Build, 1, null);
        }

        // 2. what goes into the VPK, hashed for last-publish.json
        Progress(events, PublishStage.Pack, 0, "Collecting files");

        if (!File.Exists(gameInfo))
        {
            throw new AddonStudioException(ErrorCodes.Cs2Missing, $"gameinfo.gi was not found at \"{gameInfo}\".");
        }

        if (!Directory.Exists(buildDir))
        {
            throw AddonStudioException.NotFound($"The build dir \"{buildDir}\" does not exist. Build the addon first.");
        }

        var rules = pack.BuildRules(repo);
        var packed = AddonPackager.CollectFiles(buildDir, gameInfo, rules);

        if (packed.Count == 0)
        {
            throw AddonStudioException.NotFound($"Nothing to pack in \"{buildDir}\". Build the addon first.");
        }

        var files = await HashFilesAsync(repo, buildDir, packed, events, ct).ConfigureAwait(false);
        var totalSize = files.Values.Sum(entry => entry.Size);
        Progress(events, PublishStage.Pack, 1, $"{files.Count} files");

        // 3. upload
        var head = await git.HeadAsync(repo, ct).ConfigureAwait(false);
        var dirty = head != null && (await git.StatusAsync(repo, ct).ConfigureAwait(false)).Files.Any(file => file.Index != GitState.Ignored);
        var previewPath = ResolvePreview(repo, config.Preview, warnings);
        var created = publishedId == null;

        var upload = new UploadRequest(
            cs2Path,
            config.Addon,
            publishedId,
            config.Title,
            config.Description,
            config.Visibility,
            config.Tags,
            previewPath,
            rules,
            BuildChangeNote(note, head, dirty),
            request.AllowSourceFolderChange);

        Progress(events, PublishStage.Upload, 0, created ? "Creating the Workshop item" : "Uploading");
        ct.ThrowIfCancellationRequested();

        UploadResult uploaded;

        try
        {
            uploaded = await uploader.UploadAsync(upload, new UploadProgress(events), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (MapUploadError(exception) is { } mapped)
        {
            throw mapped;
        }

        var workshopId = uploaded.PublishedFileId.ToString(CultureInfo.InvariantCulture);
        var publishedAt = clock();
        Progress(events, PublishStage.Upload, 1, null);

        // from here on the item is live on Steam: nothing below fails the publish
        if (created || config.WorkshopId?.Trim() != workshopId)
        {
            try
            {
                await projects.SaveConfigAsync(repo, config with { WorkshopId = workshopId }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                warnings.Add($"Could not write workshopId {workshopId} to workshop.json ({exception.Message}). Add it by hand, or the next publish creates another item.");
            }
        }

        try
        {
            new LastPublish { At = publishedAt, WorkshopId = workshopId, Commit = head, Files = files }.Write(repo);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not write last-publish.json: {exception.Message}");
        }

        // 4. tags
        Progress(events, PublishStage.Tag, null, null);
        var tags = await CreateTagsAsync(repo, head, workshopId, files.Count, totalSize, note, request.Version, publishedAt, warnings).ConfigureAwait(false);

        // 5. push the tags
        if (request.Push && tags.Count > 0)
        {
            Progress(events, PublishStage.Push, null, null);

            try
            {
                if (await git.RemoteUrlAsync(repo, CancellationToken.None).ConfigureAwait(false) == null)
                {
                    warnings.Add("The repo has no remote, the tags were not pushed.");
                }
                else
                {
                    await git.PushTagsAsync(repo, tags, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (AddonStudioException exception)
            {
                warnings.Add($"Pushing the tags failed: {exception.Message}");
            }
        }

        // 6. GitHub release
        string? releaseUrl = null;

        if (request.GithubRelease)
        {
            Progress(events, PublishStage.Release, null, "Creating the GitHub release");
            releaseUrl = await CreateReleaseAsync(repo, request.Version, note, warnings, ct).ConfigureAwait(false);
        }

        Progress(events, PublishStage.Done, 1, null);

        return new PublishResult(workshopId, created, ItemUrl(workshopId), uploaded.NeedsAgreement, tags, releaseUrl, warnings);
    }

    /// <summary>
    /// note + "\n\n[" + shortHash + "]", plus " (uncommitted changes)" when the tree is dirty. Without commits the note
    /// goes as it is; an empty note gives just the hash part.
    /// </summary>
    internal static string BuildChangeNote(string? note, string? head, bool dirty)
    {
        var text = (note ?? "").TrimEnd();

        if (string.IsNullOrEmpty(head))
        {
            return text;
        }

        var suffix = "[" + head[..Math.Min(7, head.Length)] + "]" + (dirty ? " (uncommitted changes)" : "");

        return text.Length == 0 ? suffix : text + "\n\n" + suffix;
    }

    internal static string ItemUrl(string workshopId) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";

    /// <summary>"1.2.3" stays, "v1.2.3" becomes "1.2.3", blank becomes null. invalid_params for what can not be a tag.</summary>
    internal static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var value = version.Trim();

        if (value.Length > 1 && value[0] is 'v' or 'V' && char.IsAsciiDigit(value[1]))
        {
            value = value[1..];
        }

        return VersionPattern().IsMatch(value) ? value : throw AddonStudioException.InvalidParams($"\"{version}\" is not a version (like 1.2.3).");
    }

    private async Task<List<string>> CreateTagsAsync(string repo, string? head, string workshopId, int fileCount, long totalSize, string note,
        string? version, DateTimeOffset publishedAt, List<string> warnings)
    {
        var created = new List<string>();

        if (head == null)
        {
            warnings.Add("The repo has no commits yet, nothing was tagged.");
            return created;
        }

        HashSet<string> existing;

        try
        {
            existing = (await git.TagsAsync(repo, CancellationToken.None).ConfigureAwait(false)).Select(tag => tag.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (AddonStudioException exception)
        {
            warnings.Add($"Could not read the tags, nothing was tagged: {exception.Message}");
            return created;
        }

        var deployTag = DeployTags.Name(publishedAt);
        await TryTagAsync(deployTag, DeployTags.Message(workshopId, fileCount, totalSize, note)).ConfigureAwait(false);

        if (version != null)
        {
            var firstLine = DeployTags.FirstLine(note);
            await TryTagAsync("v" + version, firstLine.Length == 0 ? $"v{version}" : $"v{version}\n\n{note.Trim()}").ConfigureAwait(false);
        }

        return created;

        async Task TryTagAsync(string name, string message)
        {
            if (existing.Contains(name))
            {
                warnings.Add($"Tag {name} already exists, it was not created again.");
                return;
            }

            try
            {
                await git.CreateTagAsync(repo, name, message, head, CancellationToken.None).ConfigureAwait(false);
                created.Add(name);
            }
            catch (AddonStudioException exception)
            {
                warnings.Add($"Could not create tag {name}: {exception.Message}");
            }
        }
    }

    private async Task<string?> CreateReleaseAsync(string repo, string? version, string note, List<string> warnings, CancellationToken ct)
    {
        if (version == null)
        {
            warnings.Add("A GitHub release needs a version, none was created.");
            return null;
        }

        var tag = "v" + version;
        var dir = Path.Combine(Path.GetTempPath(), "addonstudio-release-" + Guid.NewGuid().ToString("N"));

        try
        {
            var export = await pack.ExportAsync(repo, dir, ct).ConfigureAwait(false);

            return await git.CreateGithubReleaseAsync(repo, tag, tag, note.Trim().Length == 0 ? tag : note.Trim(), export.Files, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            warnings.Add("The GitHub release was cancelled.");
        }
        catch (Exception exception) when (exception is AddonStudioException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            warnings.Add($"The GitHub release was not created: {exception.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not delete {dir}: {exception.Message}");
            }
        }

        return null;
    }

    /// <summary>SHA-256 and size of every packed file, keyed by build-relative path (hash-cache.json makes repeats cheap).</summary>
    private static async Task<Dictionary<string, LastPublishEntry>> HashFilesAsync(string repo, string buildDir, List<FileInfo> packed, IEventSink events, CancellationToken ct)
    {
        var hasher = FileHasher.ForRepo(repo);
        var result = new Dictionary<string, LastPublishEntry>(Paths.RelComparer);
        var fullBuildDir = Paths.Full(buildDir);

        await Task.Run(() =>
        {
            for (var index = 0; index < packed.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var file = packed[index];
                result[AddonPackager.GetRelativePath(fullBuildDir, file.FullName)] = new LastPublishEntry(file.Length, hasher.Hash(file.FullName));

                if (index % 50 == 49)
                {
                    Progress(events, PublishStage.Pack, (double)(index + 1) / packed.Count, null);
                }
            }
        }, ct).ConfigureAwait(false);

        hasher.Save();

        return result;
    }

    private static string? ResolvePreview(string repo, string? preview, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return null;
        }

        if (!Paths.TryNormalizeRelative(preview, out var relative))
        {
            warnings.Add($"The preview \"{preview}\" is not a path inside the repo, the thumbnail was left as it is.");
            return null;
        }

        var full = Paths.SafeCombine(repo, relative);

        if (!File.Exists(full))
        {
            warnings.Add($"The preview \"{preview}\" does not exist, the thumbnail was left as it is.");
            return null;
        }

        return full;
    }

    /// <summary>CS2WorkshopManager and Steamworks exceptions as error codes (SourceFolderConflictException is conflict).</summary>
    private static AddonStudioException? MapUploadError(Exception exception)
    {
        return exception switch
        {
            AddonStudioException or OperationCanceledException => null,
            SourceFolderConflictException conflict => new AddonStudioException(ErrorCodes.Conflict, conflict.Message,
                new
                {
                    workshopId = conflict.PublishedFileId.ToString(CultureInfo.InvariantCulture),
                    previousAddon = conflict.PreviousAddonName,
                    addon = conflict.AddonName,
                },
                conflict),
            _ when SteamGate.Map(exception) is { } steamError => steamError,
            DirectoryNotFoundException => AddonStudioException.NotFound(exception.Message),
            InvalidDataException or ArgumentException => AddonStudioException.InvalidParams(exception.Message),
            _ => new AddonStudioException(ErrorCodes.Internal, $"The upload failed: {exception.Message}", null, exception),
        };
    }

    private static void Progress(IEventSink events, PublishStage stage, double? fraction, string? message)
    {
        events.Emit(EventNames.PublishProgress, new PublishProgressEvent(stage, fraction, message));
    }

    [GeneratedRegex(@"^\d[0-9A-Za-z._+-]{0,63}$")]
    private static partial Regex VersionPattern();

    /// <summary>Upload progress straight to publish.progress, at most once per percent.</summary>
    private sealed class UploadProgress(IEventSink events) : IProgress<float>
    {
        private int lastPercent = -1;

        public void Report(float value)
        {
            var percent = (int)Math.Clamp(value * 100, 0, 100);

            if (Interlocked.Exchange(ref lastPercent, percent) != percent)
            {
                Progress(events, PublishStage.Upload, percent / 100.0, null);
            }
        }
    }
}
