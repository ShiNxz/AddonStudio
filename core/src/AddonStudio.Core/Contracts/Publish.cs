namespace AddonStudio.Core.Contracts;

/// <summary>publish.run params.</summary>
/// <param name="Path">The project repo.</param>
/// <param name="ChangeNote">The note, the short commit hash is appended.</param>
/// <param name="Version">x.y.z, also tags v&lt;version&gt;. Null = no version tag.</param>
/// <param name="SkipBuild">Skip the "changed" build before packing.</param>
/// <param name="Push">Push the new tags when a remote exists.</param>
/// <param name="GithubRelease">Also create a GitHub release (needs a version and gh).</param>
/// <param name="AllowSourceFolderChange">Publish even though the item was last published from another addon folder (after a "conflict" error).</param>
public sealed record PublishRequest(
    string Path,
    string ChangeNote,
    string? Version = null,
    bool SkipBuild = false,
    bool Push = false,
    bool GithubRelease = false,
    bool AllowSourceFolderChange = false);

/// <summary>publish.run result. Tags = the git tags created.</summary>
public sealed record PublishResult(
    string WorkshopId,
    bool Created,
    string Url,
    bool NeedsAgreement,
    IReadOnlyList<string> Tags,
    string? ReleaseUrl,
    IReadOnlyList<string> Warnings);
