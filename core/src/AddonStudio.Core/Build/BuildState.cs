using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Build;

/// <summary>One compiled root in build-state.json. Paths: deps content-relative, outputs build-relative.</summary>
public sealed record BuildStateRoot
{
    public string Hash { get; init; } = "";

    public IReadOnlyList<string> Deps { get; init; } = [];

    public IReadOnlyList<string> Outputs { get; init; } = [];

    public RootResult Result { get; init; } = RootResult.Ok;

    public DateTimeOffset? BuiltAt { get; init; }
}

/// <summary>One prebuilt/ file in build-state.json: the size and mtime of the source when it was copied.</summary>
public sealed record BuildStatePrebuilt(long Size, DateTimeOffset Mtime);

/// <summary>
/// .addonstudio/build-state.json (SPEC section 4 "Build state"). Keys ignore case. Not thread-safe: the build service
/// guards it with its own lock.
/// </summary>
public sealed class BuildState
{
    public const int CurrentVersion = 2;

    /// <summary>
    /// Version 1 builds could record a changed root as ok while resourcecompiler kept its old output (it called it up to
    /// date), and later builds then skipped it as unchanged. Such a state loads with every root's hash cleared: every
    /// root counts as changed and rebuilds once, and the recorded outputs still let deleted roots be cleaned up.
    /// </summary>
    private const int UntrustedHashesVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string? CompilerFingerprint { get; set; }

    public Dictionary<string, BuildStateRoot> Roots { get; set; } = new(Paths.RelComparer);

    public Dictionary<string, BuildStatePrebuilt> Prebuilt { get; set; } = new(Paths.RelComparer);

    /// <summary>
    /// The repo's build state, an empty one when the file is missing, broken or from an unknown version. A version 1
    /// file loads with no root hashes (see <see cref="UntrustedHashesVersion"/>).
    /// </summary>
    public static BuildState Load(string repo)
    {
        var file = Json.TryReadFile<FileModel>(StatePaths.BuildState(repo));
        var state = new BuildState();

        if (file is not { Version: CurrentVersion or UntrustedHashesVersion })
        {
            return state;
        }

        var trustHashes = file.Version == CurrentVersion;
        state.CompilerFingerprint = file.CompilerFingerprint;

        foreach (var (path, root) in file.Roots ?? new Dictionary<string, BuildStateRoot>())
        {
            if (root != null && Paths.TryNormalizeRelative(path, out var normalized))
            {
                state.Roots[normalized] = root with
                {
                    Hash = trustHashes ? root.Hash ?? "" : "",
                    Deps = root.Deps ?? [],
                    Outputs = root.Outputs ?? [],
                };
            }
        }

        foreach (var (path, entry) in file.Prebuilt ?? new Dictionary<string, BuildStatePrebuilt>())
        {
            if (entry != null && Paths.TryNormalizeRelative(path, out var normalized))
            {
                state.Prebuilt[normalized] = entry;
            }
        }

        return state;
    }

    /// <summary>Writes build-state.json atomically, keys sorted so the file diffs well.</summary>
    public void Save(string repo)
    {
        var model = new FileModel(
            CurrentVersion,
            CompilerFingerprint,
            new SortedDictionary<string, BuildStateRoot>(Roots, StringComparer.Ordinal),
            new SortedDictionary<string, BuildStatePrebuilt>(Prebuilt, StringComparer.Ordinal));

        Json.WriteFile(StatePaths.BuildState(repo), model);
    }

    /// <summary>Every build-relative output recorded for any root.</summary>
    public HashSet<string> RecordedOutputs()
    {
        var outputs = new HashSet<string>(Paths.RelComparer);

        foreach (var root in Roots.Values)
        {
            outputs.UnionWith(root.Outputs);
        }

        return outputs;
    }

    private sealed record FileModel(
        int Version,
        string? CompilerFingerprint,
        IDictionary<string, BuildStateRoot>? Roots,
        IDictionary<string, BuildStatePrebuilt>? Prebuilt);
}
