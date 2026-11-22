using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Util;

public sealed class PathsTests
{
    [Theory]
    [InlineData("panorama/layout/hud.xml", "panorama/layout/hud.xml")]
    [InlineData(@"panorama\layout\hud.xml", "panorama/layout/hud.xml")]
    [InlineData("./panorama//images/./a.png", "panorama/images/a.png")]
    [InlineData("models/x.vmdl/", "models/x.vmdl")]
    [InlineData("  sounds/a.wav ", "sounds/a.wav")]
    public void NormalizesSafeRelativePaths(string input, string expected)
    {
        Assert.True(Paths.TryNormalizeRelative(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, Paths.NormalizeRelative(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\windows\system32")]
    [InlineData(@"C:\Users\x")]
    [InlineData("C:x")]
    [InlineData("../outside")]
    [InlineData("a/../../b")]
    [InlineData("a/..")]
    [InlineData(@"a\..\b")]
    [InlineData("file.txt:stream")]
    [InlineData("a/.../b")]
    [InlineData("a/ . /b")]
    [InlineData("a\0b")]
    [InlineData("./.")]
    public void RejectsUnsafeRelativePaths(string? input)
    {
        Assert.False(Paths.TryNormalizeRelative(input, out _));
        Assert.False(Paths.IsSafeRelative(input));
        var error = Assert.Throws<AddonStudioException>(() => Paths.NormalizeRelative(input));
        Assert.Equal(ErrorCodes.InvalidParams, error.Code);
    }

    [Fact]
    public void IsUnderGuardsTraversal()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(root);

        Assert.True(Paths.IsUnder(root, Path.Combine(root, "content", "a.xml")));
        Assert.True(Paths.IsUnder(root + Path.DirectorySeparatorChar, Path.Combine(root, "a")));
        Assert.False(Paths.IsUnder(root, root));
        Assert.True(Paths.IsUnder(root, root, allowEqual: true));
        Assert.False(Paths.IsUnder(root, Path.Combine(root, "..", "other")));
        Assert.False(Paths.IsUnder(root, Path.Combine(root, "content", "..", "..", "repo2", "x")));
        Assert.False(Paths.IsUnder(root, root + "2"));
        Assert.False(Paths.IsUnder(root, temp.Path));
        Assert.False(Paths.IsUnder("", root));
        Assert.True(Paths.IsUnder(root, Path.Combine(root, "a", "..", "b")));
    }

    [Fact]
    public void SafeCombineStaysInside()
    {
        using var temp = new TempDir();

        var combined = Paths.SafeCombine(temp.Path, "content/panorama/a.xml");
        Assert.Equal(Path.Combine(Paths.Full(temp.Path), "content", "panorama", "a.xml"), combined);
        Assert.True(Paths.IsUnder(temp.Path, combined));

        Assert.Throws<AddonStudioException>(() => Paths.SafeCombine(temp.Path, "../x"));
        Assert.Throws<AddonStudioException>(() => Paths.SafeCombine(temp.Path, "/x"));
    }

    [Fact]
    public void RelativeUsesForwardSlashes()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "content", "panorama", "a.xml");

        Assert.Equal("content/panorama/a.xml", Paths.Relative(temp.Path, file));
        Assert.Throws<ArgumentException>(() => Paths.Relative(Path.Combine(temp.Path, "content"), temp.Path));
    }

    [Fact]
    public void ForwardAndNative()
    {
        Assert.Equal("a/b/c", Paths.ToForward(@"a\b/c"));
        Assert.Equal(Path.Combine("a", "b"), Paths.ToNative("a/b"));
        Assert.Equal(".vmdl_c", Paths.Extension("models/X.VMDL_C"));
        Assert.Equal("", Paths.Extension("README"));
    }

    [Fact]
    public void ListFilesSkipsFoldersAndSorts()
    {
        using var temp = new TempDir();
        temp.Write("b/two.txt", "2");
        temp.Write("a/one.txt", "1");
        temp.Write(".git/config", "x");
        temp.Write("a/.addonstudio/state.json", "{}");

        var files = Paths.ListFiles(temp.Path, [".git", ".addonstudio"]);

        Assert.Equal(["a/one.txt", "b/two.txt"], files);
        Assert.Empty(Paths.ListFiles(Path.Combine(temp.Path, "missing")));
    }
}
