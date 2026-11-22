namespace AddonStudio.Core.Contracts;

/// <summary>Workshop items and their status on Steam (SPEC 5 and 6.7).</summary>
public interface ISteamService
{
    /// <summary>steam.items: the account's CS2 Workshop items through the Steam client. steam_unavailable without a running Steam client.</summary>
    Task<IReadOnlyList<SteamItem>> ItemsAsync(CancellationToken ct = default);

    /// <summary>Status of one item: owner items plus the public GetPublishedFileDetails call. Live is worked out against lastPublishAt.</summary>
    Task<SteamItemStatus> StatusAsync(string? workshopId, DateTimeOffset? lastPublishAt, CancellationToken ct = default);

    /// <summary>steam.status { path }: StatusAsync with the project's workshopId and last-publish.json time.</summary>
    Task<SteamItemStatus> ProjectStatusAsync(string path, CancellationToken ct = default);
}
