using System.Reflection;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Env;
using AddonStudio.Core.Projects;
using AddonStudio.Core.Settings;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Projects;

/// <summary>
/// A temp folder with its own settings home and (optionally) a fake CS2 install, wired to the real settings,
/// environment and project services. Git is a fake that answers "not a repo".
/// </summary>
internal sealed class ProjectKit : IDisposable
{
    public ProjectKit(bool withCs2 = true)
    {
        Settings = new SettingsStore(Path.Combine(Temp.Path, "home"));

        if (withCs2)
        {
            Directory.CreateDirectory(Path.Combine(Cs2, "game", "csgo"));
            Directory.CreateDirectory(Path.Combine(Cs2, "content", "csgo_addons"));
            Directory.CreateDirectory(Path.Combine(Cs2, "game", "csgo_addons"));
            Settings.Set(new AppSettingsPatch { Cs2PathOverride = Cs2 });
        }
        else
        {
            // a folder that is not there, so the machine's own Steam library is never looked at
            Settings.Set(new AppSettingsPatch { Cs2PathOverride = Path.Combine(Temp.Path, "no-cs2") });
        }

        Env = new EnvironmentService(Settings);
        Git = FakeGit.Create();
        Projects = new ProjectService(Settings, Env, Git);
    }

    public TempDir Temp { get; } = new();

    public string Cs2 => Path.Combine(Temp.Path, "cs2");

    public SettingsStore Settings { get; }

    public EnvironmentService Env { get; }

    public IGitService Git { get; }

    public ProjectService Projects { get; }

    public string ContentLinkPath(string addon) => Path.Combine(Cs2, "content", "csgo_addons", addon);

    public string BuildDir(string addon) => Path.Combine(Cs2, "game", "csgo_addons", addon);

    public string Repo(string name) => Path.Combine(Temp.Path, "repos", name);

    public void Dispose() => Temp.Dispose();
}

/// <summary>IGitService that records calls: InfoAsync says "not a repo", LfsAvailableAsync false, void tasks complete.</summary>
public class FakeGit : DispatchProxy
{
    public List<string> Calls { get; } = [];

    public static IGitService Create() => Create<IGitService, FakeGit>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        Calls.Add(name);

        return name switch
        {
            nameof(IGitService.InfoAsync) => Task.FromResult(new ProjectGitInfo(false, null, null, false)),
            nameof(IGitService.LfsAvailableAsync) => Task.FromResult(false),
            _ when targetMethod.ReturnType == typeof(Task) => Task.CompletedTask,
            _ => throw new NotSupportedException(name),
        };
    }
}
