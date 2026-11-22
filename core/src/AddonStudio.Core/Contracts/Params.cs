using System.Text.Json;

namespace AddonStudio.Core.Contracts;

// RPC params records (SPEC section 7 "Params" column). The host binds them with Json.StrictOptions:
// a missing parameter without a default, or null for a non-nullable one, is invalid_params.
// PublishRequest and CommitRequest live with their areas.

/// <summary>{ path }</summary>
public sealed record PathParams(string Path);

/// <summary>projects.create { path, addon, title }</summary>
public sealed record CreateProjectParams(string Path, string Addon, string Title);

/// <summary>projects.clone { url, path }</summary>
public sealed record CloneProjectParams(string Url, string Path);

/// <summary>projects.saveConfig { path, config }</summary>
public sealed record SaveConfigParams(string Path, WorkshopConfig Config);

/// <summary>projects.link { path, backup }</summary>
public sealed record LinkParams(string Path, bool Backup = false);

/// <summary>import.plan / import.run { addon, path, decompile }. Path = the repo folder to create.</summary>
public sealed record ImportParams(string Addon, string Path, bool Decompile = false);

/// <summary>build.run { path, mode, files? }. Files = content-relative roots for mode "files".</summary>
public sealed record BuildRunParams(string Path, BuildMode Mode = BuildMode.Changed, IReadOnlyList<string>? Files = null);

/// <summary>pack.export { path, outDir? }</summary>
public sealed record PackExportParams(string Path, string? OutDir = null);

/// <summary>git.stage / git.unstage / git.discard { path, files }</summary>
public sealed record GitFilesParams(string Path, IReadOnlyList<string> Files);

/// <summary>git.log { path, limit, skip }</summary>
public sealed record GitLogParams(string Path, int Limit = 50, int Skip = 0);

/// <summary>git.show { path, hash }</summary>
public sealed record GitShowParams(string Path, string Hash);

/// <summary>git.publishRepo { path, owner, name, private, description }. owner null = the gh account itself.</summary>
public sealed record PublishRepoParams(string Path, string Name, bool Private = true, string? Owner = null, string? Description = null);

/// <summary>watch.set { path, enabled }</summary>
public sealed record WatchSetParams(string Path, bool Enabled);

/// <summary>watch.set result.</summary>
public sealed record WatchSetResult(bool Enabled);

/// <summary>rpc.cancel { requestId }. RequestId is the id of the request to cancel, as it was sent.</summary>
public sealed record CancelParams(JsonElement RequestId);

/// <summary>rpc.cancel result.</summary>
public sealed record CancelResult(bool Cancelled);
