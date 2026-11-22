using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Projects;

/// <summary>
/// Reads, validates and writes workshop.json (SPEC section 3). Reading is tolerant: a key with the wrong type gets
/// its default and an error, so a broken hand edit still opens the project. Saving merges into the JSON on disk,
/// so keys this version does not know are kept.
/// </summary>
public static partial class WorkshopConfigFile
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static readonly string[] VisibilityNames = ["public", "friendsOnly", "private", "unlisted"];

    [GeneratedRegex(WorkshopConfig.AddonPattern)]
    public static partial Regex AddonRegex();

    /// <summary>True for a valid csgo_addons folder name.</summary>
    public static bool IsValidAddon(string? addon) => addon != null && AddonRegex().IsMatch(addon);

    /// <summary>workshop.json of the repo with the errors found while reading and validating it.</summary>
    public static (WorkshopConfig Config, List<string> Errors) Read(string repo)
    {
        var file = Path.Combine(repo, WorkshopConfig.FileName);

        if (!File.Exists(file))
        {
            throw AddonStudioException.NotFound($"{repo} has no {WorkshopConfig.FileName}, it is not an Addon Studio project.");
        }

        JsonNode? node;

        try
        {
            node = JsonNode.Parse(File.ReadAllText(file), documentOptions: DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw AddonStudioException.InvalidParams($"{file} is not valid JSON: {exception.Message}");
        }

        if (node is not JsonObject obj)
        {
            throw AddonStudioException.InvalidParams($"{file} must hold a JSON object.");
        }

        var errors = new List<string>();
        var config = FromJson(obj, errors);
        errors.AddRange(Validate(config, repo));

        return (config, errors);
    }

    /// <summary>The config from a JSON object, defaults for missing keys, an error for every key with the wrong type.</summary>
    public static WorkshopConfig FromJson(JsonObject obj, List<string> errors)
    {
        var defaults = new WorkshopConfig();
        var pack = Find(obj, "pack") as JsonObject;
        var autoPublish = Find(obj, "autoPublish") as JsonObject;

        if (Find(obj, "pack") is { } packNode && pack == null)
        {
            errors.Add($"pack must be an object, got {packNode.GetValueKind().ToString().ToLowerInvariant()}.");
        }

        if (Find(obj, "autoPublish") is { } autoNode && autoPublish == null)
        {
            errors.Add($"autoPublish must be an object, got {autoNode.GetValueKind().ToString().ToLowerInvariant()}.");
        }

        return new WorkshopConfig
        {
            Addon = ReadString(obj, "addon", errors, nullable: false) ?? defaults.Addon,
            WorkshopId = ReadWorkshopId(obj, errors),
            Title = ReadString(obj, "title", errors, nullable: false) ?? defaults.Title,
            Description = ReadString(obj, "description", errors, nullable: true),
            Visibility = ReadVisibility(obj, errors) ?? defaults.Visibility,
            Tags = ReadStrings(obj, "tags", errors) ?? defaults.Tags,
            Preview = ReadString(obj, "preview", errors, nullable: true),
            Pack = new PackConfig
            {
                ValveWhitelist = (pack == null ? null : ReadBool(pack, "valveWhitelist", errors, "pack.")) ?? false,
                Include = (pack == null ? null : ReadStrings(pack, "include", errors, "pack.")) ?? [],
                Exclude = (pack == null ? null : ReadStrings(pack, "exclude", errors, "pack.")) ?? [],
            },
            AutoBuild = ReadBool(obj, "autoBuild", errors) ?? defaults.AutoBuild,
            AutoPublish = new AutoPublishConfig
            {
                Enabled = (autoPublish == null ? null : ReadBool(autoPublish, "enabled", errors, "autoPublish.")) ?? false,
                Pattern = (autoPublish == null ? null : ReadString(autoPublish, "pattern", errors, nullable: false, "autoPublish.")) ?? AutoPublishConfig.DefaultPattern,
                GithubRelease = (autoPublish == null ? null : ReadBool(autoPublish, "githubRelease", errors, "autoPublish.")) ?? false,
            },
        };
    }

    /// <summary>What is wrong with the config (empty when nothing is). Repo is used to check that the preview exists.</summary>
    public static List<string> Validate(WorkshopConfig config, string repo)
    {
        var errors = new List<string>();

        if (!IsValidAddon(config.Addon))
        {
            errors.Add($"addon \"{config.Addon}\" is not valid: use 1 to 64 lowercase letters, digits or underscores.");
        }

        if (string.IsNullOrWhiteSpace(config.Title))
        {
            errors.Add("title is empty.");
        }

        if (config.WorkshopId != null && (config.WorkshopId.Length == 0 || !config.WorkshopId.All(char.IsAsciiDigit)))
        {
            errors.Add($"workshopId \"{config.WorkshopId}\" must be a number (or null before the first publish).");
        }

        if (!Enum.IsDefined(config.Visibility))
        {
            errors.Add("visibility must be one of public, friendsOnly, private, unlisted.");
        }

        if (!string.IsNullOrWhiteSpace(config.Preview))
        {
            if (!Paths.TryNormalizeRelative(config.Preview, out var preview))
            {
                errors.Add($"preview \"{config.Preview}\" must be a path inside the project folder.");
            }
            else if (!File.Exists(Path.Combine(repo, Paths.ToNative(preview))))
            {
                errors.Add($"preview file \"{preview}\" does not exist.");
            }
        }

        try
        {
            var regex = new Regex(config.AutoPublish.Pattern ?? "", RegexOptions.None, TimeSpan.FromSeconds(1));

            if (regex.GetGroupNumbers().Length < 2)
            {
                errors.Add("autoPublish.pattern needs a capture group: group 1 is the version.");
            }
        }
        catch (ArgumentException exception)
        {
            errors.Add($"autoPublish.pattern is not a valid regular expression: {exception.Message}");
        }

        return errors;
    }

    /// <summary>Writes the config into the repo's workshop.json, keeping keys of the file this version does not know.</summary>
    public static void Save(string repo, WorkshopConfig config)
    {
        var file = Path.Combine(repo, WorkshopConfig.FileName);
        JsonObject existing = new();

        if (File.Exists(file))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file), documentOptions: DocumentOptions) is JsonObject parsed)
                {
                    existing = parsed;
                }
            }
            catch (JsonException exception)
            {
                Log.Warn($"{file} was not valid JSON, it is replaced: {exception.Message}");
            }
        }

        var overlay = (JsonObject)Json.ToNode(config)!;
        Json.WriteNode(file, Json.Merge(existing, overlay));
    }

    private static JsonNode? Find(JsonObject obj, string name)
    {
        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static bool Has(JsonObject obj, string name)
    {
        return obj.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadString(JsonObject obj, string name, List<string> errors, bool nullable, string prefix = "")
    {
        if (!Has(obj, name))
        {
            return null;
        }

        var node = Find(obj, name);

        if (node == null)
        {
            if (!nullable)
            {
                errors.Add($"{prefix}{name} must be a string, got null.");
            }

            return null;
        }

        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return value.GetValue<string>();
        }

        errors.Add($"{prefix}{name} must be a string, got {node.GetValueKind().ToString().ToLowerInvariant()}.");

        return null;
    }

    private static string? ReadWorkshopId(JsonObject obj, List<string> errors)
    {
        var node = Find(obj, "workshopId");

        switch (node)
        {
            case null:
                return null;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var text = value.GetValue<string>().Trim();

                return text.Length == 0 ? null : text;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                return value.ToJsonString();
            default:
                errors.Add($"workshopId must be a string, got {node.GetValueKind().ToString().ToLowerInvariant()}.");

                return null;
        }
    }

    private static Visibility? ReadVisibility(JsonObject obj, List<string> errors)
    {
        var node = Find(obj, "visibility");

        if (node == null)
        {
            return null;
        }

        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            var text = value.GetValue<string>();
            var index = Array.FindIndex(VisibilityNames, name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                return (Visibility)Enum.Parse(typeof(Visibility), VisibilityNames[index], ignoreCase: true);
            }

            errors.Add($"visibility \"{text}\" is not valid: use public, friendsOnly, private or unlisted.");

            return null;
        }

        errors.Add($"visibility must be a string, got {node.GetValueKind().ToString().ToLowerInvariant()}.");

        return null;
    }

    private static IReadOnlyList<string>? ReadStrings(JsonObject obj, string name, List<string> errors, string prefix = "")
    {
        var node = Find(obj, name);

        if (node == null)
        {
            return null;
        }

        if (node is not JsonArray array)
        {
            errors.Add($"{prefix}{name} must be a list of strings.");

            return null;
        }

        var result = new List<string>();

        foreach (var item in array)
        {
            if (item is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                result.Add(value.GetValue<string>());
            }
            else
            {
                errors.Add($"{prefix}{name} must only hold strings.");
            }
        }

        return result;
    }

    private static bool? ReadBool(JsonObject obj, string name, List<string> errors, string prefix = "")
    {
        var node = Find(obj, name);

        if (node == null)
        {
            return null;
        }

        if (node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetValue<bool>();
        }

        errors.Add($"{prefix}{name} must be true or false.");

        return null;
    }
}
