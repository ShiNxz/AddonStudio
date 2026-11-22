using AddonStudio.Core.Contracts;

namespace AddonStudio.Core.Util;

/// <summary>
/// Path helpers. Two kinds of paths go around:
/// - absolute paths (repo, CS2 folders) in the OS's own form, compared with FsComparison;
/// - relative asset paths (content-, build-, repo-relative) with forward slashes, compared ignoring case (RelComparer).
/// </summary>
public static class Paths
{
    /// <summary>How this OS compares absolute paths: ignoring case on Windows and macOS, exact on Linux.</summary>
    public static StringComparison FsComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer FsComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Relative asset paths ignore case everywhere, like CS2 and Windows do.</summary>
    public static StringComparer RelComparer => StringComparer.OrdinalIgnoreCase;

    public static StringComparison RelComparison => StringComparison.OrdinalIgnoreCase;

    /// <summary>Backslashes to forward slashes.</summary>
    public static string ToForward(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>A forward-slash relative path with this OS's separator.</summary>
    public static string ToNative(string relative)
    {
        return Path.DirectorySeparatorChar == '/' ? relative : relative.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>Full path without a trailing separator (a drive or file system root keeps its one).</summary>
    public static string Full(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>Same file system path after making both full.</summary>
    public static bool SamePath(string a, string b)
    {
        return string.Equals(Full(a), Full(b), FsComparison);
    }

    /// <summary>
    /// True when child is inside parent after both are made full (so "a/../.." tricks are resolved first).
    /// Equal paths count only with allowEqual. Purely lexical: junctions and symlinks are not followed.
    /// </summary>
    public static bool IsUnder(string parent, string child, bool allowEqual = false)
    {
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child) || parent.Contains('\0') || child.Contains('\0'))
        {
            return false;
        }

        var fullParent = Full(parent);
        var fullChild = Full(child);

        if (string.Equals(fullParent, fullChild, FsComparison))
        {
            return allowEqual;
        }

        var prefix = Path.EndsInDirectorySeparator(fullParent) ? fullParent : fullParent + Path.DirectorySeparatorChar;

        return fullChild.StartsWith(prefix, FsComparison);
    }

    /// <summary>fullPath relative to root with forward slashes. Throws ArgumentException when it is not inside root.</summary>
    public static string Relative(string root, string fullPath)
    {
        if (!IsUnder(root, fullPath))
        {
            throw new ArgumentException($"\"{fullPath}\" is not inside \"{root}\".", nameof(fullPath));
        }

        return ToForward(Path.GetRelativePath(Full(root), Full(fullPath)));
    }

    /// <summary>
    /// Cleans a relative path: forward slashes, no empty or "." segments, no leading or trailing slash.
    /// False for empty input, rooted paths (/x, C:x, \\server), ".." segments, NUL, ':' (drives, NTFS streams)
    /// and segments made only of dots and spaces.
    /// </summary>
    public static bool TryNormalizeRelative(string? relative, out string normalized)
    {
        normalized = "";

        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0'))
        {
            return false;
        }

        var path = ToForward(relative.Trim());

        if (path.StartsWith('/') || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            return false;
        }

        var segments = new List<string>();

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == ".." || segment.Contains(':') || segment.Trim('.', ' ').Length == 0)
            {
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return false;
        }

        normalized = string.Join('/', segments);

        return true;
    }

    /// <summary>TryNormalizeRelative or throws invalid_params naming the bad path.</summary>
    public static string NormalizeRelative(string? relative)
    {
        if (!TryNormalizeRelative(relative, out var normalized))
        {
            throw new AddonStudioException(ErrorCodes.InvalidParams, $"Not a safe relative path: \"{relative}\".");
        }

        return normalized;
    }

    /// <summary>True when the relative path is safe to join under a root.</summary>
    public static bool IsSafeRelative(string? relative)
    {
        return TryNormalizeRelative(relative, out _);
    }

    /// <summary>root + a relative path, as a full native path that is guaranteed to be inside root. Throws invalid_params otherwise.</summary>
    public static string SafeCombine(string root, string relative)
    {
        var normalized = NormalizeRelative(relative);
        var full = Path.GetFullPath(Path.Combine(Full(root), ToNative(normalized)));

        if (!IsUnder(root, full))
        {
            throw new AddonStudioException(ErrorCodes.InvalidParams, $"\"{relative}\" leaves \"{root}\".");
        }

        return full;
    }

    /// <summary>Lowercase extension with the dot (".vmdl"), "" when there is none.</summary>
    public static string Extension(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant();
    }

    /// <summary>
    /// Every file under root as forward-slash relative paths, skipping the folders named in skipDirs (matched on any
    /// level, ignoring case). Reparse points (junctions, symlinks) below root are not followed.
    /// </summary>
    public static List<string> ListFiles(string root, IReadOnlyCollection<string>? skipDirs = null)
    {
        var result = new List<string>();

        if (!Directory.Exists(root))
        {
            return result;
        }

        var fullRoot = Full(root);
        var pending = new Stack<string>();
        pending.Push(fullRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<FileSystemInfo> entries;

            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (skipDirs != null && skipDirs.Contains(sub.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pending.Push(sub.FullName);
                }
                else
                {
                    result.Add(ToForward(Path.GetRelativePath(fullRoot, entry.FullName)));
                }
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);

        return result;
    }
}
