using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AddonStudio.Core.Util;

namespace AddonStudio.Host.Rpc;

/// <summary>
/// The one stdout writer: every protocol message is one JSON line, written under a lock and flushed, so concurrent
/// requests and events never interleave. Lines end with "\n" only.
/// </summary>
public sealed class RpcWriter
{
    private readonly TextWriter output;
    private readonly object gate = new();

    public RpcWriter(TextWriter output)
    {
        this.output = output;
    }

    /// <summary>{"id": id, "result": result}. A result that fails to serialize becomes an internal error.</summary>
    public void WriteResult(JsonNode? id, object? result)
    {
        string line;

        try
        {
            line = Compose(writer =>
            {
                WriteId(writer, id);
                writer.WritePropertyName("result");

                if (result == null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    JsonSerializer.Serialize(writer, result, result.GetType(), Json.Options);
                }
            });
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            Log.Error("Could not serialize a result", exception);
            WriteError(id, Core.Contracts.ErrorCodes.Internal, $"Could not serialize the result: {exception.Message}", null);
            return;
        }

        WriteLine(line);
    }

    /// <summary>{"id": id, "error": {"code", "message", "details"}}</summary>
    public void WriteError(JsonNode? id, string code, string message, object? details)
    {
        string line;

        try
        {
            line = ComposeError(id, code, message, details);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            Log.Error("Could not serialize error details", exception);
            line = ComposeError(id, code, message, null);
        }

        WriteLine(line);
    }

    /// <summary>{"event": name, "data": data}</summary>
    public void WriteEvent(string name, JsonNode? data)
    {
        var line = Compose(writer =>
        {
            writer.WriteString("event", name);
            writer.WritePropertyName("data");

            if (data == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                data.WriteTo(writer, Json.Options);
            }
        });

        WriteLine(line);
    }

    private static string ComposeError(JsonNode? id, string code, string message, object? details)
    {
        return Compose(writer =>
        {
            WriteId(writer, id);
            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WritePropertyName("details");

            if (details == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, details, details.GetType(), Json.Options);
            }

            writer.WriteEndObject();
        });
    }

    private static void WriteId(Utf8JsonWriter writer, JsonNode? id)
    {
        writer.WritePropertyName("id");

        if (id == null)
        {
            writer.WriteNullValue();
        }
        else
        {
            id.WriteTo(writer);
        }
    }

    private static string Compose(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = Json.Options.Encoder, Indented = false }))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void WriteLine(string line)
    {
        lock (gate)
        {
            try
            {
                output.Write(line);
                output.Write('\n');
                output.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // the app closed our stdout: nothing left to talk to
                Log.Warn($"stdout is gone: {exception.Message}");
            }
        }
    }
}
