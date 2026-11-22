using Steamworks;

namespace AddonStudio.Core.Steam;

/// <summary>
/// Our one pipe to the running Steam client as Counter-Strike 2 (SPEC 5: Steamworks.SteamClient(730) from the
/// Steamworks.dll inside CS2WorkshopManager). WorkshopManager's own static client is never opened, so there is only
/// one pipe and one callback pump in the process. Callers hold SteamGate.Lock.
/// </summary>
internal static class SteamConnection
{
    public const uint AppId = 730;

    private static SteamClient? client;

    public static bool IsOpen => client != null;

    /// <summary>The connection, opened on first use. Throws SteamUnavailableException when Steam is not up.</summary>
    public static SteamClient Get()
    {
        return client ??= new SteamClient(AppId);
    }

    /// <summary>Closes the pipe, the next Get opens a new one (after Steam restarted, or a failed call).</summary>
    public static void Reset()
    {
        var current = client;
        client = null;

        try
        {
            current?.Dispose();
        }
        catch (Exception exception)
        {
            Util.Log.Warn($"Closing the Steam connection failed: {exception.Message}");
        }
    }
}
