using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;
using Steamworks;

namespace AddonStudio.Core.Steam;

/// <summary>
/// Workshop items of the logged in account (a UGC query over our own SteamConnection) and the
/// public GetPublishedFileDetails web API (SPEC 5 "Steam status").
/// </summary>
public sealed class SteamService : ISteamService, IDisposable
{
    public const string DetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    /// <summary>
    /// Steam sets time_updated when it commits the update, a little before our upload call returns (and the PC clock
    /// can be off a bit), so an update counts as live within this much of our last publish.
    /// </summary>
    internal static readonly TimeSpan LiveTolerance = TimeSpan.FromMinutes(5);

    /// <summary>How long steam.items waits for an upload or another Steam call to finish.</summary>
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(3);

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly IProjectService projects;
    private readonly HttpClient http;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SteamItem>>>? ownerItems;

    public SteamService(IProjectService projects)
        : this(projects, SharedHttp, null)
    {
    }

    /// <param name="ownerItems">Replaces the Steam client query (tests), null = the real one.</param>
    internal SteamService(IProjectService projects, HttpClient http, Func<CancellationToken, Task<IReadOnlyList<SteamItem>>>? ownerItems)
    {
        this.projects = projects;
        this.http = http;
        this.ownerItems = ownerItems;
    }

    public async Task<IReadOnlyList<SteamItem>> ItemsAsync(CancellationToken ct = default)
    {
        if (ownerItems != null)
        {
            return await ownerItems(ct).ConfigureAwait(false);
        }

        SteamGate.RequireSteam("Listing Workshop items", notWindowsError: false);

        if (!await SteamGate.Lock.WaitAsync(GateWait, ct).ConfigureAwait(false))
        {
            throw AddonStudioException.Busy("Steam is busy with an upload, try again when it is done.");
        }

        try
        {
            return await QueryOwnItemsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SteamGate.Map(exception) is { } mapped && exception is not OperationCanceledException)
        {
            SteamConnection.Reset();
            throw mapped;
        }
        catch (IOException exception)
        {
            SteamConnection.Reset();
            throw new AddonStudioException(ErrorCodes.SteamUnavailable, exception.Message, null, exception);
        }
        catch (InvalidOperationException exception)
        {
            // "Workshop query failed: ..." and friends
            throw new AddonStudioException(ErrorCodes.SteamUnavailable, exception.Message, null, exception);
        }
        finally
        {
            SteamGate.Lock.Release();
        }
    }

