using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;
using CS2WorkshopManager;

namespace AddonStudio.Core.Pack;

/// <summary>The default mode's walk of a build dir: what it packs and what it leaves out (with the reason).</summary>
public sealed record PackWalk(List<FileInfo> Packed, List<PackSkippedFile> Skipped);

/// <summary>
/// Which build dir files go into the VPK (SPEC 5 "What goes into the VPK"), the same for pack.contents, pack.export and
/// publish.run.
/// <para>
/// Default: every file in the build dir, so everything the project compiles plus the prebuilt/ mirror. Never packed:
/// Valve's tool files (tools_asset_info.bin, readonly_tools_asset_info.bin, tools_thumbnail_cache.*, _bakeresourcecache/,
/// _vrad3/), hidden files and folders (names starting with "."), and the types Valve's tool refuses (exe, dll, zip...).
/// Then the workshop.json rules in order, the first match decides (excludes come first): an exclude drops the file, an
/// include packs it. Files no rule names are packed unless they are junk (Thumbs.db, *.tmp, *.log...) or source-only
/// (what the compiler reads: panorama xml/css/js, svg, vmdl, vmat, images, fbx, wav...).
/// </para>
/// <para>
/// ValveWhitelist ("Valve whitelist only"): AddonPackager.CollectFiles as it is, so only the folders gameinfo.gi
/// AddonConfig/VpkDirectories lists, with the workshop.json rules before Valve's. That is what Valve's workshop manager
/// and CS2WorkshopManager pack.
/// </para>
/// </summary>
public sealed class PackSelection
{
    /// <summary>The types AddonPackager never packs (its BlockedExtensions), matched on the file's extension.</summary>
    public static readonly IReadOnlyList<string> BlockedExtensions = [".bat", ".cmd", ".com", ".dll", ".exe", ".msi", ".rar", ".reg", ".los", ".zip"];

    /// <summary>OS and editor leftovers, by file name.</summary>
    public static readonly IReadOnlyList<string> JunkFileNames = ["thumbs.db", "ehthumbs.db", "desktop.ini"];

    /// <summary>Temporary files, by extension.</summary>
    public static readonly IReadOnlyList<string> JunkExtensions = [".tmp", ".temp", ".bak", ".log"];

    /// <summary>
    /// Source files the compiler reads anywhere in the tree (images, models, audio, art files). Together with the roots of
    /// AssetRules (panorama xml/css/js, svg, vmdl, vmat, vtex, vpcf, vsndevts, vdata, vmap) they are source-only: CS2 loads
    /// their compiled _c form, never the file itself.
    /// </summary>
    public static readonly IReadOnlyList<string> SourceOnlyExtensions =
        [".psd", ".tga", ".png", ".jpg", ".jpeg", ".exr", ".tif", ".tiff", ".fbx", ".dmx", ".obj", ".blend", ".max", ".ma", ".mb", ".wav", ".mp3"];

    public PackSelection(bool valveWhitelist, AddonRules rules, string? gameInfoPath)
    {
        ValveWhitelist = valveWhitelist;
        Rules = rules;
        GameInfoPath = gameInfoPath;
    }

    /// <summary>The project's mode: true = only Valve's gameinfo.gi whitelist.</summary>
    public bool ValveWhitelist { get; }

    /// <summary>workshop.json include/exclude rules, globs already expanded to path prefixes.</summary>
    public AddonRules Rules { get; }

    /// <summary>CS2's game/csgo/gameinfo.gi, only needed for the whitelist.</summary>
    public string? GameInfoPath { get; }

    /// <summary>What goes into the VPK in the project's mode, as full paths under buildDir.</summary>
    public List<FileInfo> Collect(string buildDir)
    {
        return ValveWhitelist ? CollectWhitelist(buildDir) : Walk(buildDir).Packed;
    }

    /// <summary>What Valve's whitelist packs: AddonPackager.CollectFiles with the workshop.json rules first.</summary>
    public List<FileInfo> CollectWhitelist(string buildDir)
    {
        if (GameInfoPath == null || !File.Exists(GameInfoPath))
        {
            throw new AddonStudioException(ErrorCodes.Cs2Missing, "CS2's game/csgo/gameinfo.gi was not found, it holds Valve's packing whitelist.");
        }

        return AddonPackager.CollectFiles(buildDir, GameInfoPath, Rules);
    }

    /// <summary>The default mode: every file of buildDir, packed or skipped with a reason, in folder order.</summary>
    public PackWalk Walk(string buildDir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(buildDir));
        var packed = new List<FileInfo>();
        var skipped = new List<PackSkippedFile>();

        if (Directory.Exists(root))
        {
            Walk(root, root, packed, skipped);
        }

        return new PackWalk(packed, skipped);
    }

    /// <summary>Why the default mode leaves this build-relative file out, null when it is packed.</summary>
    public PackSkipReason? SkipReason(string relativePath)
    {
        var path = Paths.ToForward(relativePath).TrimStart('/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        var extension = Paths.Extension(name);

        if (BlockedExtensions.Contains(extension))
        {
            return PackSkipReason.Blocked;
        }

        // the first matching rule decides, the way AddonPackager reads them (BuildRules puts the excludes first)
        foreach (var rule in Rules.Rules)
        {
            if (rule.Pattern.Length > 0 && path.StartsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                return rule.Exclude ? PackSkipReason.Excluded : null;
            }
        }

        if (IsJunk(name))
        {
            return PackSkipReason.Junk;
        }

        if (IsSourceOnly(path))
        {
            return PackSkipReason.SourceOnly;
        }

        return null;
    }

    /// <summary>Thumbs.db, desktop.ini, *.tmp, *.temp, *.bak, *.log, names ending in "~".</summary>
    public static bool IsJunk(string fileName)
    {
        return fileName.EndsWith('~')
            || JunkFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)
            || JunkExtensions.Contains(Paths.Extension(fileName));
    }

    /// <summary>A file the compiler reads: a root of the SPEC 4 table at this path, or an image/model/audio/art source anywhere.</summary>
    public static bool IsSourceOnly(string relativePath)
    {
        return AssetRules.IsRoot(relativePath) || SourceOnlyExtensions.Contains(Paths.Extension(relativePath));
    }

    /// <summary>Names AddonPackager skips before any rule: hidden entries, Valve's tool files, its thumbnail cache.</summary>
    private static bool IsAlwaysSkipped(string name)
    {
        return name.StartsWith('.')
            || AssetRules.ValveFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith("tools_thumbnail_cache.", StringComparison.Ordinal);
    }

    private void Walk(string root, string directory, List<FileInfo> packed, List<PackSkippedFile> skipped)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(entry);

            if (IsAlwaysSkipped(name))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                if (!AssetRules.ValveDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    Walk(root, entry, packed, skipped);
                }

                continue;
            }

            var file = new FileInfo(entry);
            var relative = AddonPackager.GetRelativePath(root, entry);

            if (SkipReason(relative) is { } reason)
            {
                skipped.Add(new PackSkippedFile(relative, file.Length, reason));
            }
            else
            {
                packed.Add(file);
            }
        }
    }
}
