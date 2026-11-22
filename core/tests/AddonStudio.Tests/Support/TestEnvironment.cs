using System.Runtime.CompilerServices;

namespace AddonStudio.Tests.Support;

internal static class TestEnvironment
{
    /// <summary>Settings never touch the real %APPDATA%/AddonStudio while tests run.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        if (Environment.GetEnvironmentVariable("ADDONSTUDIO_HOME") == null)
        {
            var home = Path.Combine(Path.GetTempPath(), "addonstudio-tests", "home-" + Environment.ProcessId);
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("ADDONSTUDIO_HOME", home);
        }
    }
}
