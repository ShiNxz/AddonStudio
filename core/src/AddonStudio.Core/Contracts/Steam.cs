using System.Text.Json.Nodes;

namespace AddonStudio.Core.Contracts;

/// <summary>An item the logged in Steam account owns (steam.items).</summary>
public sealed record SteamItem(
    string WorkshopId,
    string Title,
    Visibility Visibility,
    DateTimeOffset TimeCreated,
    DateTimeOffset TimeUpdated,
    long Size,
    long Subscribers,
    long Favorites,
    long Views,
    string? PreviewUrl);

/// <summary>
/// steam.status result. Live: "live" when Steam's time_updated is at or after our last publish, "processing" when older,
/// "unknown" without data. Raw = the GetPublishedFileDetails publishedfiledetails[0] object as Steam sent it.
/// </summary>
public sealed record SteamItemStatus(
    string? WorkshopId,
    bool Found,
    string? Title,
    Visibility? Visibility,
    DateTimeOffset? TimeUpdated,
    long? Size,
    bool Banned,
    string? BanReason,
    LiveState Live,
    DateTimeOffset? LastPublishAt,
    JsonObject? Raw);
