using System.Text.Json;
using System.Text.Json.Nodes;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Tests.Contracts;

public sealed class JsonNamingTests
{
    private static JsonObject Object(object value) => JsonNode.Parse(Json.SerializeObject(value))!.AsObject();

    private static string Enum<T>(T value) where T : struct, System.Enum => JsonSerializer.Serialize(value, Json.Options).Trim('"');

    [Fact]
    public void EnumsUseTheSpecStrings()
    {
        Assert.Equal("friendsOnly", Enum(Visibility.FriendsOnly));
        Assert.Equal("public", Enum(Visibility.Public));
        Assert.Equal("unlisted", Enum(Visibility.Unlisted));

        Assert.Equal(["os", "steam", "cs2", "workshopTools", "steamRunning", "git", "gitLfs", "gh"], System.Enum.GetValues<DoctorCheckId>().Select(Enum));
        Assert.Equal(["windows", "macos", "linux"], System.Enum.GetValues<Os>().Select(Enum));
        Assert.Equal(["ok", "warn", "error"], System.Enum.GetValues<CheckStatus>().Select(Enum));
        Assert.Equal(["dark", "light", "system"], System.Enum.GetValues<Theme>().Select(Enum));
        Assert.Equal(["linked", "missing", "conflict", "wrongTarget", "noCs2"], System.Enum.GetValues<LinkState>().Select(Enum));
        Assert.Equal(["copySource", "decompile", "prebuilt", "skip"], System.Enum.GetValues<ImportAction>().Select(Enum));
        Assert.Equal(["panorama", "texture", "model", "material", "particle", "sound", "soundevents", "vdata", "map", "other"], System.Enum.GetValues<RootKind>().Select(Enum));
        Assert.Equal(["new", "changed", "unchanged", "deleted", "unsupported", "failed"], System.Enum.GetValues<RootStatus>().Select(Enum));
        Assert.Equal(["ok", "warning", "error"], System.Enum.GetValues<RootResult>().Select(Enum));
        Assert.Equal(["new", "changed", "unchanged", "deleted"], System.Enum.GetValues<PrebuiltStatus>().Select(Enum));
        Assert.Equal(["ok", "warning", "error", "skipped"], System.Enum.GetValues<ItemStatus>().Select(Enum));
        Assert.Equal(["changed", "full", "files"], System.Enum.GetValues<BuildMode>().Select(Enum));
        Assert.Equal(["added", "changed", "same"], System.Enum.GetValues<FileChange>().Select(Enum));
        Assert.Equal(["compiled", "prebuilt", "other"], System.Enum.GetValues<PackOrigin>().Select(Enum));
        Assert.Equal(["build", "pack", "upload", "tag", "push", "release", "done"], System.Enum.GetValues<PublishStage>().Select(Enum));
        Assert.Equal(["live", "processing", "unknown"], System.Enum.GetValues<LiveState>().Select(Enum));
        Assert.Equal(
            ["unmodified", "modified", "added", "deleted", "renamed", "copied", "untracked", "ignored", "conflicted", "typeChanged"],
            System.Enum.GetValues<GitState>().Select(Enum));
    }

