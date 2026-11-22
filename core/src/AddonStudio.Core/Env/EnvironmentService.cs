using System.Diagnostics;
using System.Runtime.Versioning;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;
using CS2WorkshopManager;

namespace AddonStudio.Core.Env;

/// <summary>
/// SPEC 6.1: what this machine has. The CS2 lookup through Steam's library folders is cached until Invalidate or a
/// change of settings.cs2PathOverride. Never starts the Steam client.
/// </summary>
public sealed class EnvironmentService : IEnvironmentService
{
    public const string WorkshopToolsFix = "Steam > CS2 > Properties > DLC > Counter-Strike 2 Workshop Tools";

    private const string LfsDocFix = "See docs/GITHUB-LFS.md for the setup steps.";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(15);

    private readonly ISettingsStore settings;
    private readonly object gate = new();
    private Cs2Lookup? cached;

    public EnvironmentService(ISettingsStore settings)
    {
        this.settings = settings;
    }

    public Os Os => Platform.Os;

    public async Task<DoctorReport> DoctorAsync(CancellationToken ct = default)
    {
        Invalidate();

        var gitTask = ToolVersionAsync("git", ["--version"], ct);
        var lfsTask = ToolVersionAsync("git", ["lfs", "version"], ct);
        var ghTask = ToolVersionAsync("gh", ["--version"], ct);
        var ghAuthTask = GhAuthAsync(ct);

        var lookup = Lookup();
        var cs2 = lookup.Path;
        var compiler = CompilerPath();
        var steamPath = SteamInstallPath();
        var steamRunning = IsSteamRunning();

        var checks = new List<DoctorCheck>
        {
            Platform.IsWindows
                ? new DoctorCheck(DoctorCheckId.Os, "Windows", CheckStatus.Ok, Environment.OSVersion.VersionString, null)
                : new DoctorCheck(DoctorCheckId.Os, "Windows", CheckStatus.Warn,
                    $"Running on {Os.ToString().ToLowerInvariant()}. Compiling and publishing only work on Windows.",
                    "Run Addon Studio on Windows to compile and publish. Git and project settings work here."),

            steamPath != null
                ? new DoctorCheck(DoctorCheckId.Steam, "Steam installed", CheckStatus.Ok, steamPath, null)
                : new DoctorCheck(DoctorCheckId.Steam, "Steam installed", CheckStatus.Error, "Steam was not found on this PC.",
                    "Install Steam from https://store.steampowered.com/about/ and log in."),

            cs2 != null
                ? new DoctorCheck(DoctorCheckId.Cs2, "Counter-Strike 2", CheckStatus.Ok, cs2, null)
                : new DoctorCheck(DoctorCheckId.Cs2, "Counter-Strike 2", CheckStatus.Error, lookup.Error ?? "Counter-Strike 2 was not found.",
                    "Install Counter-Strike 2 in Steam, or set the CS2 folder (the one with game/ and content/) in Settings."),

            compiler != null
                ? new DoctorCheck(DoctorCheckId.WorkshopTools, "Workshop Tools", CheckStatus.Ok, compiler, null)
                : new DoctorCheck(DoctorCheckId.WorkshopTools, "Workshop Tools", CheckStatus.Error,
                    cs2 == null ? "Needs Counter-Strike 2 first." : $"{CompilerPathFor(cs2)} does not exist.",
                    WorkshopToolsFix),

            steamRunning
                ? new DoctorCheck(DoctorCheckId.SteamRunning, "Steam running", CheckStatus.Ok, "Steam is running.", null)
                : new DoctorCheck(DoctorCheckId.SteamRunning, "Steam running", CheckStatus.Warn, "Steam is not running.",
                    "Start Steam and log in with the account that owns the Workshop items. Publishing needs it."),
        };

        var git = await gitTask.ConfigureAwait(false);
        checks.Add(git.Ok
            ? new DoctorCheck(DoctorCheckId.Git, "Git", CheckStatus.Ok, git.Text, null)
            : new DoctorCheck(DoctorCheckId.Git, "Git", CheckStatus.Error, git.Text,
                "Install Git for Windows from https://git-scm.com/download/win (it includes Git LFS)."));

        var lfs = await lfsTask.ConfigureAwait(false);
        checks.Add(lfs.Ok
            ? new DoctorCheck(DoctorCheckId.GitLfs, "Git LFS", CheckStatus.Ok, lfs.Text, null)
            : new DoctorCheck(DoctorCheckId.GitLfs, "Git LFS", CheckStatus.Warn,
                git.Ok ? "Git LFS is not installed. Big binaries would go into plain git." : "Needs Git first.",
                "Install Git LFS (Git for Windows includes it), then run: git lfs install. " + LfsDocFix));

        var gh = await ghTask.ConfigureAwait(false);
        var ghAuth = await ghAuthTask.ConfigureAwait(false);

        if (!gh.Ok)
        {
            checks.Add(new DoctorCheck(DoctorCheckId.Gh, "GitHub CLI", CheckStatus.Warn,
                "GitHub CLI (gh) is not installed. Only needed for \"Create private GitHub repo\" and GitHub releases.",
                "Install it from https://cli.github.com, then run: gh auth login. " + LfsDocFix));
        }
        else if (!ghAuth.Ok)
        {
            checks.Add(new DoctorCheck(DoctorCheckId.Gh, "GitHub CLI", CheckStatus.Warn,
                $"{gh.Text}. gh auth status says: {ghAuth.Text}",
                "Run: gh auth login (GitHub.com, HTTPS, log in with the browser), then Check again. " +
                "If you are logged in already, run gh auth status in a terminal and fix the account it marks with X. " + LfsDocFix));
        }
        else
        {
            checks.Add(new DoctorCheck(DoctorCheckId.Gh, "GitHub CLI", CheckStatus.Ok, $"{gh.Text}, logged in.", null));
        }

        return new DoctorReport(Os, CanCompile(), CanPublish(), cs2, checks);
    }

