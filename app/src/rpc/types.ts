// RPC contract between the app and addonstudio-core (docs/SPEC.md section 7).
// JSON is camelCase, enums are camelCase strings, dates are ISO 8601 strings, workshop ids are strings,
// sizes are numbers in bytes. Paths inside a project are relative with forward slashes unless noted.

/* ---------- app, doctor, settings ---------- */

export type Os = "windows" | "macos" | "linux";
export type CheckStatus = "ok" | "warn" | "error";
export type DoctorCheckId = "os" | "steam" | "cs2" | "workshopTools" | "steamRunning" | "git" | "gitLfs" | "gh";
export type Theme = "dark" | "light" | "system";

export interface AppInfo {
  version: string;
  os: Os;
  canCompile: boolean;
  canPublish: boolean;
}

export interface DoctorCheck {
  id: DoctorCheckId;
  label: string;
  status: CheckStatus;
  detail: string;
  fix: string | null;
}

export interface DoctorReport {
  os: Os;
  canCompile: boolean;
  canPublish: boolean;
  cs2Path: string | null;
  checks: DoctorCheck[];
}

export interface RecentProject {
  path: string;
  addon: string;
  title: string;
  lastOpened: string;
  exists: boolean;
}

export interface AppSettings {
  cs2PathOverride: string | null;
  compileParallelism: number;
  compilerArgs: string;
  /** Pack size cap in bytes, default 3221225472 (3 GiB). */
  maxPackBytes: number;
  theme: Theme;
  recentProjects: RecentProject[];
}

/** settings.set params: any subset of AppSettings without recentProjects. cs2PathOverride: null clears it. */
export type AppSettingsPatch = Partial<Omit<AppSettings, "recentProjects">>;

/* ---------- projects ---------- */

export type Visibility = "public" | "friendsOnly" | "private" | "unlisted";

export interface PackConfig {
  include: string[];
  exclude: string[];
}

export interface AutoPublishConfig {
  enabled: boolean;
  /** Matched against the commit subject, group 1 = version. */
  pattern: string;
  githubRelease: boolean;
}

/** workshop.json */
export interface WorkshopConfig {
  addon: string;
  workshopId: string | null;
  title: string;
  description: string | null;
  visibility: Visibility;
  tags: string[];
  preview: string | null;
  pack: PackConfig;
  autoBuild: boolean;
  autoPublish: AutoPublishConfig;
}

export type LinkState = "linked" | "missing" | "conflict" | "wrongTarget" | "noCs2";

export interface LinkInfo {
  state: LinkState;
  linkPath: string | null;
  target: string | null;
  detail: string | null;
}

export interface ProjectGitInfo {
  isRepo: boolean;
  branch: string | null;
  remote: string | null;
  lfs: boolean;
}

/** Absolute paths (path, contentDir, prebuiltDir, buildDir) are in the OS's own form. */
export interface ProjectInfo {
  path: string;
  config: WorkshopConfig;
  configErrors: string[];
  contentDir: string;
  prebuiltDir: string;
  buildDir: string | null;
  link: LinkInfo;
  git: ProjectGitInfo;
}

export interface Cs2Addon {
  name: string;
  hasContent: boolean;
  hasGame: boolean;
  contentFiles: number;
  gameFiles: number;
  sizeBytes: number;
  linkedTo: string | null;
}

/* ---------- import ---------- */

export type ImportAction = "copySource" | "decompile" | "prebuilt" | "skip";

/**
 * path: relative to the CS2 folder it comes from, prefixed "content/" (the addon's content folder) or
 * "game/" (its build dir). target: repo-relative, null for skip.
 */
export interface ImportItem {
  path: string;
  size: number;
  action: ImportAction;
  target: string | null;
  reason: string | null;
}

export interface ImportPlan {
  addon: string;
  path: string;
  sourceFiles: number;
  items: ImportItem[];
  totalBytes: number;
  warnings: string[];
}

export interface ImportResult {
  project: ProjectInfo;
  copied: number;
  decompiled: number;
  prebuilt: number;
  skipped: number;
  backupPath: string | null;
  warnings: string[];
}

/* ---------- build ---------- */

