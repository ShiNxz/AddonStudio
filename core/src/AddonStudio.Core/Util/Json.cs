using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AddonStudio.Core.Util;

/// <summary>
/// The one JSON setup for RPC, state files and CLI output: camelCase properties, enums as camelCase strings,
/// dates as ISO 8601, nulls written (TS types use "| null"), dictionary keys left as they are (paths, Steam fields).
/// </summary>
public static class Json
{
    /// <summary>Compact, for RPC lines.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: false, strict: false);

    /// <summary>Indented, for files on disk and CLI output.</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true, strict: false);

    /// <summary>
    /// For binding RPC params: a missing constructor parameter without a default, or null for a non-nullable
    /// property, throws JsonException (the host turns it into invalid_params).
    /// </summary>
    public static JsonSerializerOptions StrictOptions { get; } = Create(indented: false, strict: true);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static JsonSerializerOptions Create(bool indented, bool strict)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = indented,
            // paths, Hebrew titles and quotes stay readable in files; still valid JSON for JSON.parse
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = strict,
            RespectRequiredConstructorParameters = strict,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();

        return options;
    }

    public static string Serialize<T>(T value, bool indented = false)
    {
        return JsonSerializer.Serialize(value, indented ? Indented : Options);
    }

    /// <summary>Serializes with the runtime type (so a value typed as object keeps its properties).</summary>
    public static string SerializeObject(object? value, bool indented = false)
    {
        return value == null ? "null" : JsonSerializer.Serialize(value, value.GetType(), indented ? Indented : Options);
    }

    /// <summary>The value as a JSON node, with its runtime type.</summary>
    public static JsonNode? ToNode(object? value)
    {
        return value == null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Options);
    }

    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    /// <summary>Reads and deserializes a JSON file, null when the file does not exist. Throws JsonException on bad JSON.</summary>
    public static T? ReadFile<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        return JsonSerializer.Deserialize<T>(stream, Options);
    }

    /// <summary>Like ReadFile, but a missing, empty or broken file gives null instead of throwing.</summary>
    public static T? TryReadFile<T>(string path)
        where T : class
    {
        try
        {
            return ReadFile<T>(path);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes indented JSON atomically (temp file + rename), creating the folder. UTF-8 without BOM, trailing newline.</summary>
    public static void WriteFile<T>(string path, T value)
    {
        WriteTextAtomic(path, JsonSerializer.Serialize(value, Indented) + "\n");
    }

    /// <summary>Writes a JSON node indented and atomically.</summary>
    public static void WriteNode(string path, JsonNode node)
    {
        WriteTextAtomic(path, node.ToJsonString(Indented) + "\n");
    }

    /// <summary>Writes text atomically: a temp file next to the target, then a replace. UTF-8 without BOM.</summary>
    public static void WriteTextAtomic(string path, string text)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

        try
        {
            File.WriteAllText(temp, text, Utf8NoBom);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Deep merge for "unknown keys are kept on save": every property of overlay replaces the one in target,
    /// objects are merged key by key, everything else (arrays, values, null) replaces. Target is changed and returned.
    /// </summary>
    public static JsonObject Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            var existingKey = FindKey(target, key);

            if (value is JsonObject overlayObject && existingKey != null && target[existingKey] is JsonObject targetObject)
            {
                Merge(targetObject, overlayObject);
                continue;
            }

            if (existingKey != null && existingKey != key)
            {
                target.Remove(existingKey);
            }

            target[key] = value?.DeepClone();
        }

        return target;
    }

    private static string? FindKey(JsonObject obj, string key)
    {
        if (obj.ContainsKey(key))
        {
            return key;
        }

        foreach (var (existing, _) in obj)
        {
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
        }

        return null;
    }
}

/// <summary>Reads a JSON string or number as a string (workshop ids typed as numbers in hand edited files), writes a string.</summary>
public sealed class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
            _ => throw new JsonException($"Expected a string or a number, got {reader.TokenType}."),
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    public override bool HandleNull => true;
}
