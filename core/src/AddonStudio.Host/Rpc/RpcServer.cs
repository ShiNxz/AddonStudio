using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Host.Rpc;

/// <summary>
/// JSON lines over stdin/stdout (SPEC section 7). Requests run concurrently, each with its own cancellation;
/// responses and events go through one locked writer; logs go to stderr only.
/// </summary>
public sealed class RpcServer : IDisposable
{
    /// <summary>How long in-flight requests get to wind down after stdin closes.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    private readonly RpcWriter writer;
    private readonly IReadOnlyDictionary<string, RpcHandler> methods;
    private readonly ConcurrentDictionary<string, InFlight> inFlight = new(StringComparer.Ordinal);
    private readonly IWatchService watch;
    private readonly GlobalEventSink globalEvents;

    public RpcServer(Services services, TextWriter output)
        : this(services, new RpcWriter(output))
    {
    }

    public RpcServer(Services services, RpcWriter writer)
    {
        this.writer = writer;
        methods = RpcMethods.Create(services);
        globalEvents = new GlobalEventSink(writer);
        watch = services.Watch;
        watch.Changed += OnWatchChanged;
    }

    /// <summary>Events that belong to no request.</summary>
    public IEventSink GlobalEvents => globalEvents;

    /// <summary>Requests still running.</summary>
    public int InFlightCount => inFlight.Count;

    /// <summary>Reads requests until the input ends (the app closed our stdin or went away), then cancels what still runs.</summary>
    public async Task RunAsync(TextReader input, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;

            try
            {
                line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException exception)
            {
                Log.Warn($"stdin failed: {exception.Message}");
                break;
            }

            if (line == null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                _ = HandleLineAsync(line);
            }
        }

        await ShutdownAsync(ShutdownGrace).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles one input line. The returned task finishes when its response is written (rpc.cancel and bad lines
    /// finish at once). Never throws.
    /// </summary>
    public Task HandleLineAsync(string line)
    {
        JsonObject? message;

        try
        {
            message = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException exception)
        {
            Log.Warn($"Ignored a line that is not JSON: {exception.Message}");
            return Task.CompletedTask;
        }

        if (message == null)
        {
            Log.Warn("Ignored a line that is not a JSON object.");
            return Task.CompletedTask;
        }

        if (!message.TryGetPropertyValue("id", out var id) || id == null)
        {
            Log.Warn("Ignored a message without an id.");
            return Task.CompletedTask;
        }

        id = id.DeepClone();

        if (id.GetValueKind() is not (JsonValueKind.Number or JsonValueKind.String))
        {
            writer.WriteError(id, ErrorCodes.InvalidParams, "id must be a number or a string.", null);
            return Task.CompletedTask;
        }

        if (!message.TryGetPropertyValue("method", out var methodNode) || methodNode is not JsonValue methodValue
            || !methodValue.TryGetValue<string>(out var method) || string.IsNullOrWhiteSpace(method))
        {
            writer.WriteError(id, ErrorCodes.InvalidParams, "method is required.", null);
            return Task.CompletedTask;
        }

        JsonElement? parameters = null;

        if (message.TryGetPropertyValue("params", out var paramsNode) && paramsNode != null)
        {
            parameters = JsonSerializer.SerializeToElement(paramsNode);
        }

        if (method == RpcMethods.Cancel)
        {
            HandleCancel(id, parameters);
            return Task.CompletedTask;
        }

        if (!methods.TryGetValue(method, out var handler))
        {
            writer.WriteError(id, ErrorCodes.InvalidParams, $"Unknown method \"{method}\".", null);
            return Task.CompletedTask;
        }

        var key = Key(id);
        var request = new InFlight();

        if (!inFlight.TryAdd(key, request))
        {
            writer.WriteError(id, ErrorCodes.InvalidParams, $"Request {key} is still running, ids must be unique.", null);
            request.Cancellation.Dispose();
            return Task.CompletedTask;
        }

        var task = Task.Run(() => ExecuteAsync(id, key, method, handler, parameters, request));
        request.Task = task;

        return task;
    }

    private async Task ExecuteAsync(JsonNode id, string key, string method, RpcHandler handler, JsonElement? parameters, InFlight request)
    {
        var events = new RequestEventSink(writer, id);
        var ct = request.Cancellation.Token;

        try
        {
            var result = await handler(new RpcCall(method, parameters, events, ct)).ConfigureAwait(false);
            events.Close();
            writer.WriteResult(id, result);
        }
        catch (Exception exception)
        {
            events.Close();
            var (code, text, details) = ToError(exception, ct, method);
            writer.WriteError(id, code, text, details);
        }
        finally
        {
            inFlight.TryRemove(key, out _);
            request.Cancellation.Dispose();
        }
    }

    private void HandleCancel(JsonNode id, JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("requestId", out var target)
            || target.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
        {
            writer.WriteError(id, ErrorCodes.InvalidParams, "rpc.cancel needs requestId.", null);
            return;
        }

        var cancelled = false;

        if (inFlight.TryGetValue(Key(JsonNode.Parse(target.GetRawText())!), out var request))
        {
            try
            {
                request.Cancellation.Cancel();
                cancelled = true;
            }
            catch (ObjectDisposedException)
            {
                // finished in the meantime
            }
            catch (AggregateException exception)
            {
                Log.Warn($"A cancellation callback failed: {exception.InnerException?.Message}");
                cancelled = true;
            }
        }

        writer.WriteResult(id, new CancelResult(cancelled));
    }

    /// <summary>Cancels every running request and waits up to grace for them to answer.</summary>
    public async Task ShutdownAsync(TimeSpan grace)
    {
        var running = inFlight.Values.ToList();

        foreach (var request in running)
        {
            try
            {
                request.Cancellation.Cancel();
            }
            catch (Exception exception) when (exception is ObjectDisposedException or AggregateException)
            {
            }
        }

        var tasks = running.Select(request => request.Task).OfType<Task>().ToArray();

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn($"{tasks.Count(task => !task.IsCompleted)} request(s) did not stop within {grace.TotalSeconds:0}s.");
        }
    }

    /// <summary>Maps an exception to the SPEC error codes.</summary>
    public static (string Code, string Message, object? Details) ToError(Exception exception, CancellationToken ct, string method)
    {
        switch (exception)
        {
            case AddonStudioException known:
                if (known.Code == ErrorCodes.Internal)
                {
                    Log.Error($"{method} failed", known);
                }

                return (known.Code, known.Message, known.Details);
            case OperationCanceledException when ct.IsCancellationRequested:
                return (ErrorCodes.Cancelled, "Cancelled.", null);
            case AggregateException { InnerExceptions.Count: 1 } aggregate:
                return ToError(aggregate.InnerExceptions[0], ct, method);
            default:
                Log.Error($"{method} failed", exception);
                return (ErrorCodes.Internal, exception.Message, new { type = exception.GetType().Name });
        }
    }

    private static string Key(JsonNode id)
    {
        // 7 and "7" are different ids, ToJsonString keeps them apart
        return id.ToJsonString();
    }

    private void OnWatchChanged(WatchChangedEvent change)
    {
        globalEvents.Emit(EventNames.WatchChanged, change);
    }

    public void Dispose()
    {
        watch.Changed -= OnWatchChanged;
    }

    private sealed class InFlight
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Task { get; set; }
    }
}