export type RootKind = "panorama" | "texture" | "model" | "material" | "particle" | "sound" | "soundevents" | "vdata" | "map" | "other";
export type RootStatus = "new" | "changed" | "unchanged" | "deleted" | "unsupported" | "failed";
export type RootResult = "ok" | "warning" | "error";
export type PrebuiltStatus = "new" | "changed" | "unchanged" | "deleted";
export type ItemStatus = "ok" | "warning" | "error" | "skipped";
export type BuildMode = "changed" | "full" | "files";

/** source and dependencies are content-relative, outputs build-relative. */
export interface BuildRoot {
  source: string;
  kind: RootKind;
  status: RootStatus;
  reason: string | null;
  dependencies: string[];
  outputs: string[];
  lastResult: RootResult | null;
  lastBuiltAt: string | null;
}

export interface PrebuiltEntry {
  path: string;
  size: number;
  status: PrebuiltStatus;
}

export interface BuildConflict {
  path: string;
  source: string;
}

export interface BuildCounts {
  new: number;
  changed: number;
  unchanged: number;
  deleted: number;
  unsupported: number;
  failed: number;
  prebuiltChanged: number;
}

export interface BuildPlan {
  generatedAt: string;
  cs2Ready: boolean;
  buildDir: string | null;
  roots: BuildRoot[];
  prebuilt: PrebuiltEntry[];
  conflicts: BuildConflict[];
  orphans: string[];
  counts: BuildCounts;
}

export interface BuildItemResult {
  source: string;
  status: ItemStatus;
  durationMs: number;
  exitCode: number | null;
  outputs: string[];
  errors: string[];
  warnings: string[];
  /** capped 64 KB */
  log: string;
}

export interface BuildResult {
  ok: boolean;
  cancelled: boolean;
  durationMs: number;
  items: BuildItemResult[];
  prebuiltCopied: number;
  removed: string[];
}

export interface CleanResult {
  removed: number;
}

/* ---------- pack ---------- */

export type FileChange = "added" | "changed" | "same";
export type PackOrigin = "compiled" | "prebuilt" | "other";

/** path is build-relative. change is null when nothing was published yet. */
export interface PackFile {
  path: string;
  size: number;
  change: FileChange | null;
  origin: PackOrigin;
}

export interface PackTypeStat {
  label: string;
  count: number;
  size: number;
}

export interface PackLastPublish {
  at: string;
  workshopId: string;
  commit: string | null;
}

export interface PackChanges {
  added: number;
  changed: number;
  removed: number;
}

export interface PackContents {
  files: PackFile[];
  removed: string[];
  fileCount: number;
  totalSize: number;
  /** The maxPackBytes setting. */
  limitBytes: number;
  byType: PackTypeStat[];
  lastPublish: PackLastPublish | null;
  changes: PackChanges;
}

/** files are full paths. */
export interface PackExportResult {
  dir: string;
  files: string[];
  totalSize: number;
}

/* ---------- publish ---------- */

export interface PublishRequest {
  path: string;
  changeNote: string;
  version: string | null;
  skipBuild: boolean;
  push: boolean;
  githubRelease: boolean;
  allowSourceFolderChange: boolean;
}

export type PublishStage = "build" | "pack" | "upload" | "tag" | "push" | "release" | "done";

export interface PublishResult {
  workshopId: string;
  created: boolean;
  url: string;
  needsAgreement: boolean;
  tags: string[];
  releaseUrl: string | null;
  warnings: string[];
}

/* ---------- steam ---------- */

export type LiveState = "live" | "processing" | "unknown";

export interface SteamItem {
  workshopId: string;
  title: string;
  visibility: Visibility;
  timeCreated: string;
  timeUpdated: string;
  size: number;
  subscribers: number;
  favorites: number;
  views: number;
  previewUrl: string | null;
}

export interface SteamItemStatus {
  workshopId: string | null;
  found: boolean;
  title: string | null;
  visibility: Visibility | null;
  timeUpdated: string | null;
  size: number | null;
  banned: boolean;
  banReason: string | null;
  live: LiveState;
  lastPublishAt: string | null;
  /** Steam's publishedfiledetails object as sent, for fields we do not map yet. */
  raw: Record<string, unknown> | null;
}

/* ---------- git ---------- */

export type GitState =
  | "unmodified"
  | "modified"
  | "added"
  | "deleted"
  | "renamed"
  | "copied"
  | "untracked"
  | "ignored"
  | "conflicted"
  | "typeChanged";

