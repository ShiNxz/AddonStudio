using System.Text.Json.Serialization;

namespace AddonStudio.Core.Contracts;

// Every enum here goes over the wire as its camelCase name (Json.Options adds a camelCase JsonStringEnumConverter).
// A member whose camelCase name would not match SPEC section 7 carries JsonStringEnumMemberName.

/// <summary>"windows" | "macos" | "linux"</summary>
public enum Os
{
    Windows,
    Macos,
    Linux,
}

/// <summary>"ok" | "warn" | "error"</summary>
public enum CheckStatus
{
    Ok,
    Warn,
    Error,
}

/// <summary>DoctorCheck.id: "os" | "steam" | "cs2" | "workshopTools" | "steamRunning" | "git" | "gitLfs" | "gh"</summary>
public enum DoctorCheckId
{
    Os,
    Steam,
    Cs2,
    WorkshopTools,
    SteamRunning,
    Git,
    GitLfs,
    Gh,
}

/// <summary>AppSettings.theme: "dark" | "light" | "system"</summary>
public enum Theme
{
    Dark,
    Light,
    System,
}

/// <summary>"public" | "friendsOnly" | "private" | "unlisted"</summary>
public enum Visibility
{
    Public,
    FriendsOnly,
    Private,
    Unlisted,
}

/// <summary>LinkInfo.state: "linked" | "missing" | "conflict" | "wrongTarget" | "noCs2"</summary>
public enum LinkState
{
    /// <summary>The content link is a junction to the repo's content/.</summary>
    Linked,
    /// <summary>Nothing at the link path yet.</summary>
    Missing,
    /// <summary>A real folder (not a junction) is at the link path. We never delete it.</summary>
    Conflict,
    /// <summary>A junction is there but points somewhere else.</summary>
    WrongTarget,
    /// <summary>CS2 was not found, so there is no link path.</summary>
    NoCs2,
}

/// <summary>"copySource" | "decompile" | "prebuilt" | "skip"</summary>
public enum ImportAction
{
    CopySource,
    Decompile,
    Prebuilt,
    Skip,
}

/// <summary>"panorama" | "texture" | "model" | "material" | "particle" | "sound" | "soundevents" | "vdata" | "map" | "other"</summary>
public enum RootKind
{
    Panorama,
    Texture,
    Model,
    Material,
    Particle,
    Sound,
    [JsonStringEnumMemberName("soundevents")]
    SoundEvents,
    Vdata,
    Map,
    Other,
}

/// <summary>BuildRoot.status: "new" | "changed" | "unchanged" | "deleted" | "unsupported" | "failed"</summary>
public enum RootStatus
{
    New,
    Changed,
    Unchanged,
    Deleted,
    Unsupported,
    Failed,
}

/// <summary>BuildRoot.lastResult and build-state.json roots[].result: "ok" | "warning" | "error"</summary>
public enum RootResult
{
    Ok,
    Warning,
    Error,
}

/// <summary>PrebuiltEntry.status: "new" | "changed" | "unchanged" | "deleted"</summary>
public enum PrebuiltStatus
{
    New,
    Changed,
    Unchanged,
    Deleted,
}

/// <summary>BuildItemResult.status: "ok" | "warning" | "error" | "skipped"</summary>
public enum ItemStatus
{
    Ok,
    Warning,
    Error,
    Skipped,
}

/// <summary>build.run mode: "changed" | "full" | "files"</summary>
public enum BuildMode
{
    /// <summary>New, changed and failed roots, plus the prebuilt mirror and deleted-root cleanup.</summary>
    Changed,
    /// <summary>Every root.</summary>
    Full,
    /// <summary>Only the roots given in files.</summary>
    Files,
}

/// <summary>PackFile.change: "added" | "changed" | "same"</summary>
public enum FileChange
{
    Added,
    Changed,
    Same,
}

/// <summary>PackFile.origin: "compiled" | "prebuilt" | "other"</summary>
public enum PackOrigin
{
    Compiled,
    Prebuilt,
    Other,
}

/// <summary>PackSkippedFile.reason: "junk" | "sourceOnly" | "excluded" | "blocked"</summary>
public enum PackSkipReason
{
    /// <summary>Temporary or OS files: Thumbs.db, desktop.ini, *.tmp, *.temp, *.bak, *.log, names ending in ~.</summary>
    Junk,
    /// <summary>A file the compiler reads (a root of the SPEC 4 table, or an image/model/audio source), its compiled form is what ships.</summary>
    SourceOnly,
    /// <summary>A workshop.json pack.exclude rule.</summary>
    Excluded,
    /// <summary>A type Valve's tool never packs: .bat .cmd .com .dll .exe .msi .rar .reg .los .zip.</summary>
    Blocked,
}

/// <summary>"build" | "pack" | "upload" | "tag" | "push" | "release" | "done"</summary>
public enum PublishStage
{
    Build,
    Pack,
    Upload,
    Tag,
    Push,
    Release,
    Done,
}

/// <summary>SteamItemStatus.live: "live" | "processing" | "unknown"</summary>
public enum LiveState
{
    Live,
    Processing,
    Unknown,
}

/// <summary>"unmodified" | "modified" | "added" | "deleted" | "renamed" | "copied" | "untracked" | "ignored" | "conflicted" | "typeChanged"</summary>
public enum GitState
{
    Unmodified,
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    Untracked,
    Ignored,
    Conflicted,
    TypeChanged,
}
