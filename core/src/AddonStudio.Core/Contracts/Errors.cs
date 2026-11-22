namespace AddonStudio.Core.Contracts;

/// <summary>RPC error codes (SPEC section 7).</summary>
public static class ErrorCodes
{
    public const string InvalidParams = "invalid_params";
    public const string NotFound = "not_found";
    public const string NotWindows = "not_windows";
    public const string Cs2Missing = "cs2_missing";
    public const string ToolsMissing = "tools_missing";
    public const string SteamUnavailable = "steam_unavailable";
    public const string GitFailed = "git_failed";
    public const string Conflict = "conflict";
    public const string Busy = "busy";
    public const string Cancelled = "cancelled";
    public const string Internal = "internal";

    public static readonly IReadOnlyList<string> All =
    [
        InvalidParams, NotFound, NotWindows, Cs2Missing, ToolsMissing, SteamUnavailable,
        GitFailed, Conflict, Busy, Cancelled, Internal,
    ];
}

/// <summary>
/// An error the RPC host sends as { code, message, details }. Details is serialized with Json.Options (null when none).
/// Anything else a service throws becomes "internal" (OperationCanceledException becomes "cancelled").
/// </summary>
public class AddonStudioException : Exception
{
    public string Code { get; }

    public object? Details { get; }

    public AddonStudioException(string code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public AddonStudioException(string code, string message, object? details, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    public static AddonStudioException InvalidParams(string message, object? details = null) => new(ErrorCodes.InvalidParams, message, details);

    public static AddonStudioException NotFound(string message, object? details = null) => new(ErrorCodes.NotFound, message, details);

    public static AddonStudioException NotWindows(string what) => new(ErrorCodes.NotWindows, $"{what} only works on Windows.");

    public static AddonStudioException Busy(string message) => new(ErrorCodes.Busy, message);

    public static AddonStudioException GitFailed(string message, object? details = null) => new(ErrorCodes.GitFailed, message, details);

    public static AddonStudioException Internal(string message, object? details = null) => new(ErrorCodes.Internal, message, details);
}
