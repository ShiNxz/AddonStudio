using System.Reflection;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Host;

/// <summary>What app.info reports.</summary>
public static class HostInfo
{
    /// <summary>The core's version (Directory.Build.props Version), without build metadata.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// app.info. canCompile/canPublish come from the environment service; if it fails they are false (logged),
    /// so the app can always start.
    /// </summary>
    public static AppInfo Create(IEnvironmentService env)
    {
        return new AppInfo(Version, Platform.Os, Safe(env.CanCompile, "canCompile"), Safe(env.CanPublish, "canPublish"));
    }

    private static bool Safe(Func<bool> check, string name)
    {
        try
        {
            return check();
        }
        catch (Exception exception)
        {
            Log.Warn($"app.info {name} failed: {exception.Message}");
            return false;
        }
    }

    private static string ReadVersion()
    {
        var assembly = typeof(HostInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
