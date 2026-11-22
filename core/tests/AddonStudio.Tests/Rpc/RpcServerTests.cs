using System.Text.Json;
using System.Text.Json.Nodes;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;
using AddonStudio.Host;
using AddonStudio.Host.Rpc;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Rpc;

public sealed class RpcServerTests
{
    private static JsonObject Parse(string line) => JsonNode.Parse(line)!.AsObject();

    [Fact]
    public async Task AppInfoRoundTrip()
    {
        using var services = TestServices.Create(new FakeEnvironment { Compile = true, Publish = false });
        var output = new LineWriter();
        using var server = new RpcServer(services, output);

        await server.HandleLineAsync("""{"id": 7, "method": "app.info"}""");

        var response = Parse(await output.NextAsync());
        Assert.Equal(7, response["id"]!.GetValue<int>());
        Assert.False(response.ContainsKey("error"));

        var result = response["result"]!.AsObject();
        Assert.Equal(HostInfo.Version, result["version"]!.GetValue<string>());
        Assert.Equal(Platform.Os switch { Os.Windows => "windows", Os.Macos => "macos", _ => "linux" }, result["os"]!.GetValue<string>());
        Assert.True(result["canCompile"]!.GetValue<bool>());
        Assert.False(result["canPublish"]!.GetValue<bool>());
        Assert.Equal(["version", "os", "canCompile", "canPublish"], result.Select(property => property.Key));
    }

