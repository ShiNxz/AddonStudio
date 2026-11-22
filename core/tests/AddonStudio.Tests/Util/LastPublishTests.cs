using AddonStudio.Core.Util;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Util;

public sealed class LastPublishTests
{
    [Fact]
    public void WritesAndReadsBack()
    {
        using var temp = new TempDir();
        Assert.Null(LastPublish.Read(temp.Path));

        var at = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        var record = new LastPublish
        {
            At = at,
            WorkshopId = "3389501123",
            Commit = "0123456789abcdef0123456789abcdef01234567",
            Files = new Dictionary<string, LastPublishEntry>
            {
                ["panorama/layout/hud.vxml_c"] = new(120, new string('a', 64)),
                [@"models\x.vmdl_c"] = new(3000, new string('b', 64)),
            },
        };

        record.Write(temp.Path);
        Assert.True(File.Exists(StatePaths.LastPublish(temp.Path)));

        var read = LastPublish.Read(temp.Path)!;
        Assert.Equal(at, read.At);
        Assert.Equal("3389501123", read.WorkshopId);
        Assert.Equal(record.Commit, read.Commit);
        Assert.Equal(2, read.Files.Count);
        Assert.Equal(3120, read.TotalSize);
        Assert.True(read.Files.ContainsKey("models/x.vmdl_c"));
        Assert.True(read.Files.ContainsKey("PANORAMA/layout/HUD.vxml_c"));

        var text = File.ReadAllText(StatePaths.LastPublish(temp.Path));
        Assert.Contains("\"workshopId\": \"3389501123\"", text);
        Assert.Contains("\"sha256\"", text);
        Assert.DoesNotContain("totalSize", text);
    }

    [Fact]
    public void BrokenFileReadsAsNothing()
    {
        using var temp = new TempDir();
        temp.Write(".addonstudio/last-publish.json", "{");

        Assert.Null(LastPublish.Read(temp.Path));
    }

    [Fact]
    public void NumericWorkshopIdIsAString()
    {
        using var temp = new TempDir();
        temp.Write(".addonstudio/last-publish.json", """{"at":"2026-11-22T10:00:00Z","workshopId":42,"commit":null,"files":{}}""");

        Assert.Equal("42", LastPublish.Read(temp.Path)!.WorkshopId);
    }
}
