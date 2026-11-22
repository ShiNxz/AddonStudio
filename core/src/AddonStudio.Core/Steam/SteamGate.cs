using System.Diagnostics;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Steam;

/// <summary>
/// One Steam call at a time. SteamConnection is one pipe and every wait drains all pending callbacks, so two calls at
/// once would eat each other's results. Items, status and uploads all go through here.
/// </summary>
internal static class SteamGate
{
    public static SemaphoreSlim Lock { get; } = new(1, 1);

    /// <summary>A steam.exe process is running (Windows only, false elsewhere).</summary>
    public static bool IsSteamRunning()
    {
        if (!Platform.IsWindows)
        {
            return false;
        }

        var processes = Process.GetProcessesByName("steam");

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Windows and a running Steam client, else steam_unavailable (not_windows off Windows when asked).</summary>
    public static void RequireSteam(string what, bool notWindowsError)
    {
        if (!Platform.IsWindows)
        {
            if (notWindowsError)
            {
                throw AddonStudioException.NotWindows(what);
            }

            throw new AddonStudioException(ErrorCodes.SteamUnavailable, $"{what} needs the Steam client, which only runs on Windows here.");
        }

        if (!IsSteamRunning())
        {
            throw new AddonStudioException(ErrorCodes.SteamUnavailable, "Steam is not running. Start Steam and log in, then try again.");
        }
    }

    /// <summary>Steam's own exceptions as our error codes, null for anything else.</summary>
    public static AddonStudioException? Map(Exception exception)
    {
        return exception switch
        {
            AddonStudioException known => known,
            Steamworks.SteamUnavailableException unavailable => new AddonStudioException(ErrorCodes.SteamUnavailable, unavailable.Message, null, unavailable),
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException =>
                new AddonStudioException(ErrorCodes.SteamUnavailable, $"Steam's client library could not be used: {exception.Message}", null, exception),
            _ => null,
        };
    }
}