    [Fact]
    public void EnumsReadBackAndRejectNumbers()
    {
        Assert.Equal(Visibility.FriendsOnly, JsonSerializer.Deserialize<Visibility>("\"friendsOnly\"", Json.Options));
        Assert.Equal(RootKind.SoundEvents, JsonSerializer.Deserialize<RootKind>("\"soundevents\"", Json.Options));
        Assert.Equal(LinkState.NoCs2, JsonSerializer.Deserialize<LinkState>("\"noCs2\"", Json.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Visibility>("1", Json.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Visibility>("\"everyone\"", Json.Options));
    }

    [Fact]
    public void DoctorCheckShape()
    {
        var json = Object(new DoctorCheck(DoctorCheckId.WorkshopTools, "Workshop Tools", CheckStatus.Error, "missing", null));

        Assert.Equal(["id", "label", "status", "detail", "fix"], json.Select(property => property.Key));
        Assert.Equal("workshopTools", json["id"]!.GetValue<string>());
        Assert.Equal("error", json["status"]!.GetValue<string>());
        Assert.Null(json["fix"]);
    }

    [Fact]
    public void AppSettingsShape()
    {
        var json = Object(new AppSettings());

        Assert.Equal(["cs2PathOverride", "compileParallelism", "compilerArgs", "maxPackBytes", "theme", "recentProjects"], json.Select(property => property.Key));
        Assert.Equal(3221225472L, json["maxPackBytes"]!.GetValue<long>());
        Assert.Equal("-nop4 -i \"{input}\"", json["compilerArgs"]!.GetValue<string>());
        Assert.Equal("dark", json["theme"]!.GetValue<string>());
        Assert.True(json["compileParallelism"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public void SettingsPatchTellsNullFromMissing()
    {
        var cleared = JsonSerializer.Deserialize<AppSettingsPatch>("""{"cs2PathOverride": null}""", Json.StrictOptions)!;
        var untouched = JsonSerializer.Deserialize<AppSettingsPatch>("""{"theme": "light"}""", Json.StrictOptions)!;
        var current = new AppSettings { Cs2PathOverride = @"D:\Steam\cs2", Theme = Theme.Dark };

        Assert.True(cleared.HasCs2PathOverride);
        Assert.Null(cleared.ApplyTo(current).Cs2PathOverride);

        Assert.False(untouched.HasCs2PathOverride);
        Assert.Equal(@"D:\Steam\cs2", untouched.ApplyTo(current).Cs2PathOverride);
        Assert.Equal(Theme.Light, untouched.ApplyTo(current).Theme);
    }

    [Fact]
    public void WorkshopConfigShapeAndDefaults()
    {
        var json = Object(WorkshopConfig.CreateDefault("next_hud", "Next HUD"));

        Assert.Equal(
            ["addon", "workshopId", "title", "description", "visibility", "tags", "preview", "pack", "autoBuild", "autoPublish"],
            json.Select(property => property.Key));
        Assert.Null(json["workshopId"]);
        Assert.Equal("private", json["visibility"]!.GetValue<string>());
        Assert.Equal(["include", "exclude"], json["pack"]!.AsObject().Select(property => property.Key));
        Assert.Equal(["enabled", "pattern", "githubRelease"], json["autoPublish"]!.AsObject().Select(property => property.Key));
        Assert.Equal(AutoPublishConfig.DefaultPattern, json["autoPublish"]!["pattern"]!.GetValue<string>());

        // a hand edited file: comments, a numeric id, missing keys
        var read = Json.Deserialize<WorkshopConfig>("""
            {
              // the addon
              "addon": "next_hud",
              "workshopId": 3389501123,
              "title": "Next HUD",
            }
            """)!;

        Assert.Equal("3389501123", read.WorkshopId);
        Assert.True(read.AutoBuild);
        Assert.Equal(Visibility.Private, read.Visibility);
        Assert.Empty(read.Tags);
        Assert.Equal("\"3389501123\"", Object(read)["workshopId"]!.ToJsonString());
    }

    [Fact]
    public void BuildDtosShape()
    {
        var counts = Object(new BuildCounts(1, 2, 3, 4, 5, 6, 7));
        Assert.Equal(["new", "changed", "unchanged", "deleted", "unsupported", "failed", "prebuiltChanged"], counts.Select(property => property.Key));

        var root = Object(new BuildRoot("panorama/x.xml", RootKind.SoundEvents, RootStatus.Unsupported, null, [], [], null, null));
        Assert.Equal(["source", "kind", "status", "reason", "dependencies", "outputs", "lastResult", "lastBuiltAt"], root.Select(property => property.Key));
        Assert.Equal("soundevents", root["kind"]!.GetValue<string>());

        var item = Object(new BuildItemResult("a.xml", ItemStatus.Skipped, 12, null, [], [], [], ""));
        Assert.Equal(["source", "status", "durationMs", "exitCode", "outputs", "errors", "warnings", "log"], item.Select(property => property.Key));

        var result = Object(new BuildResult(true, false, 5, [], 2, ["a"]));
        Assert.Equal(["ok", "cancelled", "durationMs", "items", "prebuiltCopied", "removed"], result.Select(property => property.Key));
    }

    [Fact]
    public void DatesAreIsoStrings()
    {
        var at = new DateTimeOffset(2026, 11, 22, 13, 36, 5, TimeSpan.Zero);
        var json = Object(new Deploy("deploy/20261122-133605", at, "3389501123", "first"));

        var text = json["at"]!.GetValue<string>();
        Assert.StartsWith("2026-11-22T13:36:05", text);
        Assert.Equal(at, DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void GitAndPackDtosShape()
    {
        var change = Object(new GitFileChange("content/a.png", null, GitState.TypeChanged, GitState.Unmodified, true, true, 10));
        Assert.Equal(["path", "origPath", "index", "worktree", "staged", "lfs", "size"], change.Select(property => property.Key));
        Assert.Equal("typeChanged", change["index"]!.GetValue<string>());

        var commit = Object(new CommitResult("abc", false, new AutoPublishMatch("1.2.3"), GitStatus.NotARepo(false)));
        Assert.Equal("1.2.3", commit["autoPublish"]!["version"]!.GetValue<string>());
        Assert.Equal(["isRepo", "branch", "upstream", "ahead", "behind", "files", "merging", "lfsAvailable"], commit["status"]!.AsObject().Select(property => property.Key));

        var file = Object(new PackFile("models/x.vmdl_c", 5, null, PackOrigin.Prebuilt));
        Assert.Equal(["path", "size", "change", "origin"], file.Select(property => property.Key));
        Assert.Null(file["change"]);

        var contents = Object(new PackContents([], [], 0, 0, 3L * 1024 * 1024 * 1024, [new PackTypeStat("Models", 1, 5)], null, new PackChanges(0, 0, 0)));
        Assert.Equal(["files", "removed", "fileCount", "totalSize", "limitBytes", "byType", "lastPublish", "changes"], contents.Select(property => property.Key));
        Assert.Equal(["label", "count", "size"], contents["byType"]![0]!.AsObject().Select(property => property.Key));
    }

    [Fact]
    public void SteamStatusKeepsRaw()
    {
        var raw = new JsonObject { ["time_updated"] = 1763800000, ["ban_reason"] = "" };
        var json = Object(new SteamItemStatus("3389501123", true, "Next HUD", Visibility.Public, null, 5, false, null, LiveState.Processing, null, raw));

        Assert.Equal("processing", json["live"]!.GetValue<string>());
        Assert.Equal(1763800000, json["raw"]!["time_updated"]!.GetValue<int>());
        Assert.True(json["raw"]!.AsObject().ContainsKey("ban_reason"));
    }

    [Fact]
    public void EventsShape()
    {
        Assert.Equal(["source", "index", "total"], Object(new BuildItemStartedEvent("a", 0, 1)).Select(property => property.Key));
        Assert.Equal(["stage", "fraction", "message"], Object(new PublishProgressEvent(PublishStage.Upload, 0.5, null)).Select(property => property.Key));
        Assert.Equal(["phase", "percent", "line"], Object(new GitProgressEvent("Receiving objects", 45, "x")).Select(property => property.Key));
        Assert.Equal(["done", "total", "current"], Object(new ImportProgressEvent(1, 2, "x")).Select(property => property.Key));
    }

    [Fact]
    public void StrictParamsRejectMissingAndNull()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PathParams>("{}", Json.StrictOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PathParams>("""{"path": null}""", Json.StrictOptions));

        var run = JsonSerializer.Deserialize<BuildRunParams>("""{"path": "C:\\a"}""", Json.StrictOptions)!;
        Assert.Equal(BuildMode.Changed, run.Mode);
        Assert.Null(run.Files);

        var publish = JsonSerializer.Deserialize<PublishRequest>(
            """{"path":"C:\\a","changeNote":"hi","version":null,"skipBuild":false,"push":true,"githubRelease":false,"allowSourceFolderChange":false}""",
            Json.StrictOptions)!;
        Assert.True(publish.Push);
        Assert.Null(publish.Version);

        var commit = JsonSerializer.Deserialize<CommitRequest>("""{"path":"a","message":"v1.0.0 x","files":null,"push":false}""", Json.StrictOptions)!;
        Assert.Null(commit.Files);

        var config = JsonSerializer.Deserialize<SaveConfigParams>("""{"path":"a","config":{"addon":"x","title":"X","visibility":"friendsOnly"}}""", Json.StrictOptions)!;
        Assert.Equal(Visibility.FriendsOnly, config.Config.Visibility);
    }

    [Fact]
    public void MergeKeepsUnknownKeys()
    {
        var onDisk = JsonNode.Parse("""{"addon":"a","custom":1,"pack":{"include":[],"note":"keep"},"Title":"old"}""")!.AsObject();
        var saved = JsonNode.Parse(Json.Serialize(WorkshopConfig.CreateDefault("a", "New")))!.AsObject();

        var merged = Json.Merge(onDisk, saved);

        Assert.Equal(1, merged["custom"]!.GetValue<int>());
        Assert.Equal("keep", merged["pack"]!["note"]!.GetValue<string>());
        Assert.Equal("**/*.psd", merged["pack"]!["exclude"]![0]!.GetValue<string>());
        Assert.Equal("New", merged["title"]!.GetValue<string>());
        Assert.False(merged.ContainsKey("Title"));
    }
}
