namespace AddonStudio.Core.Contracts;

/// <summary>Addon projects (git repos with workshop.json), their CS2 link and the recent list (SPEC 6.3).</summary>
public interface IProjectService
{
    /// <summary>projects.list</summary>
    Task<IReadOnlyList<RecentProject>> ListAsync(CancellationToken ct = default);

    /// <summary>projects.open: reads the project and moves it to the top of the recent list. not_found when the folder or workshop.json is missing.</summary>
    Task<ProjectInfo> OpenAsync(string path, CancellationToken ct = default);

    /// <summary>projects.create: layout + workshop.json + .gitattributes + .gitignore + git init + git lfs install --local when available.</summary>
    Task<ProjectInfo> CreateAsync(string path, string addon, string title, CancellationToken ct = default, bool initialCommit = true);

    /// <summary>projects.clone: IGitService.CloneAsync (git.progress events), then open.</summary>
    Task<ProjectInfo> CloneAsync(string url, string path, IEventSink events, CancellationToken ct = default);

    /// <summary>projects.saveConfig: validates (errors come back in configErrors), keeps unknown keys, writes workshop.json.</summary>
    Task<ProjectInfo> SaveConfigAsync(string path, WorkshopConfig config, CancellationToken ct = default);

    /// <summary>projects.link: CreateLinkAsync, then open.</summary>
    Task<ProjectInfo> LinkAsync(string path, bool backup, CancellationToken ct = default);

    /// <summary>projects.remove: from the recent list only, never deletes files.</summary>
    Task<IReadOnlyList<RecentProject>> RemoveAsync(string path, CancellationToken ct = default);

    /// <summary>projects.cs2Addons: addons in CS2's content and game csgo_addons folders.</summary>
    Task<IReadOnlyList<Cs2Addon>> ListCs2AddonsAsync(CancellationToken ct = default);

    /// <summary>
    /// Makes &lt;cs2&gt;/content/csgo_addons/&lt;addon&gt; a junction to &lt;repo&gt;/content. A real folder there is a conflict
    /// (error "conflict") unless backup is true, then it is renamed to &lt;addon&gt;.addonstudio-backup-&lt;yyyyMMdd-HHmmss&gt; first.
    /// </summary>
    Task<LinkOutcome> CreateLinkAsync(string repo, bool backup, CancellationToken ct = default);

    /// <summary>The link state right now.</summary>
    LinkInfo GetLink(string repo);

    /// <summary>Every path of the project (reads workshop.json for the addon name). not_found when workshop.json is missing.</summary>
    ProjectPaths GetPaths(string repo);

    /// <summary>workshop.json as it is on disk, defaults for missing keys. not_found when missing, invalid_params when it is not JSON.</summary>
    WorkshopConfig LoadConfig(string repo);
}
