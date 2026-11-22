using System.Net;
using System.Text;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Steam;

namespace AddonStudio.Tests.Steam;

public sealed class SteamServiceTests
{
    private const string Details = """
        {"response":{"result":1,"resultcount":1,"publishedfiledetails":[{"publishedfileid":"123","result":1,"creator":"7656","title":"Public title",
        "file_size":"2048","time_updated":1700000000,"visibility":0,"banned":1,"ban_reason":"Incompatible content","valve_check":"x"}]}}
        """;

    [Fact]
    public async Task Status_merges_owner_item_with_public_details()
    {
        var handler = new CannedHandler(Details);
        var updated = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var owned = new SteamItem("123", "Owner title", Visibility.Private, updated.AddDays(-5), updated.AddMinutes(-10), 1000, 3, 1, 9, null);
        var steam = new SteamService(null!, new HttpClient(handler), _ => Task.FromResult<IReadOnlyList<SteamItem>>([owned]));

        var live = await steam.StatusAsync("123", updated.AddMinutes(1));

        Assert.True(live.Found);
        Assert.Equal("Owner title", live.Title);
        Assert.Equal(Visibility.Private, live.Visibility);
        Assert.Equal(updated, live.TimeUpdated);
        Assert.Equal(2048, live.Size);
        Assert.True(live.Banned);
        Assert.Equal("Incompatible content", live.BanReason);
        Assert.Equal(LiveState.Live, live.Live);
        Assert.Equal("x", live.Raw?["valve_check"]?.ToString());
        Assert.Contains("publishedfileids%5B0%5D=123", handler.LastBody);
        Assert.Contains("itemcount=1", handler.LastBody);

        var processing = await steam.StatusAsync("123", updated.AddHours(1));
        Assert.Equal(LiveState.Processing, processing.Live);
    }

    [Fact]
    public async Task Status_of_an_item_steam_does_not_show()
    {
        var handler = new CannedHandler("""{"response":{"result":1,"resultcount":1,"publishedfiledetails":[{"publishedfileid":"55","result":9}]}}""");
        var steam = new SteamService(null!, new HttpClient(handler), _ => throw new AddonStudioException(ErrorCodes.SteamUnavailable, "no steam"));

        var status = await steam.StatusAsync("55", null);

        Assert.False(status.Found);
        Assert.Equal(LiveState.Unknown, status.Live);
        Assert.False(status.Banned);
        Assert.NotNull(status.Raw);

        var none = await steam.StatusAsync(null, null);
        Assert.Equal((false, LiveState.Unknown, (string?)null), (none.Found, none.Live, none.WorkshopId));
    }

    private sealed class CannedHandler(string json) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(SteamService.DetailsUrl, request.RequestUri?.ToString());
            LastBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
