using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;
using AddonStudio.Host.Rpc;

namespace AddonStudio.Host.Cli;

/// <summary>
/// CLI verbs for testing on Windows without the UI (SPEC section 7). The result is printed to stdout as indented
/// JSON, events go to stderr as JSON lines, an error prints {"error": {code, message, details}} and exits 1.
/// </summary>
public static class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitUsage = 2;

    public const string Usage = """
        addonstudio-core - Addon Studio core

        usage:
          addonstudio-core rpc                       JSON lines on stdin/stdout (what the app runs)
          addonstudio-core doctor                    environment checks
          addonstudio-core plan <repo>               build plan
          addonstudio-core build <repo> [--full]     build changed roots (or every root)
          addonstudio-core pack <repo> [--out <dir>] export the VPK (default .addonstudio/export/<time>)
          addonstudio-core pack <repo> --contents    list what goes into the VPK
          addonstudio-core publish <repo> --note "..." [--version x.y.z] [--skip-build] [--push]
                                                     [--github-release] [--allow-source-change]
          addonstudio-core status <repo>             Steam status of the project's item
          addonstudio-core version

        Results print as JSON on stdout, progress events as JSON lines on stderr.
        """;

    public static async Task<int> RunAsync(string[] args, Services services, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            stderr.WriteLine(Usage);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        var verb = args[0];
        var rest = args.Skip(1).ToList();
        var events = new DelegateEventSink((name, data) =>
        {
            var line = Json.SerializeObject(new { @event = name, data });

            lock (stderr)
            {
                stderr.WriteLine(line);
                stderr.Flush();
            }
        });

        object? result;

        try
        {
            switch (verb)
            {
                case "version" or "--version":
                    result = HostInfo.Create(services.Env);
                    break;
                case "doctor":
                    result = await services.Env.DoctorAsync(ct);
                    break;
                case "plan":
                {
                    var repo = Repo(rest);
                    RejectUnknown(rest);
                    result = await services.Build.PlanAsync(repo, ct);
                    break;
                }
                case "build":
                {
                    var mode = TakeFlag(rest, "--full") ? BuildMode.Full : BuildMode.Changed;
                    var repo = Repo(rest);
                    RejectUnknown(rest);
                    result = await services.Build.RunAsync(repo, mode, null, events, ct);
                    break;
                }
                case "pack":
                {
                    var contents = TakeFlag(rest, "--contents");
                    var outDir = TakeOption(rest, "--out");
                    var repo = Repo(rest);
                    RejectUnknown(rest);
                    result = contents
                        ? await services.Pack.ContentsAsync(repo, ct)
                        : await services.Pack.ExportAsync(repo, outDir, ct);
                    break;
                }
                case "publish":
                {
                    var note = TakeOption(rest, "--note") ?? throw new CliUsageException("publish needs --note \"...\".");
                    var version = TakeOption(rest, "--version");
                    var skipBuild = TakeFlag(rest, "--skip-build");
                    var push = TakeFlag(rest, "--push");
                    var githubRelease = TakeFlag(rest, "--github-release");
                    var allowSourceChange = TakeFlag(rest, "--allow-source-change");
                    var repo = Repo(rest);
                    RejectUnknown(rest);
                    var request = new PublishRequest(repo, note, version, skipBuild, push, githubRelease, allowSourceChange);
                    result = await services.Publish.RunAsync(request, events, ct);
                    break;
                }
                case "status":
                {
                    var repo = Repo(rest);
                    RejectUnknown(rest);
                    result = await services.Steam.ProjectStatusAsync(repo, ct);
                    break;
                }
                default:
                    throw new CliUsageException($"Unknown command \"{verb}\".");
            }
        }
        catch (CliUsageException exception)
        {
            stderr.WriteLine(exception.Message);
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return ExitUsage;
        }
        catch (Exception exception)
        {
            var (code, message, details) = RpcServer.ToError(exception, ct, verb);
            stdout.WriteLine(Json.SerializeObject(new { error = new { code, message, details } }, indented: true));
            stdout.Flush();
            stderr.WriteLine($"{code}: {message}");
            return ExitError;
        }

        stdout.WriteLine(Json.SerializeObject(result, indented: true));
        stdout.Flush();

        return ExitOk;
    }

    /// <summary>The first positional argument as a full path, removed from the list.</summary>
    private static string Repo(List<string> args)
    {
        var index = args.FindIndex(arg => !arg.StartsWith("--", StringComparison.Ordinal));

        if (index < 0)
        {
            throw new CliUsageException("Missing <repo>.");
        }

        var repo = args[index];
        args.RemoveAt(index);

        return Path.GetFullPath(repo);
    }

    private static bool TakeFlag(List<string> args, string flag)
    {
        return args.RemoveAll(arg => arg.Equals(flag, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>--name value or --name=value, removed from the list. Null when absent.</summary>
    private static string? TakeOption(List<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i][(name.Length + 1)..];
                args.RemoveAt(i);
                return value;
            }

            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    throw new CliUsageException($"{name} needs a value.");
                }

                var value = args[i + 1];
                args.RemoveRange(i, 2);
                return value;
            }
        }

        return null;
    }

    private static void RejectUnknown(List<string> args)
    {
        if (args.Count > 0)
        {
            throw new CliUsageException($"Unexpected argument \"{args[0]}\".");
        }
    }

    private sealed class CliUsageException(string message) : Exception(message);
}
