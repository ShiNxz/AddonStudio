using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Git;

/// <summary>
/// Runs git (SPEC 6.8): argument lists only (never a shell string), -c core.quotepath=off on every call,
/// --no-optional-locks for reads, CreateNoWindow and UTF-8 through ProcessRunner. GIT_TERMINAL_PROMPT is left
/// alone so Git Credential Manager can show its sign-in window on Windows.
/// </summary>
internal static class GitRunner
{
    /// <summary>Keeps every command line well under the Windows limit of 32767 chars.</summary>
    private const int MaxBatchChars = 8000;

    private static readonly Lazy<string> GitPath = new(() => ProcessRunner.FindExecutable("git") ?? "git");

    public static string Executable => GitPath.Value;

    /// <summary>Runs git and returns the result whatever the exit code. Throws git_failed when git can not be started.</summary>
    public static async Task<ProcessResult> RunRawAsync(
        string? workingDirectory,
        IReadOnlyList<string> args,
        bool read = false,
        string? stdin = null,
        Action<string>? onStderr = null,
        CancellationToken ct = default)
    {
        var full = new List<string>(args.Count + 4) { "-c", "core.quotepath=off" };

        if (read)
        {
            full.Add("--no-optional-locks");
        }

        full.AddRange(args);

        var options = new ProcessOptions
        {
            WorkingDirectory = workingDirectory,
            StandardInput = stdin,
            OnStderr = onStderr,
        };

        try
        {
            return await ProcessRunner.RunAsync(Executable, full, options, ct).ConfigureAwait(false);
        }
        catch (ProcessStartException exception)
        {
            throw new AddonStudioException(ErrorCodes.GitFailed, "git is not installed or not on PATH. Install Git for Windows from https://git-scm.com.", null, exception);
        }
    }

    /// <summary>Runs git and returns stdout. Throws git_failed with git's own message on a non-zero exit.</summary>
    public static async Task<string> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> args,
        bool read = false,
        string? stdin = null,
        Action<string>? onStderr = null,
        CancellationToken ct = default)
    {
        var result = await RunRawAsync(workingDirectory, args, read, stdin, onStderr, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw Failed(args, result);
        }

        return result.Stdout;
    }

    /// <summary>Runs "git prefix... -- paths..." in batches that keep the command line short.</summary>
    public static async Task RunBatchedAsync(string repo, IReadOnlyList<string> prefix, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var batch = new List<string>();
        var chars = 0;

        foreach (var path in paths)
        {
            if (batch.Count > 0 && chars + path.Length + 3 > MaxBatchChars)
            {
                await RunAsync(repo, [.. prefix, "--", .. batch], ct: ct).ConfigureAwait(false);
                batch.Clear();
                chars = 0;
            }

            batch.Add(path);
            chars += path.Length + 3;
        }

        if (batch.Count > 0)
        {
            await RunAsync(repo, [.. prefix, "--", .. batch], ct: ct).ConfigureAwait(false);
        }
    }

    public static AddonStudioException Failed(IReadOnlyList<string> args, ProcessResult result)
    {
        var command = "git " + string.Join(' ', args.Take(3));
        var text = result.TimedOut ? "timed out" : result.ErrorText;

        if (string.IsNullOrWhiteSpace(text))
        {
            text = $"exit code {result.ExitCode}";
        }

        return AddonStudioException.GitFailed($"{command} failed: {text}", new { command, exitCode = result.ExitCode });
    }

    /// <summary>Runs another tool (gh, git lfs through git) and returns the result. Null when it is not installed.</summary>
    public static async Task<ProcessResult?> RunToolAsync(string name, string? workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var path = ProcessRunner.FindExecutable(name);

        if (path == null)
        {
            return null;
        }

        try
        {
            return await ProcessRunner.RunAsync(path, args, new ProcessOptions { WorkingDirectory = workingDirectory }, ct).ConfigureAwait(false);
        }
        catch (ProcessStartException)
        {
            return null;
        }
    }
}
