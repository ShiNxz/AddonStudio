using System.Text.Json.Nodes;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Host.Rpc;

/// <summary>
/// The sink a request's handler emits through: adds "requestId" (first) to the event data. Closed once the
/// response is written, so no event of a request ever arrives after its response.
/// </summary>
public sealed class RequestEventSink : IEventSink
{
    private readonly RpcWriter writer;
    private readonly JsonNode requestId;
    private volatile bool closed;

    public RequestEventSink(RpcWriter writer, JsonNode requestId)
    {
        this.writer = writer;
        this.requestId = requestId;
    }

    public void Close() => closed = true;

    public void Emit(string eventName, object data)
    {
        if (closed)
        {
            return;
        }

        try
        {
            var payload = new JsonObject { ["requestId"] = requestId.DeepClone() };

            switch (Json.ToNode(data))
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (key == "requestId")
                        {
                            continue;
                        }

                        obj.Remove(key);
                        payload[key] = value;
                    }

                    break;
                case { } other:
                    payload["value"] = other;
                    break;
            }

            if (!closed)
            {
                writer.WriteEvent(eventName, payload);
            }
        }
        catch (Exception exception)
        {
            Log.Warn($"Dropped event {eventName}: {exception.Message}");
        }
    }
}

/// <summary>Events that belong to no request (watch.changed): data as it is, no requestId.</summary>
public sealed class GlobalEventSink : IEventSink
{
    private readonly RpcWriter writer;

    public GlobalEventSink(RpcWriter writer)
    {
        this.writer = writer;
    }

    public void Emit(string eventName, object data)
    {
        try
        {
            writer.WriteEvent(eventName, Json.ToNode(data));
        }
        catch (Exception exception)
        {
            Log.Warn($"Dropped event {eventName}: {exception.Message}");
        }
    }
}
