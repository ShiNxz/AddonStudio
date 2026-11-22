using AddonStudio.Core.Build;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Git;
using AddonStudio.Core.Import;
using AddonStudio.Core.Pack;
using AddonStudio.Core.Projects;
using AddonStudio.Core.Publish;
using AddonStudio.Core.Settings;
using AddonStudio.Core.Steam;
using AddonStudio.Core.Watch;
using AddonStudio.Host;

namespace AddonStudio.Tests.Support;

/// <summary>The real service classes wired like Services.CreateDefault, with a fake environment and optional replacements.</summary>
public static class TestServices
{
    public static Services Create(IEnvironmentService? env = null, IBuildService? build = null, IGitService? git = null)
    {
        var settings = new SettingsStore();
        env ??= new FakeEnvironment();
        git ??= new GitService();
        var projects = new ProjectService(settings, env, git);
        var import = new ImportService(projects, env);
        build ??= new BuildService(projects, env, settings);
        var pack = new PackService(projects, env);
        var steam = new SteamService(projects);
        var publish = new PublishService(projects, env, build, pack, git, steam);
        var watch = new WatchService(projects);

        return new Services(settings, env, git, projects, import, build, pack, steam, publish, watch);
    }
}