    public string? Cs2Path() => Lookup().Path;

    public string? CompilerPath()
    {
        var cs2 = Cs2Path();

        if (cs2 == null)
        {
            return null;
        }

        var compiler = CompilerPathFor(cs2);

        return File.Exists(compiler) ? compiler : null;
    }

    public string? GameInfoPath()
    {
        var cs2 = Cs2Path();

        if (cs2 == null)
        {
            return null;
        }

        var gameInfo = Path.Combine(cs2, "game", "csgo", "gameinfo.gi");

        return File.Exists(gameInfo) ? gameInfo : null;
    }

    public string RequireCs2Path()
    {
        var lookup = Lookup();

        return lookup.Path ?? throw new AddonStudioException(ErrorCodes.Cs2Missing,
            (lookup.Error ?? "Counter-Strike 2 was not found.") + " Install it in Steam or set the CS2 folder in Settings.");
    }

    public string RequireCompilerPath()
    {
        Platform.RequireWindows("Compiling");

        var cs2 = RequireCs2Path();
        var compiler = CompilerPathFor(cs2);

        if (!File.Exists(compiler))
        {
            throw new AddonStudioException(ErrorCodes.ToolsMissing,
                $"The CS2 Workshop Tools are not installed ({compiler} is missing). Install them: {WorkshopToolsFix}.");
        }

        return compiler;
    }

    public bool CanCompile() => Platform.IsWindows && CompilerPath() != null;

    public bool CanPublish() => Platform.IsWindows && Cs2Path() != null;

    public void Invalidate()
    {
        lock (gate)
        {
            cached = null;
        }
    }

    /// <summary>&lt;cs2&gt;/game/bin/win64/resourcecompiler.exe</summary>
    public static string CompilerPathFor(string cs2) => Path.Combine(cs2, "game", "bin", "win64", "resourcecompiler.exe");