/** path is repo-relative. */
export interface GitFileChange {
  path: string;
  origPath: string | null;
  index: GitState;
  worktree: GitState;
  staged: boolean;
  lfs: boolean;
  size: number | null;
}

export interface GitStatus {
  isRepo: boolean;
  branch: string | null;
  upstream: string | null;
  ahead: number;
  behind: number;
  files: GitFileChange[];
  merging: boolean;
  lfsAvailable: boolean;
}

export interface Deploy {
  tag: string;
  at: string;
  workshopId: string | null;
  note: string | null;
}

export interface GitCommit {
  hash: string;
  short: string;
  subject: string;
  body: string;
  authorName: string;
  date: string;
  parents: string[];
  refs: string[];
  tags: string[];
  version: string | null;
  deploys: Deploy[];
}

export interface GitCommitFile {
  path: string;
  status: GitState;
  additions: number | null;
  deletions: number | null;
  binary: boolean;
}

export interface GitCommitDetail {
  commit: GitCommit;
  files: GitCommitFile[];
}

export interface CommitRequest {
  path: string;
  message: string;
  /** Stage these first, null = commit what is staged. */
  files: string[] | null;
  push: boolean;
}

export interface AutoPublishMatch {
  version: string;
}

/** git.githubOwners: the account gh is logged in as and the orgs it belongs to. */
export interface GithubOwners {
  user: string;
  orgs: string[];
}

export interface CommitResult {
  hash: string;
  pushed: boolean;
  autoPublish: AutoPublishMatch | null;
  status: GitStatus;
}

/* ---------- params and small results ---------- */

export interface PathParams {
  path: string;
}

export interface WatchSetResult {
  enabled: boolean;
}

export interface CancelResult {
  cancelled: boolean;
}

/* ---------- events ---------- */

/** The id of the request an event belongs to (the id call() sent). */
export type RequestId = number;

export interface BuildStartedEvent {
  requestId: RequestId;
  path: string;
  /** roots that will be compiled */
  total: number;
}

export interface BuildItemStartedEvent {
  requestId: RequestId;
  source: string;
  /** 0-based */
  index: number;
  total: number;
}

export interface BuildLogEvent {
  requestId: RequestId;
  source: string;
  line: string;
}

export interface BuildItemEvent {
  requestId: RequestId;
  /** 0-based */
  index: number;
  total: number;
  item: BuildItemResult;
}

export interface BuildFinishedEvent {
  requestId: RequestId;
  result: BuildResult;
}

export interface PublishProgressEvent {
  requestId: RequestId;
  stage: PublishStage;
  /** 0..1 within the stage, null when unknown */
  fraction: number | null;
  message: string | null;
}

export interface GitProgressEvent {
  requestId: RequestId;
  phase: string;
  /** 0..100 */
  percent: number | null;
  line: string;
}

export interface ImportProgressEvent {
  requestId: RequestId;
  done: number;
  total: number;
  current: string;
}

/** Global event (no requestId). files are repo-relative. */
export interface WatchChangedEvent {
  path: string;
  files: string[];
}

export interface Events {
  "build.started": BuildStartedEvent;
  "build.itemStarted": BuildItemStartedEvent;
  "build.log": BuildLogEvent;
  "build.item": BuildItemEvent;
  "build.finished": BuildFinishedEvent;
  "publish.progress": PublishProgressEvent;
  "git.progress": GitProgressEvent;
  "import.progress": ImportProgressEvent;
  "watch.changed": WatchChangedEvent;
}

export type EventName = keyof Events;

/** An event as call()'s onEvent and subscribe() see it, narrowed by name. */
export type EventMessage<E extends EventName = EventName> = { [K in E]: { event: K; data: Events[K] } }[E];

export type BuildEventName = "build.started" | "build.itemStarted" | "build.log" | "build.item" | "build.finished";

/* ---------- methods ---------- */

