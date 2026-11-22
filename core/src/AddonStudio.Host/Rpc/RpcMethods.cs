using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Host.Rpc;

/// <summary>Every method of SPEC section 7 mapped to its service call. rpc.cancel is answered by the server itself.</summary>
public static class RpcMethods
{
    public const string Cancel = "rpc.cancel";

    /// <summary>Every method name the core answers, rpc.cancel included.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "app.info",
        "env.doctor",
        "settings.get",
        "settings.set",
        "projects.list",
        "projects.open",
        "projects.create",
        "projects.clone",
        "projects.saveConfig",
        "projects.link",
        "projects.remove",
        "projects.cs2Addons",
        "import.plan",
        "import.run",
        "build.plan",
        "build.run",
        "build.clean",
        "pack.contents",
        "pack.export",
        "publish.run",
        "steam.items",
        "steam.status",
        "git.status",
        "git.stage",
        "git.unstage",
        "git.discard",
        "git.commit",
        "git.push",
        "git.pull",
        "git.fetch",
        "git.log",
        "git.show",
        "git.publishRepo",
        "git.githubOwners",
        "watch.set",
        Cancel,
    ];

    public const int MaxLogLimit = 1000;

    /// <summary>The handlers for every method but rpc.cancel.</summary>
    public static IReadOnlyDictionary<string, RpcHandler> Create(Services s)
    {
        var methods = new Dictionary<string, RpcHandler>(StringComparer.Ordinal)
        {
            ["app.info"] = c => Task.FromResult<object?>(HostInfo.Create(s.Env)),
            ["env.doctor"] = async c => await s.Env.DoctorAsync(c.Ct),

            ["settings.get"] = c => Task.FromResult<object?>(s.Settings.Get()),
            ["settings.set"] = c =>
            {
                var patch = c.Bind<AppSettingsPatch>();

                if (patch.CompileParallelism is < 1)
                {
                    throw AddonStudioException.InvalidParams("compileParallelism must be at least 1.");
                }

                var result = s.Settings.Set(patch);

                try
                {
                    s.Env.Invalidate();
                }
                catch (Exception exception)
                {
                    Log.Warn($"Environment invalidate failed: {exception.Message}");
                }

                return Task.FromResult<object?>(result);
            },

            ["projects.list"] = async c => await s.Projects.ListAsync(c.Ct),
            ["projects.open"] = async c => await s.Projects.OpenAsync(PathOf(c), c.Ct),
            ["projects.create"] = async c =>
            {
                var p = c.Bind<CreateProjectParams>();
                return await s.Projects.CreateAsync(RpcCall.Require(p.Path, "path"), RpcCall.Require(p.Addon, "addon"), RpcCall.Require(p.Title, "title"), c.Ct);
            },
            ["projects.clone"] = async c =>
            {
                var p = c.Bind<CloneProjectParams>();
                return await s.Projects.CloneAsync(RpcCall.Require(p.Url, "url"), RpcCall.Require(p.Path, "path"), c.Events, c.Ct);
            },
            ["projects.saveConfig"] = async c =>
            {
                var p = c.Bind<SaveConfigParams>();
                return await s.Projects.SaveConfigAsync(RpcCall.Require(p.Path, "path"), p.Config, c.Ct);
            },
            ["projects.link"] = async c =>
            {
                var p = c.Bind<LinkParams>();
                return await s.Projects.LinkAsync(RpcCall.Require(p.Path, "path"), p.Backup, c.Ct);
            },
            ["projects.remove"] = async c => await s.Projects.RemoveAsync(PathOf(c), c.Ct),
            ["projects.cs2Addons"] = async c => await s.Projects.ListCs2AddonsAsync(c.Ct),

            ["import.plan"] = async c =>
            {
                var p = c.Bind<ImportParams>();
                return await s.Import.PlanAsync(RpcCall.Require(p.Addon, "addon"), RpcCall.Require(p.Path, "path"), p.Decompile, c.Ct);
            },
            ["import.run"] = async c =>
            {
                var p = c.Bind<ImportParams>();
                return await s.Import.RunAsync(RpcCall.Require(p.Addon, "addon"), RpcCall.Require(p.Path, "path"), p.Decompile, c.Events, c.Ct);
            },

            ["build.plan"] = async c => await s.Build.PlanAsync(PathOf(c), c.Ct),
            ["build.run"] = async c =>
            {
                var p = c.Bind<BuildRunParams>();

                if (p.Mode == BuildMode.Files && (p.Files == null || p.Files.Count == 0))
                {
                    throw AddonStudioException.InvalidParams("mode \"files\" needs files.");
                }

                return await s.Build.RunAsync(RpcCall.Require(p.Path, "path"), p.Mode, p.Files, c.Events, c.Ct);
            },
            ["build.clean"] = async c => await s.Build.CleanAsync(PathOf(c), c.Ct),

            ["pack.contents"] = async c => await s.Pack.ContentsAsync(PathOf(c), c.Ct),
            ["pack.export"] = async c =>
            {
                var p = c.Bind<PackExportParams>();
                return await s.Pack.ExportAsync(RpcCall.Require(p.Path, "path"), string.IsNullOrWhiteSpace(p.OutDir) ? null : p.OutDir, c.Ct);
            },

            ["publish.run"] = async c =>
            {
                var p = c.Bind<PublishRequest>();
                RpcCall.Require(p.Path, "path");
                return await s.Publish.RunAsync(p with { ChangeNote = p.ChangeNote ?? "" }, c.Events, c.Ct);
            },

            ["steam.items"] = async c => await s.Steam.ItemsAsync(c.Ct),
            ["steam.status"] = async c => await s.Steam.ProjectStatusAsync(PathOf(c), c.Ct),

            ["git.status"] = async c => await s.Git.StatusAsync(PathOf(c), c.Ct),
            ["git.stage"] = async c =>
            {
                var p = c.Bind<GitFilesParams>();
                return await s.Git.StageAsync(RpcCall.Require(p.Path, "path"), p.Files, c.Ct);
            },
            ["git.unstage"] = async c =>
            {
                var p = c.Bind<GitFilesParams>();
                return await s.Git.UnstageAsync(RpcCall.Require(p.Path, "path"), p.Files, c.Ct);
            },
            ["git.discard"] = async c =>
            {
                var p = c.Bind<GitFilesParams>();
                return await s.Git.DiscardAsync(RpcCall.Require(p.Path, "path"), p.Files, c.Ct);
            },
            ["git.commit"] = async c =>
            {
                var p = c.Bind<CommitRequest>();
                RpcCall.Require(p.Path, "path");
                RpcCall.Require(p.Message, "message");
                return await s.Git.CommitAsync(p, c.Events, c.Ct);
            },
            ["git.push"] = async c => await s.Git.PushAsync(PathOf(c), c.Events, c.Ct),
            ["git.pull"] = async c => await s.Git.PullAsync(PathOf(c), c.Events, c.Ct),
            ["git.fetch"] = async c => await s.Git.FetchAsync(PathOf(c), c.Events, c.Ct),
            ["git.log"] = async c =>
            {
                var p = c.Bind<GitLogParams>();

                if (p.Limit < 1 || p.Limit > MaxLogLimit || p.Skip < 0)
                {
                    throw AddonStudioException.InvalidParams($"limit must be 1..{MaxLogLimit} and skip at least 0.");
                }

                return await s.Git.LogAsync(RpcCall.Require(p.Path, "path"), p.Limit, p.Skip, c.Ct);
            },
            ["git.show"] = async c =>
            {
                var p = c.Bind<GitShowParams>();
                return await s.Git.ShowAsync(RpcCall.Require(p.Path, "path"), RpcCall.Require(p.Hash, "hash"), c.Ct);
            },

            ["git.publishRepo"] = async c =>
            {
                var p = c.Bind<PublishRepoParams>();
                var repo = RpcCall.Require(p.Path, "path");
                await s.Git.PublishRepoAsync(repo, string.IsNullOrWhiteSpace(p.Owner) ? null : p.Owner.Trim(), RpcCall.Require(p.Name, "name"), p.Private,
                    string.IsNullOrWhiteSpace(p.Description) ? null : p.Description.Trim(), c.Events, c.Ct);
                return await s.Projects.OpenAsync(repo, c.Ct);
            },

            ["git.githubOwners"] = async c => await s.Git.GithubOwnersAsync(c.Ct),

            ["watch.set"] = async c =>
            {
                var p = c.Bind<WatchSetParams>();
                return await s.Watch.SetAsync(RpcCall.Require(p.Path, "path"), p.Enabled, c.Ct);
            },
        };

        return methods;
    }

    private static string PathOf(RpcCall call)
    {
        return RpcCall.Require(call.Bind<PathParams>().Path, "path");
    }
}