    public async Task<SteamItemStatus> StatusAsync(string? workshopId, DateTimeOffset? lastPublishAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workshopId))
        {
            return new SteamItemStatus(null, false, null, null, null, null, false, null, LiveState.Unknown, lastPublishAt, null);
        }

        var id = workshopId.Trim();

        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw AddonStudioException.InvalidParams($"\"{workshopId}\" is not a Workshop id.");
        }

        SteamItem? owned = null;

        try
        {
            owned = (await ItemsAsync(ct).ConfigureAwait(false)).FirstOrDefault(item => item.WorkshopId == id);
        }
        catch (AddonStudioException exception)
        {
            // no Steam client (or busy): the public API still answers for public items
            Log.Debug($"Owner items unavailable for status of {id}: {exception.Message}");
        }

        JsonObject? raw = null;
        Exception? publicError = null;

        try
        {
            raw = await FetchDetailsAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
                                              || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            publicError = exception;
        }

        if (owned == null && raw == null && publicError != null)
        {
            throw new AddonStudioException(ErrorCodes.SteamUnavailable, $"Could not reach Steam: {publicError.Message}", null, publicError);
        }

        return Merge(id, owned, raw, lastPublishAt);
    }

    public async Task<SteamItemStatus> ProjectStatusAsync(string path, CancellationToken ct = default)
    {
        var config = projects.LoadConfig(path);
        var last = LastPublish.Read(path);
        DateTimeOffset? lastAt = last != null && config.WorkshopId != null && last.WorkshopId == config.WorkshopId ? last.At : null;

        return await StatusAsync(config.WorkshopId, lastAt, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (SteamConnection.IsOpen)
        {
            SteamConnection.Reset();
        }
    }

    /// <summary>
    /// Every item the logged in account published for CS2, newest first. The same query as
    /// WorkshopManager.GetPublishedItemsAsync but without its required "CS2" and "Map" tags, so addons published with
    /// other tags show up too. Runs on our own SteamConnection (the caller holds SteamGate.Lock).
    /// </summary>
    private static async Task<IReadOnlyList<SteamItem>> QueryOwnItemsAsync(CancellationToken ct)
    {
        var client = SteamConnection.Get();
        var ugc = client.UGC;
        var accountId = client.User.GetAccountId();
        var items = new List<SteamItem>();

        for (var page = 1u; ; page++)
        {
            ct.ThrowIfCancellationRequested();

            var query = ugc.CreateQueryUserUGCRequest(accountId, EUserUGCList.Published, EUGCMatchingUGCType.Items, EUserUGCListSortOrder.CreationOrderDesc,
                SteamConnection.AppId, SteamConnection.AppId, page);

            if (query == SteamUGC.InvalidQueryHandle)
            {
                throw new InvalidOperationException("Failed to create a workshop query.");
            }

            try
            {
                var completed = await client.WaitForCallResultAsync<SteamUGCQueryCompleted>(ugc.SendQueryUGCRequest(query)).ConfigureAwait(false);

                if (completed.Result != EResult.OK)
                {
                    throw new InvalidOperationException($"Workshop query failed: {completed.Result}");
                }

                for (var index = 0u; index < completed.NumResultsReturned; index++)
                {
                    if (ugc.GetQueryUGCResult(query, index) is not SteamUGCDetails details)
                    {
                        continue;
                    }

                    items.Add(new SteamItem(
                        details.PublishedFileId.ToString(CultureInfo.InvariantCulture),
                        details.Title,
                        ToVisibility((long)details.Visibility) ?? Visibility.Private,
                        DateTimeOffset.FromUnixTimeSeconds(details.TimeCreated),
                        DateTimeOffset.FromUnixTimeSeconds(details.TimeUpdated),
                        (long)Math.Min(details.TotalFilesSize, long.MaxValue),
                        Clamp(ugc.GetQueryUGCStatistic(query, index, EItemStatistic.NumSubscriptions)),
                        Clamp(ugc.GetQueryUGCStatistic(query, index, EItemStatistic.NumFavorites)),
                        Clamp(ugc.GetQueryUGCStatistic(query, index, EItemStatistic.NumUniqueWebsiteViews)),
                        ugc.GetQueryUGCPreviewURL(query, index)?.ToString()));
                }

                if (completed.NumResultsReturned < SteamUGC.ResultsPerPage || items.Count >= completed.TotalMatchingResults)
                {
                    return items;
                }
            }
            finally
            {
                ugc.ReleaseQueryUGCRequest(query);
            }
        }
    }

    private static long Clamp(ulong? value) => (long)Math.Min(value ?? 0, long.MaxValue);

    /// <summary>publishedfiledetails[0] of the public API, null when Steam sent none.</summary>
    private async Task<JsonObject?> FetchDetailsAsync(string id, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("itemcount", "1"),
            new KeyValuePair<string, string>("publishedfileids[0]", id),
        ]);

        using var response = await http.PostAsync(DetailsUrl, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseDetails(text);
    }

    /// <summary>The first publishedfiledetails entry of a GetPublishedFileDetails response, detached.</summary>
    internal static JsonObject? ParseDetails(string json)
    {
        var root = JsonNode.Parse(json);

        if (root?["response"]?["publishedfiledetails"] is JsonArray { Count: > 0 } list && list[0] is JsonObject first)
        {
            return first.DeepClone().AsObject();
        }

        return null;
    }

    /// <summary>Owner item and public details merged into a status, live worked out against lastPublishAt.</summary>
    internal static SteamItemStatus Merge(string id, SteamItem? owned, JsonObject? raw, DateTimeOffset? lastPublishAt)
    {
        var publicFound = raw != null && ReadLong(raw["result"]) == 1;
        var found = owned != null || publicFound;

        var title = owned?.Title ?? (publicFound ? ReadString(raw!["title"]) : null);
        var visibility = owned?.Visibility ?? (publicFound ? ToVisibility(ReadLong(raw!["visibility"])) : null);

        DateTimeOffset? timeUpdated = owned?.TimeUpdated;

        if (publicFound && ReadLong(raw!["time_updated"]) is { } seconds and > 0)
        {
            var fromApi = DateTimeOffset.FromUnixTimeSeconds(seconds);
            timeUpdated = timeUpdated == null || fromApi > timeUpdated ? fromApi : timeUpdated;
        }

        var size = publicFound && ReadLong(raw!["file_size"]) is { } fileSize ? fileSize : owned?.Size;
        var banned = publicFound && (ReadLong(raw!["banned"]) ?? 0) != 0;
        var banReason = publicFound && ReadString(raw!["ban_reason"]) is { Length: > 0 } reason ? reason : null;

        var live = timeUpdated == null
            ? LiveState.Unknown
            : lastPublishAt == null || timeUpdated.Value >= lastPublishAt.Value - LiveTolerance
                ? LiveState.Live
                : LiveState.Processing;

        return new SteamItemStatus(id, found, title, visibility, timeUpdated, size, banned, banReason, live, lastPublishAt, raw);
    }

    /// <summary>ERemoteStoragePublishedFileVisibility: 0 public, 1 friends only, 2 private, 3 unlisted.</summary>
    private static Visibility? ToVisibility(long? value) => value switch
    {
        0 => Visibility.Public,
        1 => Visibility.FriendsOnly,
        2 => Visibility.Private,
        3 => Visibility.Unlisted,
        _ => null,
    };

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>A number Steam sends as a number, a string ("file_size": "123") or a bool.</summary>
    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<double>(out var real))
        {
            return (long)real;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag ? 1 : 0;
        }

        if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
