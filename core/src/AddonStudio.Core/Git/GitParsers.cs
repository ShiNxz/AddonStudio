using System.Globalization;
using System.Text.RegularExpressions;
using AddonStudio.Core.Contracts;

namespace AddonStudio.Core.Git;

/// <summary>Parsers for git's machine-readable output. Pure functions, so they are tested without git.</summary>
internal static partial class GitParsers
{
    /// <summary>Field separator in our log and for-each-ref formats.</summary>
    public const char Unit = '\u001f';

    /// <summary>Record start marker in our for-each-ref format.</summary>
    public const char Record = '\u001e';

    /// <summary>git log --format, used with -z (records end with NUL). The body is last so stray separators in it stay in it.</summary>
    public const string LogFormat = "%H%x1f%h%x1f%P%x1f%an%x1f%aI%x1f%D%x1f%s%x1f%b";

    /// <summary>git for-each-ref --format: name, object type, object, peeled object, creator date, contents.</summary>
    public const string TagFormat = "%1e%(refname:strip=2)%1f%(objecttype)%1f%(objectname)%1f%(*objectname)%1f%(creatordate:iso-strict)%1f%(contents)";

    public sealed record StatusHeader(string? Branch, string? Upstream, int Ahead, int Behind, bool HasCommits);

    /// <summary>One entry of git status --porcelain=v2 -z before LFS and size are worked out.</summary>
    public sealed record StatusEntry(string Path, string? OrigPath, GitState Index, GitState Worktree);

    /// <summary>A commit from LogFormat before tags, version and deploys are added.</summary>
    public sealed record RawCommit(string Hash, string Short, IReadOnlyList<string> Parents, string AuthorName, DateTimeOffset Date, IReadOnlyList<string> Refs, string Subject, string Body);

    /// <summary>Parses git status --porcelain=v2 -z --branch (ordinary, renamed/copied, unmerged, untracked, ignored).</summary>
    public static (StatusHeader Header, List<StatusEntry> Entries) ParseStatus(string output)
    {
        string? branch = null;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var hasCommits = true;
        var entries = new List<StatusEntry>();
        var fields = output.Split('\0');

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];

            if (field.Length == 0)
            {
                continue;
            }

            switch (field[0])
            {
                case '#':
                    if (field.StartsWith("# branch.oid ", StringComparison.Ordinal))
                    {
                        hasCommits = field["# branch.oid ".Length..] != "(initial)";
                    }
                    else if (field.StartsWith("# branch.head ", StringComparison.Ordinal))
                    {
                        var head = field["# branch.head ".Length..];
                        branch = head == "(detached)" ? null : head;
                    }
                    else if (field.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                    {
                        upstream = field["# branch.upstream ".Length..];
                    }
                    else if (field.StartsWith("# branch.ab ", StringComparison.Ordinal))
                    {
                        foreach (var part in field["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (part.Length > 1 && int.TryParse(part[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                            {
                                if (part[0] == '+')
                                {
                                    ahead = count;
                                }
                                else if (part[0] == '-')
                                {
                                    behind = count;
                                }
                            }
                        }
                    }

                    break;

                case '1':
                {
                    // 1 XY sub mH mI mW hH hI path
                    var parts = field.Split(' ', 9);

                    if (parts.Length == 9)
                    {
                        entries.Add(new StatusEntry(parts[8], null, State(parts[1][0]), State(parts[1][1])));
                    }

                    break;
                }

                case '2':
                {
                    // 2 XY sub mH mI mW hH hI Xscore path NUL origPath
                    var parts = field.Split(' ', 10);
                    var orig = index + 1 < fields.Length ? fields[++index] : null;

                    if (parts.Length == 10)
                    {
                        entries.Add(new StatusEntry(parts[9], orig, State(parts[1][0]), State(parts[1][1])));
                    }

                    break;
                }

                case 'u':
                {
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    var parts = field.Split(' ', 11);

                    if (parts.Length == 11)
                    {
                        entries.Add(new StatusEntry(parts[10], null, GitState.Conflicted, GitState.Conflicted));
                    }

                    break;
                }

                case '?' when field.Length > 2:
                    entries.Add(new StatusEntry(field[2..], null, GitState.Untracked, GitState.Untracked));
                    break;

                case '!' when field.Length > 2:
                    entries.Add(new StatusEntry(field[2..], null, GitState.Ignored, GitState.Ignored));
                    break;
            }
        }

