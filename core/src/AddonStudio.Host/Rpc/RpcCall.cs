using System.Text.Json;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Host.Rpc;

/// <summary>One request as a handler sees it.</summary>
public sealed class RpcCall
{
    public RpcCall(string method, JsonElement? parameters, IEventSink events, CancellationToken cancellationToken)
    {
        Method = method;
        Params = parameters;
        Events = events;
        Ct = cancellationToken;
    }

    public string Method { get; }

    /// <summary>The params as sent, null when there were none.</summary>
    public JsonElement? Params { get; }

    /// <summary>Events emitted here carry this request's id.</summary>
    public IEventSink Events { get; }

    /// <summary>Cancelled by rpc.cancel or when the app goes away.</summary>
    public CancellationToken Ct { get; }

    /// <summary>The params as T (Json.StrictOptions). Missing params or a wrong shape is invalid_params.</summary>
    public T Bind<T>()
        where T : class
    {
        if (Params is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new AddonStudioException(ErrorCodes.InvalidParams, $"{Method} needs params.");
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new AddonStudioException(ErrorCodes.InvalidParams, $"{Method} params must be an object.");
        }

        T? value;

        try
        {
            value = element.Deserialize<T>(Json.StrictOptions);
        }
        catch (JsonException exception)
        {
            throw new AddonStudioException(ErrorCodes.InvalidParams, $"Bad params for {Method}: {exception.Message}", null, exception);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new AddonStudioException(ErrorCodes.InvalidParams, $"Bad params for {Method}: {exception.Message}", null, exception);
        }

        return value ?? throw new AddonStudioException(ErrorCodes.InvalidParams, $"{Method} needs params.");
    }

    /// <summary>A non-empty string param or invalid_params.</summary>
    public static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AddonStudioException(ErrorCodes.InvalidParams, $"{name} is required.");
        }

        return value;
    }
}

/// <summary>A method handler: the result is serialized with Json.Options.</summary>
public delegate Task<object?> RpcHandler(RpcCall call);
