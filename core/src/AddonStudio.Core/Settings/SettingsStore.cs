using System.Text.Json;
using System.Text.Json.Nodes;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Settings;

/// <summary>
/// settings.json (SPEC 6.2): app settings and the recent projects list. Cached in memory, every change is written
/// atomically (temp file + replace). Keys this version does not know are kept on save. Thread-safe.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    public const string HomeEnvVar = "ADDONSTUDIO_HOME";
    public const string FileName = "settings.json";

    private readonly object gate = new();
    private AppSettings? current;
    private JsonObject? raw;

    /// <param name="homeDir">The folder settings.json lives in, null = $ADDONSTUDIO_HOME, else the OS default.</param>
    public SettingsStore(string? homeDir = null)
    {
        HomeDir = Paths.Full(homeDir ?? DefaultHomeDir());
        SettingsPath = Path.Combine(HomeDir, FileName);
    }

    public string HomeDir { get; }

    public string SettingsPath { get; }

    /// <summary>$ADDONSTUDIO_HOME, else %APPDATA%/AddonStudio on Windows, ~/.config/AddonStudio elsewhere.</summary>
    public static string DefaultHomeDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable(HomeEnvVar);

        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir.Trim();
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (!string.IsNullOrEmpty(appData))
            {
                return Path.Combine(appData, "AddonStudio");
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(string.IsNullOrEmpty(home) ? Path.GetTempPath() : home, ".config", "AddonStudio");
    }

    public AppSettings Get()
    {
        lock (gate)
        {
            return WithExists(Load());
        }
    }

    public AppSettings Set(AppSettingsPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (patch.CompileParallelism is < 1 or > 256)
        {
            throw AddonStudioException.InvalidParams("compileParallelism must be between 1 and 256.");
        }

        if (patch.CompilerArgs != null && !patch.CompilerArgs.Contains("{input}", StringComparison.Ordinal))
        {
            throw AddonStudioException.InvalidParams("compilerArgs must contain {input} (the file to compile).");
        }

        if (patch.MaxPackBytes is { } maxPackBytes && maxPackBytes < AppSettings.MinMaxPackBytes)
        {
            throw AddonStudioException.InvalidParams("maxPackBytes must be at least 1 MiB.");
        }

        lock (gate)
        {
            var updated = patch.ApplyTo(Load());
            Save(updated);

            return WithExists(updated);
        }
    }

    public IReadOnlyList<RecentProject> TouchRecent(string path, string addon, string title)
    {
        var full = Paths.Full(path);

        lock (gate)
        {
            var settings = Load();
            var list = new List<RecentProject> { new(full, addon, title, DateTimeOffset.UtcNow, true) };
            list.AddRange(settings.RecentProjects.Where(project => !Paths.SamePath(project.Path, full)));

            var updated = settings with { RecentProjects = list.Take(AppSettings.MaxRecentProjects).ToList() };
            Save(updated);

            return WithExists(updated).RecentProjects;
        }
    }

    public IReadOnlyList<RecentProject> RemoveRecent(string path)
    {
        var full = Paths.Full(path);

        lock (gate)
        {
            var settings = Load();
            var updated = settings with { RecentProjects = settings.RecentProjects.Where(project => !Paths.SamePath(project.Path, full)).ToList() };

            if (updated.RecentProjects.Count != settings.RecentProjects.Count)
            {
                Save(updated);
            }

            return WithExists(updated).RecentProjects;
        }
    }

    private AppSettings Load()
    {
        if (current != null)
        {
            return current;
        }

        raw = null;
        AppSettings? loaded = null;

        try
        {
            if (File.Exists(SettingsPath))
            {
                var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
                raw = JsonNode.Parse(File.ReadAllText(SettingsPath), documentOptions: options) as JsonObject;
                loaded = raw?.Deserialize<AppSettings>(Json.Options);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            Log.Warn($"Could not read {SettingsPath}, using defaults: {exception.Message}");
            raw = null;
            loaded = null;
        }

        current = Sanitize(loaded ?? new AppSettings());

        return current;
    }

    private void Save(AppSettings settings)
    {
        var node = (JsonObject)Json.ToNode(settings)!;

        // "exists" is worked out on every read, never stored
        if (node["recentProjects"] is JsonArray projects)
        {
            foreach (var project in projects.OfType<JsonObject>())
            {
                project.Remove("exists");
            }
        }

        var merged = Json.Merge(raw?.DeepClone() as JsonObject ?? new JsonObject(), node);
        Json.WriteNode(SettingsPath, merged);

        raw = merged;
        current = settings;
    }

    /// <summary>Repairs what a hand edited file can break (nulls, out of range numbers, duplicate projects).</summary>
    private static AppSettings Sanitize(AppSettings settings)
    {
        var recent = new List<RecentProject>();

        foreach (var project in settings.RecentProjects ?? [])
        {
            if (project == null || string.IsNullOrWhiteSpace(project.Path) || recent.Any(seen => Paths.SamePath(seen.Path, project.Path)))
            {
                continue;
            }

            recent.Add(project with { Addon = project.Addon ?? "", Title = project.Title ?? "" });
        }

        return settings with
        {
            Cs2PathOverride = string.IsNullOrWhiteSpace(settings.Cs2PathOverride) ? null : settings.Cs2PathOverride,
            CompileParallelism = settings.CompileParallelism < 1 ? AppSettings.DefaultCompileParallelism : settings.CompileParallelism,
            CompilerArgs = string.IsNullOrWhiteSpace(settings.CompilerArgs) ? AppSettings.DefaultCompilerArgs : settings.CompilerArgs,
            MaxPackBytes = settings.MaxPackBytes < AppSettings.MinMaxPackBytes ? AppSettings.DefaultMaxPackBytes : settings.MaxPackBytes,
            RecentProjects = recent.Take(AppSettings.MaxRecentProjects).ToList(),
        };
    }

    private static AppSettings WithExists(AppSettings settings)
    {
        return settings with
        {
            RecentProjects = settings.RecentProjects
                .Select(project => project with { Exists = File.Exists(Path.Combine(project.Path, WorkshopConfig.FileName)) })
                .ToList(),
        };
    }
}
