using System.Text.RegularExpressions;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Build;

/// <summary>
/// Finds the content files a text root references (SPEC section 4 "Dependencies"): panorama file://{images},
/// file://{resources}, s2r://, &lt;include src&gt;, CSS url() and @import; KV3 quoted strings ending in a source or
/// resource extension (.vsnd maps back to sounds/...wav|mp3). Only references that resolve to an existing content
/// file are kept, with that file's own casing.
/// </summary>
public static partial class DependencyScanner
{
    /// <summary>Text roots bigger than this are not scanned.</summary>
    public const long MaxScanBytes = 16 * 1024 * 1024;

    /// <summary>.vtex is KV3 text too and names its source images, so it is scanned like the KV3 roots.</summary>
    private static readonly string[] ExtraKeyValuesExtensions = [".vtex"];

    /// <summary>Whether a root's text is scanned.</summary>
    public static bool IsScanned(string contentRel)
    {
        return AssetRules.IsTextRoot(contentRel)
            || (AssetRules.IsCompilable(contentRel) && ExtraKeyValuesExtensions.Contains(Paths.Extension(contentRel)));
    }

    /// <summary>Reads the root and scans it. Empty when the file can not be read or is too big.</summary>
    /// <param name="contentDir">Full path of content/.</param>
    /// <param name="contentRel">The root, content-relative.</param>
    /// <param name="files">Every content file: lowercase-insensitive key to the path with its real casing.</param>
    public static IReadOnlyList<string> ScanFile(string contentDir, string contentRel, IReadOnlyDictionary<string, string> files)
    {
        if (!IsScanned(contentRel))
        {
            return [];
        }

        string text;

        try
        {
            var full = Path.Combine(contentDir, Paths.ToNative(contentRel));
            var info = new FileInfo(full);

            if (!info.Exists || info.Length > MaxScanBytes)
            {
                return [];
            }

            text = File.ReadAllText(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return Scan(contentRel, text, files);
    }

    /// <summary>The dependencies of a root given its text, sorted, without the root itself.</summary>
    public static IReadOnlyList<string> Scan(string contentRel, string text, IReadOnlyDictionary<string, string> files)
    {
        var root = Paths.ToForward(contentRel);
        var found = new HashSet<string>(Paths.RelComparer);
        var extension = Paths.Extension(root);

        void Keep(string candidate)
        {
            if (Paths.TryNormalizeRelative(StripQuery(candidate), out var normalized)
                && files.TryGetValue(normalized, out var actual)
                && !string.Equals(actual, root, Paths.RelComparison))
            {
                found.Add(actual);
            }
        }

        if (AssetRules.PanoramaTextExtensions.Contains(extension))
        {
            ScanPanorama(root, text, Keep);
        }
        else
        {
            ScanKeyValues(text, Keep);
        }

        return found.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ScanPanorama(string root, string text, Action<string> keep)
    {
        foreach (Match match in ImagesUrl().Matches(text))
        {
            keep("panorama/images/" + match.Groups[1].Value);
        }

        foreach (Match match in ResourcesUrl().Matches(text))
        {
            keep("panorama/" + match.Groups[1].Value);
        }

        foreach (Match match in S2rUrl().Matches(text))
        {
            foreach (var candidate in S2rCandidates(match.Groups[1].Value))
            {
                keep(candidate);
            }
        }

        // plain (scheme-less) references in include src, url() and @import are relative to the root's folder
        var folder = root.Contains('/') ? root[..root.LastIndexOf('/')] : "";

        foreach (var regex in new[] { IncludeSrc(), CssUrl(), CssImport() })
        {
            foreach (Match match in regex.Matches(text))
            {
                var value = match.Groups[1].Value.Trim();

                if (value.Length == 0 || value.Contains("://", StringComparison.Ordinal) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                keep(folder.Length == 0 ? value : folder + "/" + value);
                keep(value);
            }
        }
    }

    /// <summary>
    /// s2r://panorama/images/coin_png.vtex -> panorama/images/coin.png (or coin_png.vtex), .vxml -> .xml and so on,
    /// through AssetRules.SourceCandidates. A path that is not a compiled resource is taken as it is.
    /// </summary>
    private static IEnumerable<string> S2rCandidates(string path)
    {
        var clean = StripQuery(path);
        var compiled = clean.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? clean : clean + "_c";
        var candidates = AssetRules.SourceCandidates(compiled);

        return candidates.Count > 0 ? candidates : [clean];
    }

    private static void ScanKeyValues(string text, Action<string> keep)
    {
        foreach (Match match in QuotedString().Matches(text))
        {
            var value = Paths.ToForward(match.Groups[1].Value.Trim());
            var extension = Paths.Extension(value);

            if (extension == ".vsnd")
            {
                var stem = value[..^extension.Length];

                foreach (var sound in AssetRules.SoundExtensions)
                {
                    keep(stem + sound);

                    if (!stem.StartsWith("sounds/", StringComparison.OrdinalIgnoreCase))
                    {
                        keep("sounds/" + stem + sound);
                    }
                }

                continue;
            }

            if (AssetRules.ReferenceExtensions.Contains(extension))
            {
                keep(value);
            }
        }
    }

    private static string StripQuery(string path)
    {
        var cut = path.IndexOfAny(['?', '#']);

        return cut < 0 ? path : path[..cut];
    }

    [GeneratedRegex(@"file://\{images\}/([^""'()\s<>;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ImagesUrl();

    [GeneratedRegex(@"file://\{resources\}/([^""'()\s<>;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResourcesUrl();

    [GeneratedRegex(@"s2r://([^""'()\s<>;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex S2rUrl();

    [GeneratedRegex(@"<include\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex IncludeSrc();

    [GeneratedRegex(@"url\(\s*[""']?([^""')]+)[""']?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    [GeneratedRegex(@"@import\s+[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex CssImport();

    [GeneratedRegex(@"""([^""\r\n]{1,1024})""")]
    private static partial Regex QuotedString();
}
