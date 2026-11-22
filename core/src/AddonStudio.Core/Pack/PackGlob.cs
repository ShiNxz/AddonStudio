using System.Text;
using System.Text.RegularExpressions;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Pack;

/// <summary>
/// workshop.json pack globs, relative to the build dir, matched ignoring case: "*" is anything but "/", "?" one
/// character but "/", "**" anything, "**/" zero or more folders. A pattern without wildcards is a path prefix, the
/// way AddonRules and gameinfo's VpkDirectories read it.
/// </summary>
public static class PackGlob
{
    public static bool HasWildcards(string pattern) => pattern.IndexOfAny(['*', '?']) >= 0;

    /// <summary>The pattern cleaned the way AddonRules.Normalize does, without a leading "./" or "/".</summary>
    public static string Normalize(string pattern)
    {
        var clean = Paths.ToForward(pattern.Trim());

        while (clean.StartsWith("./", StringComparison.Ordinal))
        {
            clean = clean[2..];
        }

        return clean.TrimStart('/');
    }

    public static Regex ToRegex(string pattern)
    {
        var glob = Normalize(pattern);
        var builder = new StringBuilder("^");

        for (var i = 0; i < glob.Length; i++)
        {
            var character = glob[i];

            if (character == '*')
            {
                var doubleStar = i + 1 < glob.Length && glob[i + 1] == '*';

                if (doubleStar)
                {
                    i++;

                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(character.ToString()));
            }
        }

        // "folder/" or "folder/**" style patterns also take everything below
        if (glob.EndsWith('/'))
        {
            builder.Append(".*");
        }

        builder.Append('$');

        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsMatch(string pattern, string relativePath)
    {
        return ToRegex(pattern).IsMatch(Paths.ToForward(relativePath));
    }
}