    /// <summary>
    /// The CS2 folder (holding game/ and content/) for a folder the user picked: the folder itself, or its parent or
    /// grandparent when they picked game/ or game/csgo/. Null when none of them has a game/ folder.
    /// </summary>
    public static string? NormalizeCs2Folder(string picked)
    {
        string full;

        try
        {
            full = Paths.Full(picked);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // the folder itself, or game/ or game/csgo/ inside it
        var parent = Path.GetDirectoryName(full);
        var grandparent = parent == null ? null : Path.GetDirectoryName(parent);

        foreach (var candidate in new[] { full, parent, grandparent })
        {
            if (candidate != null && (Directory.Exists(Path.Combine(candidate, "game", "csgo")) || Directory.Exists(Path.Combine(candidate, "game", "bin"))))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Steam's install folder: the registry on Windows, the default folders elsewhere. Null when not found.</summary>
    public static string? SteamInstallPath()
    {
        var candidates = new List<string?>();

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(ReadRegistry(@"Software\Valve\Steam", "SteamPath", machine: false));
            candidates.Add(ReadRegistry(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", machine: true));
            candidates.Add(ReadRegistry(@"SOFTWARE\Valve\Steam", "InstallPath", machine: true));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (OperatingSystem.IsMacOS())
            {
                candidates.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
            }
            else
            {
                candidates.Add(Path.Combine(home, ".steam", "steam"));
                candidates.Add(Path.Combine(home, ".local", "share", "Steam"));
            }
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return Paths.Full(candidate);
            }
        }

        return null;
    }

    /// <summary>A Steam client process is running (steam.exe on Windows, steam_osx on macOS, steam on Linux).</summary>
    public static bool IsSteamRunning()
    {
        foreach (var name in new[] { "steam", "steam_osx", "steamwebhelper" })
        {
            Process[] processes;

            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                continue;
            }

            var found = processes.Length > 0;

            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private Cs2Lookup Lookup()
    {
        var overridePath = settings.Get().Cs2PathOverride;

        lock (gate)
        {
            if (cached != null && cached.Override == overridePath)
            {
                return cached;
            }
        }

        var lookup = Find(overridePath);

        lock (gate)
        {
            cached = lookup;
        }

        return lookup;
    }

    private static Cs2Lookup Find(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var normalized = NormalizeCs2Folder(overridePath);

            return normalized != null
                ? new Cs2Lookup(overridePath, normalized, null)
                : new Cs2Lookup(overridePath, null, $"The CS2 folder set in Settings ({overridePath}) has no game/ folder.");
        }

        try
        {
            var manager = WorkshopManager.FromSteamInstall();
            var path = manager.GamePath;

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return new Cs2Lookup(null, null, "Steam lists Counter-Strike 2 but its folder does not exist.");
            }

            return new Cs2Lookup(null, Paths.Full(path), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Debug($"CS2 lookup through Steam failed: {exception}");

            return new Cs2Lookup(null, null, exception.Message);
        }
    }

    private static async Task<ToolResult> ToolVersionAsync(string tool, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var executable = ProcessRunner.FindExecutable(tool);

        if (executable == null)
        {
            return new ToolResult(false, $"{tool} was not found in PATH.");
        }

        try
        {
            var result = await ProcessRunner.RunAsync(executable, arguments, new ProcessOptions { Timeout = ToolTimeout }, ct).ConfigureAwait(false);

            if (result.TimedOut)
            {
                return new ToolResult(false, $"{tool} {string.Join(' ', arguments)} did not answer in time.");
            }

            var text = FirstLine(result.Stdout);

            if (text.Length == 0)
            {
                text = FirstLine(result.Stderr);
            }

            return new ToolResult(result.ExitCode == 0, result.ExitCode == 0 ? text : (text.Length == 0 ? $"{tool} exited with {result.ExitCode}." : text));
        }
        catch (ProcessStartException exception)
        {
            return new ToolResult(false, exception.Message);
        }
    }

    /// <summary>
    /// Whether gh is logged in to github.com. Plain "gh auth status" exits 1 when ANY saved account or host has a
    /// problem (an old second account, an enterprise host), so only the active github.com account is asked about.
    /// It checks the token online, so it gets a longer timeout than the version checks.
    /// </summary>
    private static async Task<ToolResult> GhAuthAsync(CancellationToken ct)
    {
        var executable = ProcessRunner.FindExecutable("gh");

        if (executable == null)
        {
            return new ToolResult(false, "gh was not found in PATH.");
        }

        try
        {
            var options = new ProcessOptions { Timeout = TimeSpan.FromSeconds(30) };
            var result = await ProcessRunner.RunAsync(executable, ["auth", "status", "--active", "--hostname", "github.com"], options, ct).ConfigureAwait(false);

            // gh older than 2.40 has no --active
            if (!result.Succeeded && !result.TimedOut && (result.Stdout + result.Stderr).Contains("unknown flag", StringComparison.OrdinalIgnoreCase))
            {
                result = await ProcessRunner.RunAsync(executable, ["auth", "status", "--hostname", "github.com"], options, ct).ConfigureAwait(false);
            }

            if (result.TimedOut)
            {
                return new ToolResult(false, "no answer in 30 seconds (it checks the token online, is GitHub reachable?).");
            }

            return result.ExitCode == 0
                ? new ToolResult(true, "logged in")
                : new ToolResult(false, GhAuthReason(result.Stdout + "\n" + result.Stderr));
        }
        catch (ProcessStartException exception)
        {
            return new ToolResult(false, exception.Message);
        }
    }

    /// <summary>The lines of gh auth status output that say what is wrong, with anything token-like masked.</summary>
    internal static string GhAuthReason(string output)
    {
        var lines = output.Split('\n')
            .Select(line => line.Trim().TrimStart('X', 'x', '!', '-', '✓', '×', ' ').Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var problems = lines.Where(line =>
                line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("not logged", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        var text = problems.Count > 0 ? string.Join(" ", problems) : lines.LastOrDefault() ?? "it exited with an error and printed nothing.";

        return System.Text.RegularExpressions.Regex.Replace(text, @"\b(gh[opsu]|github_pat)_[A-Za-z0-9_]{6,}", "$1_***");
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return "";
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistry(string key, string value, bool machine)
    {
        try
        {
            var root = machine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
            using var subKey = root.OpenSubKey(key);

            return subKey?.GetValue(value) is string text && text.Length > 0 ? text.Replace('/', '\\') : null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private sealed record Cs2Lookup(string? Override, string? Path, string? Error);

    private sealed record ToolResult(bool Ok, string Text);
}
