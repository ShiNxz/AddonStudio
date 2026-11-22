using AddonStudio.Core.Contracts;

namespace AddonStudio.Core.Util;

/// <summary>
/// SPEC section 4 "Roots, dependencies, outputs": which content files are compile roots, their kind, the build-relative
/// files the compiler writes for them, and which build dir files belong to Valve. Paths are relative with forward
/// slashes (backslashes are accepted), matched ignoring case. Outputs keep the source's folder and name casing and
/// get a lowercase compiled extension.
/// </summary>
public static class AssetRules
{
    /// <summary>Source images under panorama/images/ that compile to &lt;name&gt;_&lt;ext&gt;.vtex_c.</summary>
    public static readonly IReadOnlyList<string> PanoramaImageExtensions = [".png", ".jpg", ".jpeg", ".tga", ".psd"];

    /// <summary>Source sounds under sounds/ that compile to .vsnd_c.</summary>
    public static readonly IReadOnlyList<string> SoundExtensions = [".wav", ".mp3"];

    /// <summary>Panorama text roots scanned for file://, s2r://, include, url() and @import references.</summary>
    public static readonly IReadOnlyList<string> PanoramaTextExtensions = [".xml", ".css", ".js"];

    /// <summary>KV3/keyvalues text roots scanned for quoted references.</summary>
    public static readonly IReadOnlyList<string> KeyValuesTextExtensions = [".vmat", ".vmdl", ".vpcf", ".vsndevts", ".vdata"];

    /// <summary>A quoted string in KV3 text ending in one of these is a reference to another file.</summary>
    public static readonly IReadOnlyList<string> ReferenceExtensions =
        [".png", ".tga", ".jpg", ".psd", ".exr", ".fbx", ".dmx", ".obj", ".vmat", ".vtex", ".wav", ".mp3", ".vsnd", ".vpcf", ".vmdl"];

    /// <summary>Extensions of every root type in the table (svg and the panorama/sound sources depend on the folder too).</summary>
    public static readonly IReadOnlyList<string> RootExtensions =
        [".xml", ".css", ".js", ".svg", ".png", ".jpg", ".jpeg", ".tga", ".psd", ".vmdl", ".vmat", ".vtex", ".vpcf", ".vsndevts", ".wav", ".mp3", ".vdata", ".vmap"];

    /// <summary>Valve's files at the build dir root that we never touch.</summary>
    public static readonly IReadOnlyList<string> ValveFileNames = ["tools_asset_info.bin", "readonly_tools_asset_info.bin"];

    /// <summary>Valve's folders at the build dir root that we never touch.</summary>
    public static readonly IReadOnlyList<string> ValveDirectories = ["_bakeresourcecache", "_vrad3"];

    public const string MapUnsupportedReason = "maps need Hammer, put the compiled map in prebuilt/";

    private const string PanoramaPrefix = "panorama/";
    private const string PanoramaImagesPrefix = "panorama/images/";
    private const string SoundsPrefix = "sounds/";

    /// <summary>True for every row of the table, the unsupported .vmap included.</summary>
    public static bool IsRoot(string contentRel) => Kind(contentRel) != null;

    /// <summary>A root we can compile (every root but .vmap).</summary>
    public static bool IsCompilable(string contentRel) => Kind(contentRel) is { } kind && kind != RootKind.Map;

    /// <summary>The root's kind, null when the file is not a root (a dependency or ignored).</summary>
    public static RootKind? Kind(string contentRel)
    {
        var path = Clean(contentRel);
        var extension = Paths.Extension(path);

        switch (extension)
        {
            case ".vmap":
                return RootKind.Map;
            case ".svg":
                return RootKind.Panorama;
            case ".xml" or ".css" or ".js" when path.StartsWith(PanoramaPrefix, StringComparison.OrdinalIgnoreCase):
                return RootKind.Panorama;
            case ".png" or ".jpg" or ".jpeg" or ".tga" or ".psd" when path.StartsWith(PanoramaImagesPrefix, StringComparison.OrdinalIgnoreCase):
                return RootKind.Texture;
            case ".vmdl":
                return RootKind.Model;
            case ".vmat":
                return RootKind.Material;
            case ".vtex":
                return RootKind.Texture;
            case ".vpcf":
                return RootKind.Particle;
            case ".vsndevts":
                return RootKind.SoundEvents;
            case ".wav" or ".mp3" when path.StartsWith(SoundsPrefix, StringComparison.OrdinalIgnoreCase):
                return RootKind.Sound;
            case ".vdata":
                return RootKind.Vdata;
            default:
                return null;
        }
    }

