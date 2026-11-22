using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Projects;

/// <summary>
/// The &lt;cs2&gt;/content/csgo_addons/&lt;addon&gt; link to &lt;repo&gt;/content (SPEC section 4). A directory junction on
/// Windows (cmd /c mklink /J, no admin needed), a symlink elsewhere (dev only). Real folders are never deleted.
/// </summary>
public static class ContentLink
{
    private static readonly TimeSpan MklinkTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The state of the link at linkPath compared to the folder it should point at.</summary>
    public static LinkInfo Inspect(string linkPath, string expectedTarget)
    {
        var target = ReadTarget(linkPath);

        if (target != null)
        {
            return Paths.SamePath(target, expectedTarget)
                ? new LinkInfo(LinkState.Linked, linkPath, target, null)
                : new LinkInfo(LinkState.WrongTarget, linkPath, target, $"The CS2 content folder links to {target}, not to this project.");
        }

        if (Directory.Exists(linkPath) || File.Exists(linkPath))
        {
            return new LinkInfo(LinkState.Conflict, linkPath, null,
                "A real folder is in the way. Linking with backup renames it first, nothing is deleted.");
        }

        return new LinkInfo(LinkState.Missing, linkPath, null, "Not linked into CS2 yet.");
    }

    /// <summary>The full path a junction or symlink points at, null when the path is not a link (or does not exist).</summary>
    public static string? ReadTarget(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var target = info.LinkTarget;

            if (string.IsNullOrEmpty(target))
            {
                return null;
            }

            // junction targets can come back in NT form
            if (target.StartsWith(@"\??\", StringComparison.Ordinal) || target.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                target = target[4..];
            }

            if (!Path.IsPathRooted(target))
            {
                target = Path.Combine(Path.GetDirectoryName(info.FullName) ?? "", target);
            }

            return Paths.Full(target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Makes link point at target: a junction on Windows, a directory symlink elsewhere. The link path must be free.</summary>
    public static async Task CreateAsync(string link, string target, CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(link);

        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (OperatingSystem.IsWindows())
        {
            var shell = Environment.GetEnvironmentVariable("ComSpec");
            shell = string.IsNullOrWhiteSpace(shell) ? "cmd.exe" : shell;

            // Windows paths can not hold quotes, so plain quoting is safe; the command does not start with a quote,
            // so cmd keeps the quotes as they are
            var arguments = $"/d /c mklink /J \"{link}\" \"{target}\"";
            var result = await ProcessRunner.RunAsync(shell, arguments, new ProcessOptions { Timeout = MklinkTimeout }, ct).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw AddonStudioException.Internal($"mklink /J failed: {(result.TimedOut ? "timed out" : result.ErrorText)}");
            }
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    /// <summary>Removes a junction or symlink, never what it points at.</summary>
    public static void Remove(string link)
    {
        if (ReadTarget(link) == null)
        {
            throw AddonStudioException.Internal($"{link} is not a link, refusing to remove it.");
        }

        if (OperatingSystem.IsWindows())
        {
            // RemoveDirectory on a junction removes the junction itself
            Directory.Delete(link, recursive: false);
        }
        else
        {
            File.Delete(link);
        }
    }

    /// <summary>
    /// Renames a real folder (or file) at path to &lt;addon&gt;.addonstudio-backup-&lt;yyyyMMdd-HHmmss&gt; next to it and returns
    /// the new path. A "-2", "-3"... suffix is added when that name is taken.
    /// </summary>
    public static string Backup(string path, string addon, DateTimeOffset? now = null)
    {
        var parent = Path.GetDirectoryName(Paths.Full(path)) ?? throw AddonStudioException.Internal($"{path} has no parent folder.");
        var name = StatePaths.LinkBackupName(addon, now);
        var backup = Path.Combine(parent, name);

        for (var index = 2; Directory.Exists(backup) || File.Exists(backup); index++)
        {
            backup = Path.Combine(parent, $"{name}-{index}");
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Move(path, backup);
            }
            else
            {
                File.Move(path, backup);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AddonStudioException(ErrorCodes.Conflict,
                $"Could not rename {path} to {Path.GetFileName(backup)}: {exception.Message} Close CS2 and the Workshop Tools and try again.",
                new { linkPath = path }, exception);
        }

        return backup;
    }
}
