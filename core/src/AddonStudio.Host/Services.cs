using AddonStudio.Core.Build;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Env;
using AddonStudio.Core.Git;
using AddonStudio.Core.Import;
using AddonStudio.Core.Pack;
using AddonStudio.Core.Projects;
using AddonStudio.Core.Publish;
using AddonStudio.Core.Settings;
using AddonStudio.Core.Steam;
using AddonStudio.Core.Util;
using AddonStudio.Core.Watch;

namespace AddonStudio.Host;

/// <summary>The composition root: every service, wired with plain constructors (SPEC section 6).</summary>
public sealed class Services : IDisposable
{
    private bool disposed;

    public Services(
        ISettingsStore settings,
        IEnvironmentService env,
        IGitService git,
        IProjectService projects,
        IImportService import,
        IBuildService build,
        IPackService pack,
        ISteamService steam,
        IPublishService publish,
        IWatchService watch)
    {
        Settings = settings;
        Env = env;
        Git = git;
        Projects = projects;
        Import = import;
        Build = build;
        Pack = pack;
        Steam = steam;
        Publish = publish;
        Watch = watch;
    }

    public ISettingsStore Settings { get; }
    public IEnvironmentService Env { get; }
    public IGitService Git { get; }
    public IProjectService Projects { get; }
    public IImportService Import { get; }
    public IBuildService Build { get; }
    public IPackService Pack { get; }
    public ISteamService Steam { get; }
    public IPublishService Publish { get; }
    public IWatchService Watch { get; }

    /// <summary>The real services.</summary>
    public static Services CreateDefault()
    {
        var settings = new SettingsStore();
        var env = new EnvironmentService(settings);
        var git = new GitService();
        var projects = new ProjectService(settings, env, git);
        var import = new ImportService(projects, env, git);
        var build = new BuildService(projects, env, settings);
        long MaxPackBytes() => settings.Get().MaxPackBytes;
        var pack = new PackService(projects, env, MaxPackBytes);
        var steam = new SteamService(projects);
        var publish = new PublishService(projects, env, build, pack, git, steam, MaxPackBytes);
        var watch = new WatchService(projects);

        return new Services(settings, env, git, projects, import, build, pack, steam, publish, watch);
    }

    /// <summary>Disposes every service that holds something (watchers, the Steam client), newest first, each once.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object[] all = [Watch, Publish, Steam, Pack, Build, Import, Projects, Git, Env, Settings];

        foreach (var service in all)
        {
            if (service is IDisposable disposable && seen.Add(service))
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception exception)
                {
                    Log.Warn($"Disposing {service.GetType().Name} failed: {exception.Message}");
                }
            }
        }
    }
}
