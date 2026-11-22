using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AddonStudio.Core.Util;

/// <summary>
/// SHA-256 of files (lowercase hex), cached in .addonstudio/hash-cache.json keyed by full path + size + mtime, so an
/// unchanged file is never read twice. Thread-safe: hash from many threads, Save from any.
/// </summary>
public sealed class FileHasher
{
    private const int CacheVersion = 1;

    private readonly ConcurrentDictionary<string, CacheEntry> entries;
    private readonly string? cacheFile;
    private readonly object saveGate = new();
    private int dirty;

    /// <param name="cacheFile">The cache file to load and save, null for a cache that lives in memory only.</param>
    public FileHasher(string? cacheFile)
    {
        this.cacheFile = cacheFile;
        entries = new ConcurrentDictionary<string, CacheEntry>(Paths.FsComparer);

        if (cacheFile != null && Json.TryReadFile<CacheFileModel>(cacheFile) is { Version: CacheVersion, Files: { } files })
        {
            foreach (var (path, entry) in files)
            {
                if (entry is { Sha256.Length: 64 })
                {
                    entries[path] = entry;
                }
            }
        }
    }

    /// <summary>A hasher with the repo's .addonstudio/hash-cache.json.</summary>
    public static FileHasher ForRepo(string repo) => new(StatePaths.HashCache(repo));

    /// <summary>Entries in the cache.</summary>
    public int Count => entries.Count;

    /// <summary>The file's SHA-256, from the cache when its size and mtime did not change. Throws FileNotFoundException.</summary>
    public string Hash(string path)
    {
        var info = new FileInfo(path);

        if (!info.Exists)
        {
            throw new FileNotFoundException($"\"{path}\" does not exist.", path);
        }

        var key = info.FullName;
        var size = info.Length;
        var mtime = info.LastWriteTimeUtc;

        if (entries.TryGetValue(key, out var cached) && cached.Size == size && cached.Mtime.Ticks == mtime.Ticks)
        {
            return cached.Sha256;
        }

        var hash = HashFile(key);

        // only cache when the file did not change while it was read
        info.Refresh();

        if (info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == mtime.Ticks)
        {
            entries[key] = new CacheEntry(size, mtime, hash);
            Interlocked.Exchange(ref dirty, 1);
        }

        return hash;
    }

    /// <summary>Hash on the thread pool.</summary>
    public Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() => Hash(path), ct);
    }

    /// <summary>Forgets one file.</summary>
    public void Forget(string path)
    {
        if (entries.TryRemove(Path.GetFullPath(path), out _))
        {
            Interlocked.Exchange(ref dirty, 1);
        }
    }

    /// <summary>Drops entries whose file is gone.</summary>
    public void Prune()
    {
        foreach (var key in entries.Keys)
        {
            if (!File.Exists(key) && entries.TryRemove(key, out _))
            {
                Interlocked.Exchange(ref dirty, 1);
            }
        }
    }

    /// <summary>Writes the cache file when something changed (atomic). No-op for an in-memory hasher. IO errors are logged, not thrown.</summary>
    public void Save()
    {
        if (cacheFile == null || Interlocked.Exchange(ref dirty, 0) == 0)
        {
            return;
        }

        lock (saveGate)
        {
            try
            {
                var files = new SortedDictionary<string, CacheEntry>(entries, StringComparer.Ordinal);
                Json.WriteFile(cacheFile, new CacheFileModel(CacheVersion, files));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Interlocked.Exchange(ref dirty, 1);
                Log.Warn($"Could not save the hash cache {cacheFile}: {exception.Message}");
            }
        }
    }

    /// <summary>SHA-256 of a file without any cache.</summary>
    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string HashBytes(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    /// <summary>SHA-256 of a string's UTF-8 bytes.</summary>
    public static string HashText(string text)
    {
        return HashBytes(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// One hash over several parts in the order given (each part length-prefixed, so ["ab","c"] and ["a","bc"] differ).
    /// For a root: Combine([ownHash, depPath1, depHash1, ...]) with the dependencies sorted.
    /// </summary>
    public static string Combine(IEnumerable<string> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];

        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>A cache entry: size in bytes, last write time (UTC), hash.</summary>
    public sealed record CacheEntry(long Size, DateTime Mtime, string Sha256);

    private sealed record CacheFileModel(int Version, IDictionary<string, CacheEntry> Files);
}
