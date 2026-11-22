using System.Text.Json.Serialization;

namespace AddonStudio.Core.Util;

/// <summary>One packed file in last-publish.json.</summary>
public sealed record LastPublishEntry(long Size, string Sha256);

/// <summary>
/// .addonstudio/last-publish.json (SPEC section 5): { at, workshopId, commit, files: { "&lt;path&gt;": { size, sha256 } } }.
/// File paths are build-relative with forward slashes, as packed; lookups ignore case.
/// </summary>
public sealed record LastPublish
{
    /// <summary>When the upload finished.</summary>
    public DateTimeOffset At { get; init; }

    [JsonConverter(typeof(StringOrNumberConverter))]
    public string WorkshopId { get; init; } = "";

    /// <summary>HEAD's full hash at publish time, null when the repo had no commits.</summary>
    public string? Commit { get; init; }

    public IReadOnlyDictionary<string, LastPublishEntry> Files { get; init; } = new Dictionary<string, LastPublishEntry>(Paths.RelComparer);

    /// <summary>Total size of the published files.</summary>
    [JsonIgnore]
    public long TotalSize => Files.Values.Sum(entry => entry.Size);

    /// <summary>The repo's last-publish.json, null when nothing was published from this machine yet or the file is broken.</summary>
    public static LastPublish? Read(string repo)
    {
        var record = Json.TryReadFile<LastPublish>(StatePaths.LastPublish(repo));

        if (record == null)
        {
            return null;
        }

        var files = new Dictionary<string, LastPublishEntry>(Paths.RelComparer);

        foreach (var (path, entry) in record.Files ?? new Dictionary<string, LastPublishEntry>())
        {
            if (entry != null)
            {
                files[Paths.ToForward(path)] = entry;
            }
        }

        return record with { WorkshopId = record.WorkshopId ?? "", Files = files };
    }

    /// <summary>Writes the repo's last-publish.json atomically (creates .addonstudio/).</summary>
    public void Write(string repo)
    {
        var sorted = new SortedDictionary<string, LastPublishEntry>(StringComparer.Ordinal);

        foreach (var (path, entry) in Files)
        {
            sorted[Paths.ToForward(path)] = entry;
        }

        Json.WriteFile(StatePaths.LastPublish(repo), this with { Files = sorted });
    }
}
