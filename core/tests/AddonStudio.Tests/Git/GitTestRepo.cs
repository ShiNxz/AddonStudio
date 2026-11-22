using AddonStudio.Core.Util;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Git;

/// <summary>A temp git repo with a fixed identity, no signing and no hooks, so tests do not depend on the machine's git config.</summary>
internal sealed class GitTestRepo : IDisposable
{
    private readonly TempDir temp = new();

    public GitTestRepo(string name = "repo", bool init = true)
    {
        Path = System.IO.Path.Combine(temp.Path, name);
        Directory.CreateDirectory(Path);

        if (init)
        {
            Init(Path);
        }
    }

    public string Path { get; }

    /// <summary>The temp folder the repo lives in (for remotes and other clones next to it).</summary>
    public string Root => temp.Path;

    public static void Init(string path, bool bare = false)
    {
        Directory.CreateDirectory(path);
        Git(path, bare ? ["-c", "init.defaultBranch=main", "init", "-q", "--bare"] : ["-c", "init.defaultBranch=main", "init", "-q"]);

        if (!bare)
        {
            Configure(path);
        }
    }

    public static void Configure(string path)
    {
        Git(path, ["config", "user.name", "Test User"]);
        Git(path, ["config", "user.email", "test@example.com"]);
        Git(path, ["config", "commit.gpgsign", "false"]);
        Git(path, ["config", "tag.gpgsign", "false"]);
        Git(path, ["config", "core.hooksPath", ".git/no-hooks"]);
        Git(path, ["config", "core.autocrlf", "false"]);
    }

    public string Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);

        return full;
    }

    public string Git(params string[] args) => Git(Path, args);

    public void CommitAll(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    public static string Git(string workingDirectory, IReadOnlyList<string> args)
    {
        var result = ProcessRunner.RunAsync("git", args, new ProcessOptions { WorkingDirectory = workingDirectory }).GetAwaiter().GetResult();

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.ErrorText}");
        }

        return result.Stdout;
    }

    public void Dispose() => temp.Dispose();
}
