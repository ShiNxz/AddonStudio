using AddonStudio.Core.Contracts;

namespace AddonStudio.Core.Util;

/// <summary>The OS the core runs on.</summary>
public static class Platform
{
    public static Os Os { get; } =
        OperatingSystem.IsWindows() ? Os.Windows : OperatingSystem.IsMacOS() ? Os.Macos : Os.Linux;

    public static bool IsWindows => Os == Os.Windows;

    /// <summary>Throws not_windows naming what needs Windows ("Compiling", "Publishing", ...).</summary>
    public static void RequireWindows(string what)
    {
        if (!IsWindows)
        {
            throw AddonStudioException.NotWindows(what);
        }
    }
}
