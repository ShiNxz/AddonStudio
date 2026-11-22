using System.Globalization;

namespace AddonStudio.Core.Util;

/// <summary>
/// The repo's local state folder, .addonstudio/ (never committed, see the repo's .gitignore):
/// build-state.json, last-publish.json, hash-cache.json, logs/, export/.
/// </summary>
public static class StatePaths
{
    public const string DirName = ".addonstudio";
    public const string BuildStateFile = "build-state.json";
    public const string LastPublishFile = "last-publish.json";
    public const string HashCacheFile = "hash-cache.json";
    public const string LogsDir = "logs";
    public const string ExportDir = "export";

    /// <summary>yyyyMMdd-HHmmss, used for export folders, link backups and deploy tags.</summary>
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>&lt;repo&gt;/.addonstudio</summary>
    public static string Dir(string repo) => Path.Combine(repo, DirName);

    /// <summary>&lt;repo&gt;/.addonstudio, created when missing.</summary>
    public static string Ensure(string repo)
    {
        var directory = Dir(repo);
        Directory.CreateDirectory(directory);

        return directory;
    }

    public static string BuildState(string repo) => Path.Combine(Dir(repo), BuildStateFile);

    public static string LastPublish(string repo) => Path.Combine(Dir(repo), LastPublishFile);

    public static string HashCache(string repo) => Path.Combine(Dir(repo), HashCacheFile);

    public static string Logs(string repo) => Path.Combine(Dir(repo), LogsDir);

    /// <summary>&lt;repo&gt;/.addonstudio/logs/&lt;name&gt;</summary>
    public static string LogFile(string repo, string name) => Path.Combine(Logs(repo), name);

    public static string ExportRoot(string repo) => Path.Combine(Dir(repo), ExportDir);

    /// <summary>&lt;repo&gt;/.addonstudio/export/&lt;yyyyMMdd-HHmmss&gt; (local time), the default pack.export folder. Not created.</summary>
    public static string NewExportDir(string repo, DateTimeOffset? now = null)
    {
        return Path.Combine(ExportRoot(repo), Timestamp((now ?? DateTimeOffset.Now).ToLocalTime()));
    }

    /// <summary>The name a conflicting CS2 content folder is renamed to: &lt;addon&gt;.addonstudio-backup-&lt;yyyyMMdd-HHmmss&gt; (local time).</summary>
    public static string LinkBackupName(string addon, DateTimeOffset? now = null)
    {
        return $"{addon}.addonstudio-backup-{Timestamp((now ?? DateTimeOffset.Now).ToLocalTime())}";
    }

    /// <summary>The time as yyyyMMdd-HHmmss in its own offset.</summary>
    public static string Timestamp(DateTimeOffset time)
    {
        return time.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }
}