    [Fact]
    public async Task AppInfoWorksEvenWhenTheEnvironmentThrows()
    {
        // the real EnvironmentService before it is implemented, or one that fails: app.info still answers
        using var services = TestServices.Create(new AddonStudio.Core.Env.EnvironmentService(new AddonStudio.Core.Settings.SettingsStore()));
        var output = new LineWriter();
        using var server = new RpcServer(services, output);

        await server.HandleLineAsync("""{"id": 1, "method": "app.info"}""");

        var result = Parse(await output.NextAsync())["result"]!.AsObject();
        Assert.Equal(HostInfo.Version, result["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownMethodIsInvalidParams()
    {
        using var services = TestServices.Create();
        var output = new LineWriter();
        using var server = new RpcServer(services, output);

        await server.HandleLineAsync("""{"id": 8, "method": "nope.nothing", "params": {}}""");

        var response = Parse(await output.NextAsync());
        Assert.Equal(8, response["id"]!.GetValue<int>());
        Assert.False(response.ContainsKey("result"));

        var error = response["error"]!.AsObject();
        Assert.Equal(ErrorCodes.InvalidParams, error["code"]!.GetValue<string>());
        Assert.Contains("nope.nothing", error["message"]!.GetValue<string>());
        Assert.True(error.ContainsKey("details"));
        Assert.Null(error["details"]);
    }

    [Fact]
    public async Task MissingParamsAreInvalidParams()
    {
        using var services = TestServices.Create();
        var output = new LineWriter();
        using var server = new RpcServer(services, output);

        await server.HandleLineAsync("""{"id": 1, "method": "projects.open"}""");
        await server.HandleLineAsync("""{"id": 2, "method": "projects.open", "params": {}}""");
        await server.HandleLineAsync("""{"id": 3, "method": "build.run", "params": {"path": "x", "mode": "sideways"}}""");
        await server.HandleLineAsync("""{"id": 4, "method": "build.run", "params": {"path": "x", "mode": "files"}}""");
        await server.HandleLineAsync("""{"id": 5, "method": "git.log", "params": {"path": "x", "limit": 0, "skip": 0}}""");

        for (var i = 0; i < 5; i++)
        {
            var response = Parse(await output.NextAsync());
            Assert.Equal(ErrorCodes.InvalidParams, response["error"]!["code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ServiceErrorsKeepTheirCode()
    {
        using var services = TestServices.Create(build: new WaitingBuild());
        var output = new LineWriter();
        using var server = new RpcServer(services, output);

        // WaitingBuild.PlanAsync throws not_found
        await server.HandleLineAsync("""{"id": "a", "method": "build.plan", "params": {"path": "/x"}}""");
        var response = Parse(await output.NextAsync());
        Assert.Equal("a", response["id"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.NotFound, response["error"]!["code"]!.GetValue<string>());

        await server.HandleLineAsync("""{"id": 2, "method": "build.clean", "params": {"path": "/x"}}""");
        response = Parse(await output.NextAsync());
        Assert.Equal(4, response["result"]!["removed"]!.GetValue<int>());
    }

    [Fact]
    public async Task EventsCarryTheRequestIdAndCancelStopsTheRequest()
    {
        using var services = TestServices.Create(build: new WaitingBuild());
        var output = new LineWriter();
        using var server = new RpcServer(services, output);

        var running = server.HandleLineAsync("""{"id": 5, "method": "build.run", "params": {"path": "/repo", "mode": "changed"}}""");

        var started = Parse(await output.NextAsync());
        Assert.Equal(EventNames.BuildStarted, started["event"]!.GetValue<string>());
        var data = started["data"]!.AsObject();
        Assert.Equal(["requestId", "path", "total"], data.Select(property => property.Key));
        Assert.Equal(5, data["requestId"]!.GetValue<int>());
        Assert.Equal("/repo", data["path"]!.GetValue<string>());
        Assert.Equal(3, data["total"]!.GetValue<int>());
        Assert.Equal(1, server.InFlightCount);

        await server.HandleLineAsync("""{"id": 6, "method": "rpc.cancel", "params": {"requestId": 5}}""");
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        var responses = new[] { Parse(await output.NextAsync()), Parse(await output.NextAsync()) };
        var cancel = responses.Single(response => response["id"]!.GetValue<int>() == 6);
        var build = responses.Single(response => response["id"]!.GetValue<int>() == 5);

        Assert.True(cancel["result"]!["cancelled"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.Cancelled, build["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, server.InFlightCount);

        // cancelling something that is not running says so
        await server.HandleLineAsync("""{"id": 9, "method": "rpc.cancel", "params": {"requestId": 5}}""");
        Assert.False(Parse(await output.NextAsync())["result"]!["cancelled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RunAsyncAnswersEveryRequestAndCancelsWhatRunsWhenInputEnds()
    {
        using var services = TestServices.Create(build: new WaitingBuild());
        var output = new LineWriter();
        using var server = new RpcServer(services, output);
        var input = new QueueReader();

        var run = server.RunAsync(input);
        input.Send("""{"id": 1, "method": "app.info"}""");
        input.Send("not json at all");
        input.Send("");
        input.Send("""{"id": 2, "method": "build.run", "params": {"path": "/repo", "mode": "full"}}""");

        var seen = new List<JsonObject>();

        while (!seen.Any(line => line["event"]?.GetValue<string>() == EventNames.BuildStarted))
        {
            seen.Add(Parse(await output.NextAsync()));
        }

        input.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        var lines = output.All.Select(Parse).ToList();
        Assert.Contains(lines, line => line["id"]?.GetValue<int>() == 1 && line.ContainsKey("result"));
        Assert.Contains(lines, line => line["id"]?.GetValue<int>() == 2 && line["error"]?["code"]?.GetValue<string>() == ErrorCodes.Cancelled);
    }

    [Fact]
    public async Task WatchChangesBecomeGlobalEvents()
    {
        var watch = new FakeWatch();
        var real = TestServices.Create();
        using var services = new Services(real.Settings, real.Env, real.Git, real.Projects, real.Import, real.Build, real.Pack, real.Steam, real.Publish, watch);
        var output = new LineWriter();
        using var server = new RpcServer(services, output);

        watch.Raise(new WatchChangedEvent("/repo", ["content/panorama/layout/hud.xml"]));

        var line = Parse(await output.NextAsync());
        Assert.Equal(EventNames.WatchChanged, line["event"]!.GetValue<string>());
        Assert.False(line["data"]!.AsObject().ContainsKey("requestId"));
        Assert.Equal("content/panorama/layout/hud.xml", line["data"]!["files"]![0]!.GetValue<string>());
    }

    [Fact]
    public void EveryMethodHasAHandler()
    {
        using var services = TestServices.Create();
        var handlers = RpcMethods.Create(services);

        Assert.Equal(35, RpcMethods.Names.Count);
        Assert.Equal(RpcMethods.Names.Where(name => name != RpcMethods.Cancel).Order(), handlers.Keys.Order());
    }

    [Fact]
    public void ErrorsMapToSpecCodes()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Equal(ErrorCodes.Cancelled, RpcServer.ToError(new OperationCanceledException(), cancelled.Token, "x").Code);
        Assert.Equal(ErrorCodes.Internal, RpcServer.ToError(new OperationCanceledException(), CancellationToken.None, "x").Code);
        Assert.Equal(ErrorCodes.Busy, RpcServer.ToError(AddonStudioException.Busy("busy"), CancellationToken.None, "x").Code);

        var (code, message, _) = RpcServer.ToError(new InvalidOperationException("boom"), CancellationToken.None, "x");
        Assert.Equal(ErrorCodes.Internal, code);
        Assert.Equal("boom", message);
    }

    private sealed class FakeWatch : IWatchService
    {
        public event Action<WatchChangedEvent>? Changed;

        public void Raise(WatchChangedEvent change) => Changed?.Invoke(change);

        public Task<WatchSetResult> SetAsync(string path, bool enabled, CancellationToken ct = default) => Task.FromResult(new WatchSetResult(enabled));

        public void Dispose()
        {
        }
    }
}