        return (new StatusHeader(branch, upstream, ahead, behind, hasCommits), entries);
    }

    /// <summary>An X or Y letter of git status.</summary>
    public static GitState State(char letter) => letter switch
    {
        'M' => GitState.Modified,
        'T' => GitState.TypeChanged,
        'A' => GitState.Added,
        'D' => GitState.Deleted,
        'R' => GitState.Renamed,
        'C' => GitState.Copied,
        'U' => GitState.Conflicted,
        '?' => GitState.Untracked,
        '!' => GitState.Ignored,
        _ => GitState.Unmodified,
    };

    /// <summary>Staged = the index side has a change.</summary>
    public static bool IsStaged(GitState index) => index is not (GitState.Unmodified or GitState.Untracked or GitState.Ignored);

    /// <summary>Parses git log -z --format=LogFormat.</summary>
    public static List<RawCommit> ParseLog(string output)
    {
        var commits = new List<RawCommit>();

        foreach (var record in output.Split('\0'))
        {
            var text = record.TrimStart('\n');

            if (text.Length == 0)
            {
                continue;
            }

            var parts = text.Split(Unit, 8);

            if (parts.Length < 8)
            {
                continue;
            }

            var date = DateTimeOffset.TryParse(parts[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : DateTimeOffset.MinValue;

            commits.Add(new RawCommit(
                parts[0],
                parts[1],
                parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                parts[3],
                date,
                ParseRefs(parts[5]),
                parts[6],
                parts[7].TrimEnd('\n', '\r', ' ')));
        }

        return commits;
    }

    /// <summary>Branch and remote names from a %D decoration, without tags ("HEAD -> main" gives "main").</summary>
    public static List<string> ParseRefs(string decoration)
    {
        var refs = new List<string>();

        foreach (var raw in decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("tag: ", StringComparison.Ordinal))
            {
                continue;
            }

            var name = raw.StartsWith("HEAD -> ", StringComparison.Ordinal) ? raw["HEAD -> ".Length..] : raw;

            if (name.Length > 0 && !refs.Contains(name))
            {
                refs.Add(name);
            }
        }

        return refs;
    }

    /// <summary>Parses git for-each-ref refs/tags --format=TagFormat.</summary>
    public static List<GitTag> ParseTags(string output)
    {
        var tags = new List<GitTag>();

        foreach (var record in output.Split(Record))
        {
            if (record.Length == 0)
            {
                continue;
            }

            var parts = record.Split(Unit, 6);

            if (parts.Length < 6 || parts[0].Length == 0)
            {
                continue;
            }

            var annotated = parts[1] == "tag";
            var commit = annotated && parts[3].Length > 0 ? parts[3] : parts[2];
            DateTimeOffset? date = DateTimeOffset.TryParse(parts[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            var message = annotated ? parts[5].TrimEnd('\n', '\r') : null;

            tags.Add(new GitTag(parts[0], commit, annotated, message, annotated ? date : null));
        }

        return tags;
    }

    /// <summary>
    /// Joins git diff-tree -z --name-status and -z --numstat output into the files of a commit. Renames and copies
    /// are listed under the new path; binary when numstat shows "-".
    /// </summary>
    public static List<GitCommitFile> ParseCommitFiles(string nameStatus, string numstat)
    {
        var stats = new Dictionary<string, (int? Added, int? Deleted, bool Binary)>(StringComparer.Ordinal);
        var numFields = numstat.Split('\0');

        for (var index = 0; index < numFields.Length; index++)
        {
            var field = numFields[index].TrimStart('\n');

            if (field.Length == 0)
            {
                continue;
            }

            var parts = field.Split('\t', 3);

            if (parts.Length < 3)
            {
                continue;
            }

            var path = parts[2];

            if (path.Length == 0)
            {
                // rename or copy: "added\tdeleted\t" NUL old NUL new
                index += 2;
                path = index < numFields.Length ? numFields[index] : "";
            }

            var binary = parts[0] == "-" || parts[1] == "-";
            int? added = binary ? null : int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : null;
            int? deleted = binary ? null : int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : null;
            stats[path] = (added, deleted, binary);
        }

        var files = new List<GitCommitFile>();
        var statusFields = nameStatus.Split('\0');

        for (var index = 0; index < statusFields.Length; index++)
        {
            var code = statusFields[index].TrimStart('\n');

            if (code.Length == 0)
            {
                continue;
            }

            var state = State(code[0]);
            string path;

            if (state is GitState.Renamed or GitState.Copied)
            {
                index += 2;
                path = index < statusFields.Length ? statusFields[index] : "";
            }
            else
            {
                index += 1;
                path = index < statusFields.Length ? statusFields[index] : "";
            }

            if (path.Length == 0)
            {
                continue;
            }

            var stat = stats.GetValueOrDefault(path);
            files.Add(new GitCommitFile(path, state, stat.Added, stat.Deleted, stat.Binary));
        }

        return files;
    }

    /// <summary>A --progress stderr line as a git.progress event ("remote: " is dropped from the phase).</summary>
    public static GitProgressEvent? ParseProgress(string line)
    {
        var text = line.Trim();

        if (text.Length == 0)
        {
            return null;
        }

        var body = text.StartsWith("remote: ", StringComparison.Ordinal) ? text["remote: ".Length..] : text;
        var colon = body.IndexOf(':');
        var phase = colon > 0 ? body[..colon].Trim() : "";
        var percent = PercentPattern().Match(body);
        int? value = percent.Success && int.TryParse(percent.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? Math.Clamp(number, 0, 100)
            : null;

        return new GitProgressEvent(phase, value, text);
    }

    /// <summary>The first line of a commit message.</summary>
    public static string Subject(string message)
    {
        var trimmed = message.TrimStart('\r', '\n', ' ', '\t');
        var end = trimmed.IndexOf('\n');

        return (end < 0 ? trimmed : trimmed[..end]).Trim();
    }

    /// <summary>
    /// The version a subject gives with the autoPublish pattern (group 1, or the whole match without groups), null when
    /// it does not match or the pattern is broken.
    /// </summary>
    public static string? MatchVersion(string? pattern, string subject)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        try
        {
            var match = Regex.Match(subject, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

            if (!match.Success)
            {
                return null;
            }

            var value = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;

            return value.Length == 0 ? null : value;
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>The version of a v* tag ("v1.2.3" gives "1.2.3"), null for other tags.</summary>
    public static string? TagVersion(string tag)
    {
        return VersionTagPattern().IsMatch(tag) ? tag[1..] : null;
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex PercentPattern();

    [GeneratedRegex(@"^v\d[0-9A-Za-z._+-]*$")]
    private static partial Regex VersionTagPattern();
}
