using System.Globalization;

namespace AddonStudio.Core.Util;

/// <summary>
/// Logging for the core. Always stderr (stdout carries only RPC messages). Thread-safe.
/// Set ADDONSTUDIO_DEBUG=1 to also see Debug lines.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Where lines go, stderr by default. The host swaps in a UTF-8 writer; tests can redirect it.</summary>
    public static TextWriter Writer { get; set; } = Console.Error;

    public static bool DebugEnabled { get; set; } = Environment.GetEnvironmentVariable("ADDONSTUDIO_DEBUG") is "1" or "true";

    public static void Debug(string message)
    {
        if (DebugEnabled)
        {
            Write("debug", message);
        }
    }

    public static void Info(string message) => Write("info", message);

    public static void Warn(string message) => Write("warn", message);

    public static void Error(string message, Exception? exception = null)
    {
        Write("error", exception == null ? message : $"{message}: {exception}");
    }

    private static void Write(string level, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level} {message}");

        lock (Gate)
        {
            try
            {
                Writer.WriteLine(line);
                Writer.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }
    }
}
