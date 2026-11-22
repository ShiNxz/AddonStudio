using AddonStudio.Core.Util;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Util;

public sealed class FileHasherTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void HashesAndCachesBySizeAndMtime()
    {
        using var temp = new TempDir();
        var file = temp.Write("content/a.txt", "abc");
        var cache = Path.Combine(temp.Path, ".addonstudio", "hash-cache.json");
        var hasher = new FileHasher(cache);

        Assert.Equal(AbcSha256, hasher.Hash(file));
        Assert.Equal(AbcSha256, FileHasher.HashFile(file));
        Assert.Equal(1, hasher.Count);

        // same size and the old mtime: the cache answers without reading the file
        var mtime = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "xyz");
        File.SetLastWriteTimeUtc(file, mtime);
        Assert.Equal(AbcSha256, hasher.Hash(file));

        // a new mtime: hashed again
        File.SetLastWriteTimeUtc(file, mtime.AddSeconds(5));
        Assert.Equal(FileHasher.HashText("xyz"), hasher.Hash(file));

        hasher.Save();
        Assert.True(File.Exists(cache));

        var reloaded = new FileHasher(cache);
        Assert.Equal(1, reloaded.Count);
        File.SetLastWriteTimeUtc(file, mtime.AddSeconds(5));
        Assert.Equal(FileHasher.HashText("xyz"), reloaded.Hash(file));
    }

    [Fact]
    public void ThreadSafeAndPrunes()
    {
        using var temp = new TempDir();
        var files = Enumerable.Range(0, 50).Select(i => temp.Write($"f{i}.txt", $"file {i}")).ToList();
        var hasher = new FileHasher(Path.Combine(temp.Path, "cache.json"));

        Parallel.ForEach(Enumerable.Range(0, 400), i => hasher.Hash(files[i % files.Count]));
        Assert.Equal(50, hasher.Count);

        File.Delete(files[0]);
        hasher.Prune();
        Assert.Equal(49, hasher.Count);

        hasher.Forget(files[1]);
        Assert.Equal(48, hasher.Count);
    }

    [Fact]
    public void BrokenCacheFileIsIgnored()
    {
        using var temp = new TempDir();
        var cache = temp.Write("cache.json", "{ not json");

        var hasher = new FileHasher(cache);
        Assert.Equal(0, hasher.Count);
        Assert.Equal(AbcSha256, hasher.Hash(temp.Write("a.txt", "abc")));
    }

    [Fact]
    public void MissingFileThrows()
    {
        Assert.Throws<FileNotFoundException>(() => new FileHasher(null).Hash("/no/such/file"));
    }

    [Fact]
    public void CombineIsOrderAndBoundarySensitive()
    {
        Assert.Equal(FileHasher.Combine(["a", "b"]), FileHasher.Combine(["a", "b"]));
        Assert.NotEqual(FileHasher.Combine(["a", "b"]), FileHasher.Combine(["b", "a"]));
        Assert.NotEqual(FileHasher.Combine(["ab", "c"]), FileHasher.Combine(["a", "bc"]));
        Assert.Equal(64, FileHasher.Combine([]).Length);
        Assert.Equal(AbcSha256, FileHasher.HashText("abc"));
    }
}
