using System.Globalization;
using System.Text.RegularExpressions;
using AddonStudio.Core.Contracts;

namespace AddonStudio.Core.Util;

/// <summary>
/// The deploy/&lt;yyyyMMdd-HHmmss&gt; tags publish puts on HEAD (SPEC section 5 step 4) and how git reads them back
/// into Deploy records. Shared so the writer (publish) and the reader (git log) agree.
/// </summary>
public static partial class DeployTags
{
    public const string Prefix = "deploy/";

    /// <summary>deploy/&lt;yyyyMMdd-HHmmss&gt; in UTC.</summary>
    public static string Name(DateTimeOffset at)
    {
        return Prefix + StatePaths.Timestamp(at.ToUniversalTime());
    }

    /// <summary>"Published to Steam Workshop item &lt;id&gt;\n\nfiles: N\nsize: B\nnote: &lt;first line&gt;"</summary>
    public static string Message(string workshopId, int files, long size, string? note)
    {
        var firstLine = FirstLine(note);

        return string.Create(CultureInfo.InvariantCulture, $"Published to Steam Workshop item {workshopId}\n\nfiles: {files}\nsize: {size}\nnote: {firstLine}");
    }

    public static bool IsDeployTag(string tagName) => tagName.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// A Deploy from a tag name and its annotated message. At comes from the name (UTC), else from fallbackDate.
    /// WorkshopId and Note come from the message when it has them. Null when the name is not a deploy tag.
    /// </summary>
    public static Deploy? Parse(string tagName, string? message, DateTimeOffset? fallbackDate = null)
    {
        if (!IsDeployTag(tagName))
        {
            return null;
        }

        DateTimeOffset at;

        if (DateTime.TryParseExact(tagName[Prefix.Length..], StatePaths.TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            at = new DateTimeOffset(parsed, TimeSpan.Zero);
        }
        else if (fallbackDate is { } date)
        {
            at = date;
        }
        else
        {
            return null;
        }

        string? workshopId = null;
        string? note = null;

        if (!string.IsNullOrEmpty(message))
        {
            var id = WorkshopIdPattern().Match(message);

            if (id.Success)
            {
                workshopId = id.Groups[1].Value;
            }

            var noteMatch = NotePattern().Match(message);

            if (noteMatch.Success)
            {
                var text = noteMatch.Groups[1].Value.Trim();
                note = text.Length == 0 ? null : text;
            }
        }

        return new Deploy(tagName, at, workshopId, note);
    }

    /// <summary>The first non-empty line, trimmed ("" for none).</summary>
    public static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return "";
    }

    [GeneratedRegex(@"Steam Workshop item (\d+)")]
    private static partial Regex WorkshopIdPattern();

    [GeneratedRegex(@"^note:[ \t]*(.*)$", RegexOptions.Multiline)]
    private static partial Regex NotePattern();
}