    /// <summary>Why a root can not be built (BuildRoot.reason for status "unsupported"), null for buildable roots and non-roots.</summary>
    public static string? UnsupportedReason(string contentRel)
    {
        return Kind(contentRel) == RootKind.Map ? MapUnsupportedReason : null;
    }

    /// <summary>
    /// The build-relative files the compiler writes for a root, e.g. "panorama/images/coin.png" -> "panorama/images/coin_png.vtex_c".
    /// Empty for non-roots and .vmap.
    /// </summary>
    public static IReadOnlyList<string> ExpectedOutputs(string contentRel)
    {
        var path = Clean(contentRel);
        var kind = Kind(path);

        if (kind == null || kind == RootKind.Map)
        {
            return [];
        }

        var extension = Paths.Extension(path);
        var stem = path[..^extension.Length];

        var compiled = extension switch
        {
            ".xml" => stem + ".vxml_c",
            ".css" => stem + ".vcss_c",
            ".js" => stem + ".vjs_c",
            ".svg" => stem + ".vsvg_c",
            ".png" or ".jpg" or ".jpeg" or ".tga" or ".psd" => stem + "_" + extension[1..] + ".vtex_c",
            ".vmdl" => stem + ".vmdl_c",
            ".vmat" => stem + ".vmat_c",
            ".vtex" => stem + ".vtex_c",
            ".vpcf" => stem + ".vpcf_c",
            ".vsndevts" => stem + ".vsndevts_c",
            ".wav" or ".mp3" => stem + ".vsnd_c",
            ".vdata" => stem + ".vdata_c",
            _ => null,
        };

        return compiled == null ? [] : [compiled];
    }

    /// <summary>
    /// The content-relative sources that would produce a build-relative compiled file (the table backwards), for import:
    /// "panorama/images/coin_png.vtex_c" -> ["panorama/images/coin.png", "panorama/images/coin_png.vtex"],
    /// "sounds/a.vsnd_c" -> ["sounds/a.wav", "sounds/a.mp3"]. Empty when nothing in the table makes it.
    /// </summary>
    public static IReadOnlyList<string> SourceCandidates(string buildRel)
    {
        var path = Clean(buildRel);
        var extension = Paths.Extension(path);

        if (!extension.EndsWith("_c", StringComparison.Ordinal) || extension.Length < 4)
        {
            return [];
        }

        var stem = path[..^extension.Length];
        var compiledType = extension[1..^2];
        var candidates = new List<string>();

        switch (compiledType)
        {
            case "vxml":
                candidates.Add(stem + ".xml");
                break;
            case "vcss":
                candidates.Add(stem + ".css");
                break;
            case "vjs":
                candidates.Add(stem + ".js");
                break;
            case "vsvg":
                candidates.Add(stem + ".svg");
                break;
            case "vtex":
                foreach (var image in PanoramaImageExtensions)
                {
                    var suffix = "_" + image[1..];

                    if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(stem[..^suffix.Length] + image);
                    }
                }

                candidates.Add(stem + ".vtex");
                break;
            case "vsnd":
                candidates.AddRange(SoundExtensions.Select(sound => stem + sound));
                break;
            case "vmdl" or "vmat" or "vpcf" or "vsndevts" or "vdata":
                candidates.Add(stem + "." + compiledType);
                break;
        }

        // only what the forward table really maps to this file (folder rules like panorama/ and sounds/ apply)
        return candidates
            .Where(candidate => ExpectedOutputs(candidate).Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>True for Valve's files in the build dir: tools_asset_info.bin, readonly_tools_asset_info.bin at the root, anything under _bakeresourcecache/ or _vrad3/.</summary>
    public static bool IsValveFile(string buildRel)
    {
        var path = Clean(buildRel);

        if (ValveFileNames.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var slash = path.IndexOf('/');
        var first = slash < 0 ? path : path[..slash];

        return ValveDirectories.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A compiled resource file (extension ending in "_c", like .vmdl_c).</summary>
    public static bool IsCompiledResource(string path)
    {
        var extension = Paths.Extension(path);

        return extension.Length > 3 && extension.EndsWith("_c", StringComparison.Ordinal);
    }

    /// <summary>A root whose text is scanned for dependencies (panorama xml/css/js, vmat, vmdl, vpcf, vsndevts, vdata).</summary>
    public static bool IsTextRoot(string contentRel)
    {
        var kind = Kind(contentRel);

        if (kind == null || kind == RootKind.Map)
        {
            return false;
        }

        var extension = Paths.Extension(contentRel);

        return (kind == RootKind.Panorama && PanoramaTextExtensions.Contains(extension)) || KeyValuesTextExtensions.Contains(extension);
    }

    private static string Clean(string relative)
    {
        return Paths.ToForward(relative).TrimStart('/');
    }
}
