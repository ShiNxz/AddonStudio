using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Build;

/// <summary>
/// A fake CS2 install and project in a temp folder: game/bin/win64/resourcecompiler.exe is a sh script that writes the
/// expected output for its -i input (fails on files containing FAILME, warns on WARNME, hangs on SLOWME),
/// content/csgo_addons/&lt;addon&gt; is a symlink to the repo's content/, game/csgo/gameinfo.gi has packing rules.
/// </summary>
public sealed class FakeCs2 : IDisposable
{
    public const string AddonName = "test_addon";

    private const string CompilerScript = """
        #!/bin/sh
        in=""
        while [ $# -gt 0 ]; do
          case "$1" in
            -i) shift; in="$1" ;;
          esac
          shift
        done
        echo "compiling $in"
        if grep -q SLOWME "$in"; then exec sleep 30; fi
        if grep -q FAILME "$in"; then echo "ERROR: failed to compile $in"; exit 1; fi
        if grep -q WARNME "$in"; then echo "WARNING: something odd"; fi
        cs2="$(cd "$(dirname "$0")/../../.." && pwd)"
        rel="${in#*/content/csgo_addons/}"
        out="$cs2/game/csgo_addons/$rel"
        case "$out" in
          *.xml) out="${out%.xml}.vxml_c" ;;
          *.css) out="${out%.css}.vcss_c" ;;
          *.js) out="${out%.js}.vjs_c" ;;
          *.png) out="${out%.png}_png.vtex_c" ;;
          *) out="${out}_c" ;;
        esac
        mkdir -p "$(dirname "$out")"
        cat "$in" > "$out"
        echo "1 compiled, 0 failed, 0 skipped"
        exit 0
        """;

    private const string GameInfo = """
        "GameInfo"
        {
            game "Counter-Strike 2"
            AddonConfig
            {
                VpkDirectories
                {
                    exclude "materials/skip/"
                    include "panorama/"
                    include "models/"
                    include "materials/"
                }
            }
        }
        """;

    private readonly TempDir temp = new();

    public FakeCs2(bool withCompiler = true)
    {
        Cs2 = Path.Combine(temp.Path, "cs2");
        Repo = Path.Combine(temp.Path, "repo");
        Content = Path.Combine(Repo, "content");
        Prebuilt = Path.Combine(Repo, "prebuilt");
        LinkPath = Path.Combine(Cs2, "content", "csgo_addons", AddonName);
        BuildDir = Path.Combine(Cs2, "game", "csgo_addons", AddonName);
        Compiler = Path.Combine(Cs2, "game", "bin", "win64", "resourcecompiler.exe");
        GameInfoPath = Path.Combine(Cs2, "game", "csgo", "gameinfo.gi");

        Directory.CreateDirectory(Content);
        Directory.CreateDirectory(Prebuilt);
        Directory.CreateDirectory(Path.GetDirectoryName(LinkPath)!);
        Directory.CreateSymbolicLink(LinkPath, Content);
        Directory.CreateDirectory(Path.GetDirectoryName(GameInfoPath)!);
        File.WriteAllText(GameInfoPath, GameInfo);

        if (withCompiler)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Compiler)!);
            File.WriteAllText(Compiler, CompilerScript.Replace("\r\n", "\n") + "\n");

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Compiler, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Projects = new FakeProjects(this);
        Env = new FakeCs2Environment(this);
        Settings = new FakeSettings();
    }

    public string Cs2 { get; }
    public string Repo { get; }
    public string Content { get; }
    public string Prebuilt { get; }
    public string LinkPath { get; }
    public string BuildDir { get; }
    public string Compiler { get; }
    public string GameInfoPath { get; }

    public FakeProjects Projects { get; }
    public FakeCs2Environment Env { get; }
    public FakeSettings Settings { get; }

    public WorkshopConfig Config { get; set; } = WorkshopConfig.CreateDefault(AddonName, "Test Addon");

    public ProjectPaths Paths => new(Repo, Content, Prebuilt, StatePaths.Dir(Repo), AddonName, Cs2, LinkPath, BuildDir);

    public string WriteContent(string relative, string text) => Write(Content, relative, text);

    public string WritePrebuilt(string relative, string text) => Write(Prebuilt, relative, text);

    public string WriteBuild(string relative, string text) => Write(BuildDir, relative, text);

    public string BuildFile(string relative) => Path.Combine(BuildDir, relative.Replace('/', Path.DirectorySeparatorChar));

    public string ContentFile(string relative) => Path.Combine(Content, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Write(string root, string relative, string text)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);

        return full;
    }

    public void Dispose()
    {
        try
        {
            // the link first, so deleting the tree never walks into content/ twice
            Directory.Delete(LinkPath);
        }
        catch (IOException)
        {
        }

        temp.Dispose();
    }
}

public sealed class FakeProjects(FakeCs2 cs2) : IProjectService
{
    public LinkState LinkState { get; set; } = LinkState.Linked;

    public ProjectPaths GetPaths(string repo) => cs2.Paths;

    public WorkshopConfig LoadConfig(string repo) => cs2.Config;

    public LinkInfo GetLink(string repo) => new(LinkState, cs2.LinkPath, cs2.Content, null);

    public Task<IReadOnlyList<RecentProject>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ProjectInfo> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ProjectInfo> CreateAsync(string path, string addon, string title, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ProjectInfo> CloneAsync(string url, string path, IEventSink events, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ProjectInfo> SaveConfigAsync(string path, WorkshopConfig config, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ProjectInfo> LinkAsync(string path, bool backup, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<RecentProject>> RemoveAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<Cs2Addon>> ListCs2AddonsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<LinkOutcome> CreateLinkAsync(string repo, bool backup, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed class FakeCs2Environment(FakeCs2 cs2) : IEnvironmentService
{
    public Os Os => Platform.Os;

    public Task<DoctorReport> DoctorAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public string? Cs2Path() => cs2.Cs2;

    public string? CompilerPath() => File.Exists(cs2.Compiler) ? cs2.Compiler : null;

    public string? GameInfoPath() => cs2.GameInfoPath;

    public string RequireCs2Path() => cs2.Cs2;

    public string RequireCompilerPath() => CompilerPath() ?? throw new AddonStudioException(ErrorCodes.ToolsMissing, "no compiler");

    public bool CanCompile() => CompilerPath() != null;

    public bool CanPublish() => false;

    public void Invalidate()
    {
    }
}

public sealed class FakeSettings : ISettingsStore
{
    public AppSettings Settings { get; set; } = new() { CompileParallelism = 2 };

    public string HomeDir => Path.GetTempPath();

    public string SettingsPath => Path.Combine(HomeDir, "settings.json");

    public AppSettings Get() => Settings;

    public AppSettings Set(AppSettingsPatch patch) => Settings = patch.ApplyTo(Settings);

    public IReadOnlyList<RecentProject> TouchRecent(string path, string addon, string title) => [];

    public IReadOnlyList<RecentProject> RemoveRecent(string path) => [];
}

/// <summary>Collects events in order.</summary>
public sealed class RecordingSink : IEventSink
{
    private readonly List<(string Name, object Data)> events = [];

    public event Action<string, object>? Emitted;

    public IReadOnlyList<(string Name, object Data)> Events
    {
        get
        {
            lock (events)
            {
                return events.ToList();
            }
        }
    }

    public void Emit(string eventName, object data)
    {
        lock (events)
        {
            events.Add((eventName, data));
        }

        Emitted?.Invoke(eventName, data);
    }
}
