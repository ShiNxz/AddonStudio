using System.Text.Json.Serialization;

namespace AddonStudio.Core.Contracts;

/// <summary>app.info result.</summary>
public sealed record AppInfo(string Version, Os Os, bool CanCompile, bool CanPublish);

/// <summary>One doctor check. Fix is the text that tells the user how to fix it, null when there is nothing to fix.</summary>
public sealed record DoctorCheck(DoctorCheckId Id, string Label, CheckStatus Status, string Detail, string? Fix);

/// <summary>env.doctor result.</summary>
public sealed record DoctorReport(Os Os, bool CanCompile, bool CanPublish, string? Cs2Path, IReadOnlyList<DoctorCheck> Checks);

/// <summary>An entry of the recent projects list. Exists is worked out when the list is read.</summary>
public sealed record RecentProject(string Path, string Addon, string Title, DateTimeOffset LastOpened, bool Exists);

/// <summary>
/// App settings, stored in settings.json (see ISettingsStore). Missing keys in the file get these defaults.
/// </summary>
public sealed record AppSettings
{
    public const string DefaultCompilerArgs = "-nop4 -i \"{input}\"";
    public const int MaxRecentProjects = 50;

    /// <summary>3 GiB, Valve's own tool limit (SPEC 5).</summary>
    public const long DefaultMaxPackBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>The smallest pack size cap settings.set takes (1 MiB).</summary>
    public const long MinMaxPackBytes = 1024 * 1024;

    /// <summary>max(1, cores / 2)</summary>
    public static int DefaultCompileParallelism => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>CS2 install folder (the one holding game/ and content/), null = find it through Steam.</summary>
    public string? Cs2PathOverride { get; init; }

    public int CompileParallelism { get; init; } = DefaultCompileParallelism;

    /// <summary>resourcecompiler.exe arguments, "{input}" is replaced with the file to compile.</summary>
    public string CompilerArgs { get; init; } = DefaultCompilerArgs;

    /// <summary>Pack size cap in bytes (pack.contents limitBytes, export and publish check it).</summary>
    public long MaxPackBytes { get; init; } = DefaultMaxPackBytes;

    public Theme Theme { get; init; } = Theme.Dark;

    /// <summary>Newest first, at most MaxRecentProjects.</summary>
    public IReadOnlyList<RecentProject> RecentProjects { get; init; } = [];
}

/// <summary>
/// settings.set params: Partial&lt;AppSettings&gt; without recentProjects. A property left out of the JSON is left as it is.
/// cs2PathOverride can be sent as null to clear it, so HasCs2PathOverride tells "sent as null" apart from "not sent".
/// </summary>
public sealed class AppSettingsPatch
{
    private string? cs2PathOverride;

    public string? Cs2PathOverride
    {
        get => cs2PathOverride;
        set
        {
            cs2PathOverride = value;
            HasCs2PathOverride = true;
        }
    }

    /// <summary>True when the patch sets cs2PathOverride (to a path or to null).</summary>
    [JsonIgnore]
    public bool HasCs2PathOverride { get; private set; }

    public int? CompileParallelism { get; set; }

    public string? CompilerArgs { get; set; }

    public long? MaxPackBytes { get; set; }

    public Theme? Theme { get; set; }

    /// <summary>The settings with this patch applied. RecentProjects are kept.</summary>
    public AppSettings ApplyTo(AppSettings settings)
    {
        return settings with
        {
            Cs2PathOverride = HasCs2PathOverride ? (string.IsNullOrWhiteSpace(cs2PathOverride) ? null : cs2PathOverride) : settings.Cs2PathOverride,
            CompileParallelism = CompileParallelism ?? settings.CompileParallelism,
            CompilerArgs = CompilerArgs ?? settings.CompilerArgs,
            MaxPackBytes = MaxPackBytes ?? settings.MaxPackBytes,
            Theme = Theme ?? settings.Theme,
        };
    }
}