/** Every method: its params (undefined = none) and result. */
export interface Methods {
  "app.info": { params: undefined; result: AppInfo };
  "env.doctor": { params: undefined; result: DoctorReport };
  "settings.get": { params: undefined; result: AppSettings };
  "settings.set": { params: AppSettingsPatch; result: AppSettings };
  "projects.list": { params: undefined; result: RecentProject[] };
  "projects.open": { params: PathParams; result: ProjectInfo };
  "projects.create": { params: { path: string; addon: string; title: string }; result: ProjectInfo };
  "projects.clone": { params: { url: string; path: string }; result: ProjectInfo };
  "projects.saveConfig": { params: { path: string; config: WorkshopConfig }; result: ProjectInfo };
  "projects.link": { params: { path: string; backup: boolean }; result: ProjectInfo };
  "projects.remove": { params: PathParams; result: RecentProject[] };
  "projects.cs2Addons": { params: undefined; result: Cs2Addon[] };
  "import.plan": { params: { addon: string; path: string; decompile: boolean }; result: ImportPlan };
  "import.run": { params: { addon: string; path: string; decompile: boolean }; result: ImportResult };
  "build.plan": { params: PathParams; result: BuildPlan };
  "build.run": { params: { path: string; mode: BuildMode; files?: string[] }; result: BuildResult };
  "build.clean": { params: PathParams; result: CleanResult };
  "pack.contents": { params: PathParams; result: PackContents };
  "pack.export": { params: { path: string; outDir?: string | null }; result: PackExportResult };
  "publish.run": { params: PublishRequest; result: PublishResult };
  "steam.items": { params: undefined; result: SteamItem[] };
  "steam.status": { params: PathParams; result: SteamItemStatus };
  "git.status": { params: PathParams; result: GitStatus };
  "git.stage": { params: { path: string; files: string[] }; result: GitStatus };
  "git.unstage": { params: { path: string; files: string[] }; result: GitStatus };
  "git.discard": { params: { path: string; files: string[] }; result: GitStatus };
  "git.commit": { params: CommitRequest; result: CommitResult };
  "git.push": { params: PathParams; result: GitStatus };
  "git.pull": { params: PathParams; result: GitStatus };
  "git.fetch": { params: PathParams; result: GitStatus };
  "git.log": { params: { path: string; limit: number; skip: number }; result: GitCommit[] };
  "git.show": { params: { path: string; hash: string }; result: GitCommitDetail };
  "git.publishRepo": {
    params: { path: string; owner: string | null; name: string; private: boolean; description: string | null };
    result: ProjectInfo;
  };
  "git.githubOwners": { params: undefined; result: GithubOwners };
  "watch.set": { params: { path: string; enabled: boolean }; result: WatchSetResult };
  "rpc.cancel": { params: { requestId: RequestId }; result: CancelResult };
}

export type MethodName = keyof Methods;
export type Params<M extends MethodName> = Methods[M]["params"];
export type Result<M extends MethodName> = Methods[M]["result"];

/** The events a long request emits with its requestId. */
export interface MethodEvents {
  "projects.clone": "git.progress";
  "import.run": "import.progress";
  "build.run": BuildEventName;
  /** publish runs a build first and pushes tags, so build and git events can arrive too */
  "publish.run": "publish.progress" | BuildEventName | "git.progress";
  "git.commit": "git.progress";
  "git.push": "git.progress";
  "git.pull": "git.progress";
  "git.fetch": "git.progress";
  "git.publishRepo": "git.progress";
}

export type EventsOf<M extends MethodName> = M extends keyof MethodEvents ? MethodEvents[M] : never;

/** Every method name, for checks against the core's list. */
export const METHOD_NAMES = [
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
  "rpc.cancel",
] as const satisfies readonly MethodName[];

/* ---------- errors and wire messages ---------- */

export type ErrorCode =
  | "invalid_params"
  | "not_found"
  | "not_windows"
  | "cs2_missing"
  | "tools_missing"
  | "steam_unavailable"
  | "git_failed"
  | "conflict"
  | "busy"
  | "cancelled"
  | "internal";

export interface RpcErrorBody {
  code: ErrorCode;
  message: string;
  details: unknown;
}

export interface RpcRequestMessage {
  id: RequestId;
  method: MethodName;
  params?: unknown;
}

export type RpcResponseMessage =
  | { id: RequestId; result: unknown; error?: undefined }
  | { id: RequestId; error: RpcErrorBody; result?: undefined };

export interface RpcEventMessage {
  event: EventName;
  data: (Record<string, unknown> & { requestId?: RequestId }) | null;
}

export type RpcIncomingMessage = RpcResponseMessage | RpcEventMessage;
