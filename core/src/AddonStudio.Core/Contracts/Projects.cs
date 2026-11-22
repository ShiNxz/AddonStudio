using System.Text.Json.Serialization;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Contracts;

/// <summary>workshop.json pack rules: globs relative to the build dir, user rules go before Valve's gameinfo rules.</summary>
public sealed record PackConfig
{
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

/// <summary>workshop.json autoPublish block.</summary>
public sealed record AutoPublishConfig
{
    public const string DefaultPattern = "^v(\\d+\\.\\d+\\.\\d+)\\b";

    public bool Enabled { get; init; }

    /// <summary>Matched against the commit subject, group 1 = version.</summary>
    public string Pattern { get; init; } = DefaultPattern;

    /// <summary>Also create a GitHub release with the VPK chunks (needs gh).</summary>
    public bool GithubRelease { get; init; }
}

/// <summary>
/// workshop.json (SPEC section 3). Missing keys get the defaults below. Unknown keys in the file are kept on save
/// (the project service merges the saved config into the existing JSON, see Json.Merge).
/// </summary>
public sealed record WorkshopConfig
{
    public const string FileName = "workshop.json";

    /// <summary>Valid addon folder names.</summary>
    public const string AddonPattern = "^[a-z0-9_]{1,64}$";

    /// <summary>Folder name under csgo_addons.</summary>
    public string Addon { get; init; } = "";

    /// <summary>Null until the first publish, then written back. A number in a hand edited file is read as a string.</summary>
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? WorkshopId { get; init; }

    public string Title { get; init; } = "";

    /// <summary>Null = leave Steam's description as it is.</summary>
    public string? Description { get; init; }

    public Visibility Visibility { get; init; } = Visibility.Private;

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Repo-relative path of the workshop thumbnail, optional.</summary>
    public string? Preview { get; init; }

    public PackConfig Pack { get; init; } = new();

    /// <summary>Rebuild changed sources when files change.</summary>
    public bool AutoBuild { get; init; } = true;

    public AutoPublishConfig AutoPublish { get; init; } = new();

    /// <summary>The config a new project starts with (the SPEC section 3 template).</summary>
    public static WorkshopConfig CreateDefault(string addon, string title) => new()
    {
        Addon = addon,
        Title = title,
        Visibility = Visibility.Private,
        Tags = ["Custom"],
        Preview = "preview.png",
        Pack = new PackConfig { Include = [], Exclude = ["**/*.psd"] },
        AutoBuild = true,
        AutoPublish = new AutoPublishConfig(),
    };
}

/// <summary>Where the CS2 content link is and what it points to.</summary>
public sealed record LinkInfo(LinkState State, string? LinkPath, string? Target, string? Detail);

/// <summary>ProjectInfo.git</summary>
public sealed record ProjectGitInfo(bool IsRepo, string? Branch, string? Remote, bool Lfs);

/// <summary>projects.open / create / clone / saveConfig / link result.</summary>
public sealed record ProjectInfo(
    string Path,
    WorkshopConfig Config,
    IReadOnlyList<string> ConfigErrors,
    string ContentDir,
    string PrebuiltDir,
    string? BuildDir,
    LinkInfo Link,
    ProjectGitInfo Git);

/// <summary>An addon found in CS2's content/csgo_addons and/or game/csgo_addons.</summary>
public sealed record Cs2Addon(
    string Name,
    bool HasContent,
    bool HasGame,
    int ContentFiles,
    int GameFiles,
    long SizeBytes,
    string? LinkedTo);

/// <summary>
/// Every path of a project, worked out from the repo and its workshop.json (IProjectService.GetPaths). Not sent over RPC.
/// Absolute paths in the OS's own form. Cs2Path, LinkPath and BuildDir are null when CS2 is not found.
/// </summary>
/// <param name="Repo">Full path of the repo.</param>
/// <param name="Content">&lt;repo&gt;/content</param>
/// <param name="Prebuilt">&lt;repo&gt;/prebuilt</param>
/// <param name="StateDir">&lt;repo&gt;/.addonstudio</param>
/// <param name="Addon">workshop.json addon.</param>
/// <param name="Cs2Path">CS2 install folder (holds game/ and content/).</param>
/// <param name="LinkPath">&lt;cs2&gt;/content/csgo_addons/&lt;addon&gt;, the junction to Content.</param>
/// <param name="BuildDir">&lt;cs2&gt;/game/csgo_addons/&lt;addon&gt;, where the compiler writes.</param>
public sealed record ProjectPaths(
    string Repo,
    string Content,
    string Prebuilt,
    string StateDir,
    string Addon,
    string? Cs2Path,
    string? LinkPath,
    string? BuildDir)
{
    /// <summary>&lt;repo&gt;/workshop.json</summary>
    public string ConfigFile => System.IO.Path.Combine(Repo, WorkshopConfig.FileName);

    /// <summary>&lt;cs2&gt;/game/csgo/gameinfo.gi, null without CS2.</summary>
    public string? GameInfoPath => Cs2Path == null ? null : System.IO.Path.Combine(Cs2Path, "game", "csgo", "gameinfo.gi");

    /// <summary>&lt;cs2&gt;/game/csgo_addons, null without CS2.</summary>
    public string? AddonsRoot => Cs2Path == null ? null : System.IO.Path.Combine(Cs2Path, "game", "csgo_addons");
}

/// <summary>IProjectService.CreateLinkAsync result: the link afterwards and the folder a conflicting one was renamed to.</summary>
public sealed record LinkOutcome(LinkInfo Link, string? BackupPath);
