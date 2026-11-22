// Mock handlers for one project's methods: git, build, pack, publish, steam, watch, saveConfig and link.
// State is kept per demo project (mock/demo.ts) and changes as you use the app, so the demo flows end to end.
import type {
  BuildItemResult,
  BuildMode,
  BuildPlan,
  BuildResult,
  BuildRoot,
  CommitRequest,
  CommitResult,
  Deploy,
  FileChange,
  GitCommit,
  GitCommitDetail,
  GitCommitFile,
  GitFileChange,
  GitStatus,
  GithubOwners,
  PackContents,
  PackSkippedFile,
  PackExportResult,
  PackFile,
  PathParams,
  PrebuiltEntry,
  ProjectInfo,
  PublishRequest,
  PublishResult,
  PublishStage,
  RootKind,
  SteamItemStatus,
  WorkshopConfig,
} from "../types";
import { CS2_PATH, demoGitStatus, demoProjects, findDemoProject } from "./demo";
import { MockError, delay, type MockContext, type MockHandler } from "./types";

const HUD = demoProjects[0]!.path;
const MODELS = demoProjects[1]!.path;
const GIB = 1024 ** 3;
const MIB = 1024 ** 2;
const KIB = 1024;
const MAX_PACK_BYTES = 3 * GIB;
const NOW = Date.now();
const HOUR = 3_600_000;
const DAY = 24 * HOUR;

/* ---------- small helpers ---------- */

function mulberry32(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const rand = mulberry32(730);
const pick = <T>(items: readonly T[]): T => items[Math.floor(rand() * items.length)]!;
const between = (min: number, max: number) => Math.round(min + rand() * (max - min));
const hex = (length: number) => Array.from({ length }, () => Math.floor(rand() * 16).toString(16)).join("");
const iso = (ms: number) => new Date(ms).toISOString();
const sleep = (ms: number, ctx: MockContext) => delay(ms, ctx.signal);

function stamp(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}${p(d.getUTCMonth() + 1)}${p(d.getUTCDate())}-${p(d.getUTCHours())}${p(d.getUTCMinutes())}${p(d.getUTCSeconds())}`;
}

function requireProject(path: string): ProjectInfo {
  const project = findDemoProject(path);
  if (!project) throw new MockError("not_found", `No addon project at ${path}.`);
  return project;
}

/* ---------- roots and outputs (SPEC 4 table) ---------- */

function kindOf(source: string): RootKind {
  const s = source.toLowerCase();
  if (s.endsWith(".vmap")) return "map";
  if (s.startsWith("panorama/images/") && /\.(png|jpe?g|tga|psd)$/.test(s)) return "texture";
  if (s.startsWith("panorama/") || s.endsWith(".svg")) return "panorama";
  if (s.endsWith(".vmdl")) return "model";
  if (s.endsWith(".vmat")) return "material";
  if (s.endsWith(".vtex")) return "texture";
  if (s.endsWith(".vpcf")) return "particle";
  if (s.endsWith(".vsndevts")) return "soundevents";
  if (s.startsWith("sounds/")) return "sound";
  if (s.endsWith(".vdata")) return "vdata";
  return "other";
}

function outputsOf(source: string): string[] {
  const lower = source.toLowerCase();
  if (lower.endsWith(".vmap")) return [];
  const image = /^(panorama\/images\/.+)\.(png|jpe?g|tga|psd)$/i.exec(source);
  if (image) return [`${image[1]}_${image[2]!.toLowerCase()}.vtex_c`];
  const swap = (ext: string) => source.replace(/\.[^.]+$/, ext);
  if (lower.endsWith(".xml")) return [swap(".vxml_c")];
  if (lower.endsWith(".css")) return [swap(".vcss_c")];
  if (lower.endsWith(".js")) return [swap(".vjs_c")];
  if (lower.endsWith(".svg")) return [swap(".vsvg_c")];
  if (/\.(wav|mp3)$/.test(lower)) return [swap(".vsnd_c")];
  return [`${source}_c`];
}

interface RootSeed {
  source: string;
  status: BuildRoot["status"];
  reason?: string;
  deps?: string[];
  lastResult?: BuildRoot["lastResult"];
}

function makeRoots(seeds: RootSeed[]): BuildRoot[] {
  return seeds.map((seed) => {
    const built = seed.status !== "new" && seed.status !== "unsupported";
    return {
      source: seed.source,
      kind: kindOf(seed.source),
      status: seed.status,
      reason: seed.reason ?? null,
      dependencies: seed.deps ?? [],
      outputs: outputsOf(seed.source),
      lastResult: seed.lastResult !== undefined ? seed.lastResult : built ? "ok" : null,
      lastBuiltAt: built ? iso(NOW - between(1, 9) * DAY - between(0, 20) * HOUR) : null,
    };
  });
}

function sizeFor(output: string): number {
  if (output.endsWith(".vxml_c")) return between(2, 12) * KIB + between(0, 900);
  if (output.endsWith(".vcss_c")) return between(3, 24) * KIB + between(0, 900);
  if (output.endsWith(".vjs_c")) return between(1, 10) * KIB + between(0, 900);
  if (output.endsWith(".vsvg_c")) return between(2, 9) * KIB + between(0, 900);
  if (output.endsWith(".vtex_c")) return between(40, 1400) * KIB;
  if (output.endsWith(".vsnd_c")) return between(60, 420) * KIB;
  if (output.endsWith(".vsndevts_c")) return between(1, 4) * KIB;
  if (output.endsWith(".vmat_c")) return between(1, 3) * KIB;
  if (output.endsWith(".vmdl_c")) return between(8, 60) * MIB;
  if (output.endsWith(".vpcf_c")) return between(4, 30) * KIB;
  return between(1, 50) * KIB;
}

/* ---------- per-project state ---------- */

interface ProjectState {
  status: GitStatus;
  commits: GitCommit[];
  files: Map<string, GitCommitFile[]>;
  roots: BuildRoot[];
  prebuilt: PrebuiltEntry[];
  conflicts: BuildPlan["conflicts"];
  orphans: string[];
  /** build-relative path -> size: what is in the build dir */
  sizes: Map<string, number>;
  /** build dir files the default pack mode leaves out */
  skipped?: PackSkippedFile[];
  lastPublish: { at: string; workshopId: string; commit: string | null; files: Map<string, number> } | null;
  /** Steam shows the new version after this time */
  processingUntil: number;
  steamUpdatedAt: string | null;
  cssBroken: boolean;
  conflictPending: boolean;
  remoteCommits: number;
  building: boolean;
  watchTimer: ReturnType<typeof setTimeout> | null;
}

const states = new Map<string, ProjectState>();

const HUD_SUBJECTS = [
  "Tweak money panel spacing",
  "Fix scoreboard avatars on 1440p",
  "Round timer: flash red under 10 seconds",
  "Kill feed icons for the new knives",
  "Bomb carrier marker on the radar",
  "Move health and armor closer to the corner",
  "Translate HUD strings to Hebrew",
  "Use the new coin icon",
  "Clean up old panel classes",
  "Fix team colors in spectator mode",
  "Add damage numbers toggle",
  "Smaller fonts on the scoreboard",
  "Round start sound",
  "MVP banner animation",
  "Fix CSS warning in hud.css",
  "Sound events for UI clicks",
];

const HUD_FILES = [
  "content/panorama/layout/custom_game/hud.xml",
  "content/panorama/layout/custom_game/scoreboard.xml",
  "content/panorama/layout/custom_game/round_timer.xml",
  "content/panorama/styles/custom_game/hud.css",
  "content/panorama/styles/custom_game/scoreboard.css",
  "content/panorama/scripts/custom_game/hud.js",
  "content/panorama/scripts/custom_game/money.js",
  "content/panorama/images/coin.png",
  "content/panorama/images/logo.png",
  "content/panorama/images/icons/bomb.svg",
  "content/soundevents/next_hud.vsndevts",
  "content/sounds/ui/round_start.wav",
  "workshop.json",
];

const MODEL_FILES = [
  "prebuilt/models/player/next/agent_t.vmdl_c",
  "prebuilt/models/weapons/next/knife_karambit.vmdl_c",
  "prebuilt/maps/next_aim.vpk",
  "content/materials/models/player/next/agent_t.vmat",
  "content/particles/next/muzzle.vpcf",
  "workshop.json",
];

const AUTHORS = ["Alex Cohen", "Sam Levi", "Noa Bar"];

function commitFiles(pool: string[]): GitCommitFile[] {
  const count = between(1, 4);
  const chosen = new Set<string>();
  while (chosen.size < count) chosen.add(pick(pool));
  return [...chosen].map((path) => {
    const binary = /\.(png|wav|psd|vmdl_c|vpk|tga)$/.test(path);
    return {
      path,
      status: rand() < 0.12 ? "added" : "modified",
      additions: binary ? null : between(1, 80),
      deletions: binary ? null : between(0, 40),
      binary,
    };
  });
}

function buildHistory(
  total: number,
  subjects: string[],
  pool: string[],
  versions: Record<number, string>,
  deploys: Record<number, { workshopId: string; note: string }>,
): { commits: GitCommit[]; files: Map<string, GitCommitFile[]> } {
  const commits: GitCommit[] = [];
  const files = new Map<string, GitCommitFile[]>();
  let time = NOW - 2 * HOUR;

  for (let index = 0; index < total; index++) {
    const hash = hex(40);
    const version = versions[index] ?? null;
    const deploy = deploys[index];
    const tags: string[] = [];
    const deployList: Deploy[] = [];
    if (deploy) {
      const at = Math.min(time + 50 * 60_000, NOW - 5 * 60_000);
      const tag = `deploy/${stamp(at)}`;
      tags.push(tag);
      deployList.push({ tag, at: iso(at), workshopId: deploy.workshopId, note: deploy.note });
    }
    if (version) tags.push(`v${version}`);
    commits.push({
      hash,
      short: hash.slice(0, 7),
      subject: version ? `v${version} ${deploy?.note ?? pick(subjects)}` : pick(subjects),
      body: rand() < 0.3 ? "Tested on a local listen server with 10 bots.\nNo layout changes on 16:10." : "",
      authorName: pick(AUTHORS),
      date: iso(time),
      parents: [],
      refs: [],
      tags,
      version,
      deploys: deployList,
    });
    files.set(hash, commitFiles(pool));
    time -= between(3, 30) * HOUR;
  }

  for (let index = 0; index < commits.length; index++) {
    const next = commits[index + 1];
    commits[index]!.parents = next ? [next.hash] : [];
  }
  return { commits, files };
}

function hudState(): ProjectState {
  const status = structuredClone(demoGitStatus[HUD]!);
  status.files.push(
    { path: "content/panorama/scripts/custom_game/money.js", origPath: null, index: "unmodified", worktree: "modified", staged: false, lfs: false, size: 3_120 },
    { path: "content/panorama/styles/custom_game/round_timer.css", origPath: null, index: "untracked", worktree: "untracked", staged: false, lfs: false, size: 1_890 },
    { path: "content/panorama/images/icons/old_c4.svg", origPath: null, index: "unmodified", worktree: "deleted", staged: false, lfs: false, size: null },
    { path: "content/source_art/hud_mockup.psd", origPath: null, index: "unmodified", worktree: "modified", staged: false, lfs: true, size: 48_211_337 },
    {
      path: "content/sounds/ui/round_start.wav",
      origPath: "content/sounds/ui/round_begin.wav",
      index: "renamed",
      worktree: "unmodified",
      staged: true,
      lfs: true,
      size: 412_880,
    },
  );

  const workshopId = demoProjects[0]!.config.workshopId ?? "3389501123";
  const history = buildHistory(
    64,
    HUD_SUBJECTS,
    HUD_FILES,
    { 3: "1.4.0", 14: "1.3.2", 27: "1.3.0", 45: "1.2.0" },
    {
      3: { workshopId, note: "Round timer and money HUD" },
      14: { workshopId, note: "Scoreboard fixes" },
    },
  );
  history.commits[0]!.refs = ["HEAD -> main"];
  history.commits[1]!.refs = ["origin/main", "origin/HEAD"];

  const roots = makeRoots([
    { source: "panorama/layout/custom_game/hud.xml", status: "changed", reason: "Source changed", deps: ["panorama/images/coin.png", "panorama/styles/custom_game/hud.css"] },
    { source: "panorama/layout/custom_game/scoreboard.xml", status: "unchanged", deps: ["panorama/styles/custom_game/scoreboard.css"] },
    { source: "panorama/layout/custom_game/round_timer.xml", status: "unchanged" },
    { source: "panorama/styles/custom_game/hud.css", status: "failed", reason: "Failed last time", lastResult: "error", deps: ["panorama/images/logo.png"] },
    { source: "panorama/styles/custom_game/scoreboard.css", status: "unchanged" },
    { source: "panorama/styles/custom_game/round_timer.css", status: "new", reason: "Never built" },
    { source: "panorama/scripts/custom_game/hud.js", status: "unchanged" },
    { source: "panorama/scripts/custom_game/money.js", status: "changed", reason: "Source changed" },
    { source: "panorama/images/coin.png", status: "new", reason: "Never built" },
    { source: "panorama/images/logo.png", status: "unchanged" },
    { source: "panorama/images/icons/bomb.svg", status: "unchanged" },
    { source: "panorama/images/icons/defuser.svg", status: "unchanged" },
    { source: "panorama/images/icons/old_c4.svg", status: "deleted", reason: "Source deleted, its outputs get removed" },
    { source: "soundevents/next_hud.vsndevts", status: "unchanged", deps: ["sounds/ui/round_start.wav", "sounds/ui/coin.wav"] },
    { source: "sounds/ui/coin.wav", status: "unchanged" },
    { source: "sounds/ui/round_start.wav", status: "changed", reason: "Renamed from round_begin.wav" },
    { source: "materials/hud/panel_bg.vmat", status: "unchanged", deps: ["materials/hud/panel_bg_color.tga"] },
  ]);

  const sizes = new Map<string, number>();
  for (const root of roots) {
    if (root.status === "new") continue;
    for (const output of root.outputs) sizes.set(output, sizeFor(output));
  }
  sizes.set("panorama/layout/custom_game/test_panel.vxml_c", 2_904);
  // rank badges and an emote model: outside Valve's custom_game whitelist, never on the Workshop before
  for (const rank of ["skillgroup1", "skillgroup2", "skillgroup3", "skillgroup_premier"]) {
    sizes.set(`panorama/images/icons/skillgroups/${rank}_png.vtex_c`, 18_400 + rank.length * 310);
  }
  sizes.set("characters/models/next/emote_dance.vmdl_c", 1_284_096);

  const publishedAt = Date.parse(history.commits[3]!.deploys[0]!.at);
  const published = new Map<string, number>();
  for (const [path, size] of sizes) {
    if (!path.includes("test_panel") && !path.includes("skillgroup") && !path.startsWith("characters/")) published.set(path, size);
  }
  published.set("panorama/images/icons/old_c4.vsvg_c", 6_210);
  // changed since the last publish
  sizes.set("panorama/layout/custom_game/hud.vxml_c", (sizes.get("panorama/layout/custom_game/hud.vxml_c") ?? 0) + 412);
  sizes.set("panorama/scripts/custom_game/money.vjs_c", (sizes.get("panorama/scripts/custom_game/money.vjs_c") ?? 0) + 96);

  return {
    status,
    commits: history.commits,
    files: history.files,
    roots,
    prebuilt: [{ path: "characters/models/next/emote_dance.vmdl_c", size: 1_284_096, status: "unchanged" }],
    conflicts: [],
    orphans: ["panorama/layout/custom_game/test_panel.vxml_c"],
    sizes,
    skipped: [
      { path: "panorama/images/Thumbs.db", size: 6_144, reason: "junk" },
      { path: "characters/models/next/emote_dance.fbx", size: 3_145_728, reason: "sourceOnly" },
    ],
    lastPublish: { at: iso(publishedAt), workshopId, commit: history.commits[3]!.hash, files: published },
    processingUntil: 0,
    steamUpdatedAt: iso(publishedAt + 95_000),
    cssBroken: true,
    conflictPending: true,
    remoteCommits: 1,
    building: false,
    watchTimer: null,
  };
}

function modelsState(): ProjectState {
  const status = structuredClone(demoGitStatus[MODELS]!);
  status.files.push(
    { path: "prebuilt/models/player/next/agent_t.vmdl_c", origPath: null, index: "unmodified", worktree: "modified", staged: false, lfs: true, size: 48_330_112 },
    { path: "content/particles/next/muzzle.vpcf", origPath: null, index: "untracked", worktree: "untracked", staged: false, lfs: false, size: 7_420 },
  );

  const history = buildHistory(9, ["Import from CS2", "Agent T model from the old item", "Karambit skin", "Aim map compiled in Hammer", "Muzzle flash particle"], MODEL_FILES, {}, {});
  history.commits[0]!.refs = ["HEAD -> main"];

  const roots = makeRoots([
    { source: "materials/models/player/next/agent_t.vmat", status: "unchanged", deps: ["materials/models/player/next/agent_t_color.tga", "materials/models/player/next/agent_t_normal.tga"] },
    { source: "models/player/next/agent_t.vmdl", status: "unchanged", deps: ["models/player/next/agent_t.fbx", "materials/models/player/next/agent_t.vmat"] },
    { source: "particles/next/muzzle.vpcf", status: "new", reason: "Never built" },
    { source: "maps/next_aim.vmap", status: "unsupported", reason: "Maps need Hammer, put the compiled map in prebuilt/" },
  ]);

  const prebuilt: PrebuiltEntry[] = [
    { path: "models/player/next/agent_t.vmdl_c", size: 48_330_112, status: "changed" },
    { path: "models/weapons/next/knife_karambit.vmdl_c", size: 22_904_330, status: "unchanged" },
    { path: "materials/models/weapons/next/karambit_color_png_9f2c1e0a.vtex_c", size: 67_109_000, status: "unchanged" },
    { path: "materials/models/weapons/next/karambit_normal_png_41aa07c3.vtex_c", size: 33_554_600, status: "unchanged" },
    { path: "maps/next_aim.vpk", size: Math.round(2.55 * GIB), status: "unchanged" },
    { path: "models/weapons/next/knife_bayonet.vmdl_c", size: 19_442_810, status: "new" },
  ];

  const sizes = new Map<string, number>();
  for (const root of roots) {
    if (root.status === "new" || root.status === "unsupported") continue;
    for (const output of root.outputs) sizes.set(output, sizeFor(output));
  }
  for (const entry of prebuilt) {
    if (entry.status !== "new") sizes.set(entry.path, entry.size);
  }
  sizes.set("models/player/next/agent_ct.vmdl_c", 41_200_000);

  return {
    status,
    commits: history.commits,
    files: history.files,
    roots,
    prebuilt,
    conflicts: [{ path: "models/player/next/agent_t.vmdl_c", source: "models/player/next/agent_t.vmdl" }],
    orphans: ["models/player/next/agent_ct.vmdl_c"],
    sizes,
    lastPublish: null,
    processingUntil: 0,
    steamUpdatedAt: null,
    cssBroken: false,
    conflictPending: false,
    remoteCommits: 0,
    building: false,
    watchTimer: null,
  };
}

/** A project made in this session (create, clone, import): a fresh repo with nothing built or published. */
function emptyState(project: ProjectInfo): ProjectState {
  return {
    status: {
      isRepo: project.git.isRepo,
      branch: project.git.branch ?? "main",
      upstream: null,
      ahead: 0,
      behind: 0,
      files: [],
      merging: false,
      lfsAvailable: true,
    },
    commits: [],
    files: new Map(),
    roots: [],
    prebuilt: [],
    conflicts: [],
    orphans: [],
    sizes: new Map(),
    lastPublish: null,
    processingUntil: 0,
    steamUpdatedAt: null,
    cssBroken: false,
    conflictPending: false,
    remoteCommits: 0,
    building: false,
    watchTimer: null,
  };
}

function state(project: ProjectInfo): ProjectState {
  let value = states.get(project.path);
  if (!value) {
    value = project.path === MODELS ? modelsState() : project.path === HUD ? hudState() : emptyState(project);
    states.set(project.path, value);
  }
  return value;
}

/* ---------- git ---------- */

function snapshot(s: ProjectState): GitStatus {
  return structuredClone(s.status);
}

function stageFiles(s: ProjectState, paths: string[]) {
  for (const file of s.status.files) {
    if (!paths.includes(file.path)) continue;
    if (file.worktree === "untracked") file.index = "added";
    else if (file.worktree !== "unmodified" && file.index !== "added") file.index = file.worktree;
    file.worktree = "unmodified";
    file.staged = true;
  }
}

function unstageFiles(s: ProjectState, paths: string[]) {
  for (const file of s.status.files) {
    if (!paths.includes(file.path) || !file.staged) continue;
    if (file.index === "added") {
      file.index = "untracked";
      file.worktree = "untracked";
    } else {
      if (file.worktree === "unmodified") file.worktree = file.index;
      file.index = "unmodified";
    }
    file.staged = false;
  }
}

async function gitProgress(ctx: MockContext, verb: "push" | "pull" | "fetch", lfsBytes = 0) {
  const steps: [string, number | null, string][] =
    verb === "push"
      ? [
          ["Enumerating objects", 100, "Enumerating objects: 14, done."],
          ["Counting objects", 100, "Counting objects: 100% (14/14), done."],
          ["Compressing objects", 100, "Compressing objects: 100% (8/8), done."],
          ["Writing objects", 40, "Writing objects:  40% (4/9)"],
          ["Writing objects", 100, "Writing objects: 100% (9/9), 2.31 KiB | 2.31 MiB/s, done."],
        ]
      : [
          ["Receiving objects", 30, "Receiving objects:  30% (3/10)"],
          ["Receiving objects", 100, "Receiving objects: 100% (10/10), 4.12 KiB | 1.03 MiB/s, done."],
          ["Resolving deltas", 100, "Resolving deltas: 100% (4/4), done."],
        ];
  if (lfsBytes > 0) {
    steps.unshift(
      ["Uploading LFS objects", 25, "Uploading LFS objects:  25% (1/4), 12 MB | 9.1 MB/s"],
      ["Uploading LFS objects", 100, `Uploading LFS objects: 100% (4/4), ${Math.max(1, Math.round(lfsBytes / MIB))} MB | 9.4 MB/s, done.`],
    );
  }
  for (const [phase, percent, line] of steps) {
    ctx.emit("git.progress", { phase, percent, line });
    await sleep(between(180, 420), ctx);
  }
}

function newCommit(s: ProjectState, message: string, author = "Alex Cohen"): GitCommit {
  const [subject = "", ...rest] = message.replace(/\r\n/g, "\n").split("\n");
  const hash = hex(40);
  const head = s.commits[0];
  if (head) head.refs = head.refs.filter((ref) => ref !== "HEAD -> main");
  const commit: GitCommit = {
    hash,
    short: hash.slice(0, 7),
    subject: subject.trim(),
    body: rest.join("\n").trim(),
    authorName: author,
    date: iso(Date.now()),
    parents: head ? [head.hash] : [],
    refs: ["HEAD -> main"],
    tags: [],
    version: null,
    deploys: [],
  };
  s.commits.unshift(commit);
  return commit;
}

function matchVersion(config: WorkshopConfig, subject: string): string | null {
  try {
    return new RegExp(config.autoPublish.pattern).exec(subject)?.[1] ?? null;
  } catch {
    return null;
  }
}

/* ---------- build ---------- */

function counts(s: ProjectState): BuildPlan["counts"] {
  const c = { new: 0, changed: 0, unchanged: 0, deleted: 0, unsupported: 0, failed: 0, prebuiltChanged: 0 };
  for (const root of s.roots) c[root.status]++;
  c.prebuiltChanged = s.prebuilt.filter((entry) => entry.status !== "unchanged").length;
  return c;
}

function plan(project: ProjectInfo, s: ProjectState): BuildPlan {
  return {
    generatedAt: iso(Date.now()),
    cs2Ready: true,
    buildDir: project.buildDir,
    roots: structuredClone(s.roots),
    prebuilt: structuredClone(s.prebuilt),
    conflicts: structuredClone(s.conflicts),
    orphans: [...s.orphans],
    counts: counts(s),
  };
}

function compileLog(project: ProjectInfo, root: BuildRoot, outcome: "ok" | "warning" | "error") {
  const addon = project.config.addon;
  const input = `${CS2_PATH}\\content\\csgo_addons\\${addon}\\${root.source.replace(/\//g, "\\")}`;
  const lines = [
    "Resource Compiler (resourcecompiler.exe) - Counter-Strike 2, build 10582 (Nov 18 2026)",
    `Initializing for game "csgo", addon "${addon}"`,
    `Compiling ${input}`,
  ];
  for (const dep of root.dependencies) lines.push(`  + dependency ${dep}`);
  const errors: string[] = [];
  const warnings: string[] = [];

  if (outcome === "error") {
    errors.push(
      `ERROR: ${root.source}(42): CSS parse error: unexpected '}' in declaration of 'box-shadow', expected ';'`,
      `ERROR: Failed to compile resource "${outputsOf(root.source)[0]!.replace(/_c$/, "")}"`,
    );
    lines.push(
      `  panorama: parsing stylesheet ${root.source}`,
      `  ${root.source}(41):     box-shadow: 0px 2px 8px #00000080`,
      `  ${root.source}(42):   }`,
      ...errors,
      "1 of 1 resources failed to compile (0.38 s)",
    );
    return { lines, errors, warnings };
  }

  if (outcome === "warning") {
    warnings.push(`WARNING: ${root.source}: texture is 300x300, not a power of two, it gets resized to 256x256`);
    lines.push(...warnings);
  }
  for (const output of root.outputs) lines.push(`  -> game\\csgo_addons\\${addon}\\${output.replace(/\//g, "\\")}`);
  lines.push(`1 resource compiled (${(rand() * 1.4 + 0.2).toFixed(2)} s)`);
  return { lines, errors, warnings };
}

function rootsFor(s: ProjectState, mode: BuildMode, files?: string[]): BuildRoot[] {
  const buildable = s.roots.filter((root) => root.status !== "deleted" && root.status !== "unsupported");
  if (mode === "full") return buildable;
  if (mode === "files") {
    const wanted = new Set((files ?? []).map((file) => file.toLowerCase()));
    return buildable.filter((root) => wanted.has(root.source.toLowerCase()));
  }
  return buildable.filter((root) => root.status === "new" || root.status === "changed" || root.status === "failed");
}

async function runBuild(project: ProjectInfo, mode: BuildMode, files: string[] | undefined, ctx: MockContext): Promise<BuildResult> {
  const s = state(project);
  if (s.building) throw new MockError("busy", "A build is already running for this project.");
  s.building = true;
  const started = Date.now();

  try {
    const targets = rootsFor(s, mode, files);
    ctx.emit("build.started", { path: project.path, total: targets.length });
    const items: BuildItemResult[] = [];

    for (const [index, root] of targets.entries()) {
      ctx.emit("build.itemStarted", { source: root.source, index, total: targets.length });
      const itemStart = Date.now();
      const outcome: "ok" | "warning" | "error" =
        root.source.endsWith("custom_game/hud.css") && s.cssBroken ? "error" : root.source.endsWith("coin.png") ? "warning" : "ok";
      const log = compileLog(project, root, outcome);
      for (const line of log.lines) {
        await sleep(between(40, 140), ctx);
        ctx.emit("build.log", { source: root.source, line });
      }
      await sleep(root.kind === "texture" || root.kind === "sound" ? between(400, 900) : between(120, 380), ctx);

      const item: BuildItemResult = {
        source: root.source,
        status: outcome,
        durationMs: Date.now() - itemStart,
        exitCode: outcome === "error" ? 1 : 0,
        outputs: outcome === "error" ? [] : root.outputs,
        errors: log.errors,
        warnings: log.warnings,
        log: log.lines.join("\r\n"),
      };
      items.push(item);
      ctx.emit("build.item", { index, total: targets.length, item });

      root.lastResult = outcome;
      root.lastBuiltAt = iso(Date.now());
      root.status = outcome === "error" ? "failed" : "unchanged";
      root.reason = outcome === "error" ? "Failed last time" : null;
      if (outcome !== "error") for (const output of root.outputs) s.sizes.set(output, s.sizes.get(output) ?? sizeFor(output));
      // the next try works, as if the file was fixed in the meantime
      if (outcome === "error") s.cssBroken = false;
    }

    const removed: string[] = [];
    let prebuiltCopied = 0;
    if (mode !== "files") {
      for (const root of s.roots.filter((entry) => entry.status === "deleted")) {
        for (const output of root.outputs) {
          s.sizes.delete(output);
          removed.push(output);
        }
      }
      s.roots = s.roots.filter((root) => root.status !== "deleted");
      for (const entry of s.prebuilt) {
        if (entry.status === "unchanged") continue;
        if (entry.status === "deleted") {
          s.sizes.delete(entry.path);
          removed.push(entry.path);
        } else {
          s.sizes.set(entry.path, entry.size);
          prebuiltCopied++;
        }
        entry.status = "unchanged";
      }
      s.prebuilt = s.prebuilt.filter((entry) => !removed.includes(entry.path));
    }

    const result: BuildResult = {
      ok: items.every((item) => item.status !== "error"),
      cancelled: false,
      durationMs: Date.now() - started,
      items,
      prebuiltCopied,
      removed,
    };
    ctx.emit("build.finished", { result });
    return result;
  } finally {
    s.building = false;
  }
}

/* ---------- pack ---------- */

function typeLabel(path: string): string {
  const p = path.toLowerCase();
  if (p.endsWith(".vxml_c")) return "Layouts";
  if (p.endsWith(".vcss_c")) return "Styles";
  if (p.endsWith(".vjs_c")) return "Scripts";
  if (p.endsWith(".vsvg_c")) return "SVG images";
  if (p.endsWith(".vtex_c")) return "Textures";
  if (p.endsWith(".vsnd_c")) return "Sounds";
  if (p.endsWith(".vsndevts_c")) return "Sound events";
  if (p.endsWith(".vmat_c")) return "Materials";
  if (p.endsWith(".vmdl_c")) return "Models";
  if (p.endsWith(".vpcf_c")) return "Particles";
  if (p.endsWith(".vpk")) return "Maps";
  return "Other";
}

/** Roughly what CS2's gameinfo.gi AddonConfig/VpkDirectories lets through (the real list comes from the game). */
const VALVE_WHITELIST = [
  "maps/",
  "materials/",
  "models/",
  "particles/",
  "sounds/",
  "soundevents/",
  "panorama/images/custom_game/",
  "panorama/images/overheadmaps/",
  "panorama/images/map_icons/",
  "panorama/layout/custom_game/",
  "panorama/scripts/custom_game/",
  "panorama/styles/custom_game/",
];

const inValveWhitelist = (path: string) => VALVE_WHITELIST.some((prefix) => path.toLowerCase().startsWith(prefix));

function packContents(project: ProjectInfo, s: ProjectState): PackContents {
  const prebuiltPaths = new Set(s.prebuilt.map((entry) => entry.path));
  const compiled = new Set(s.roots.flatMap((root) => root.outputs));
  const valveWhitelist = project.config.pack.valveWhitelist;
  const files: PackFile[] = [];
  const whitelistDropped: PackContents["whitelistDropped"] = [];

  for (const [path, size] of s.sizes) {
    if (!inValveWhitelist(path)) {
      whitelistDropped.push({ path, size });
      if (valveWhitelist) continue;
    }
    let change: FileChange | null = null;
    if (s.lastPublish) {
      const before = s.lastPublish.files.get(path);
      change = before === undefined ? "added" : before === size ? "same" : "changed";
    }
    files.push({ path, size, change, origin: compiled.has(path) ? "compiled" : prebuiltPaths.has(path) ? "prebuilt" : "other" });
  }
  files.sort((a, b) => a.path.localeCompare(b.path));
  whitelistDropped.sort((a, b) => a.path.localeCompare(b.path));

  const packed = new Set(files.map((file) => file.path));
  const removed = s.lastPublish ? [...s.lastPublish.files.keys()].filter((path) => !packed.has(path)) : [];
  const types = new Map<string, { count: number; size: number }>();
  for (const file of files) {
    const label = typeLabel(file.path);
    const entry = types.get(label) ?? { count: 0, size: 0 };
    entry.count++;
    entry.size += file.size;
    types.set(label, entry);
  }

  return {
    files,
    removed,
    fileCount: files.length,
    totalSize: files.reduce((sum, file) => sum + file.size, 0),
    limitBytes: MAX_PACK_BYTES,
    byType: [...types.entries()].map(([label, value]) => ({ label, ...value })).sort((a, b) => b.size - a.size),
    lastPublish: s.lastPublish ? { at: s.lastPublish.at, workshopId: s.lastPublish.workshopId, commit: s.lastPublish.commit } : null,
    changes: {
      added: files.filter((file) => file.change === "added").length,
      changed: files.filter((file) => file.change === "changed").length,
      removed: removed.length,
    },
    valveWhitelist,
    whitelistDropped,
    skipped: s.skipped ?? [],
  };
}

function chunkNames(prefix: string, totalSize: number): string[] {
  const chunks = Math.max(1, Math.ceil(totalSize / (100 * MIB)));
  return [`${prefix}_dir.vpk`, ...Array.from({ length: chunks }, (_, index) => `${prefix}_${String(index).padStart(3, "0")}.vpk`), "publish_data.txt"];
}

/* ---------- steam ---------- */

function steamStatus(project: ProjectInfo, s: ProjectState): SteamItemStatus {
  const workshopId = project.config.workshopId;
  const lastPublishAt = s.lastPublish?.at ?? null;
  if (!workshopId) {
    return {
      workshopId: null,
      found: false,
      title: null,
      visibility: null,
      timeUpdated: null,
      size: null,
      banned: false,
      banReason: null,
      live: "unknown",
      lastPublishAt,
      raw: null,
    };
  }

  const processing = Date.now() < s.processingUntil;
  if (!processing && s.processingUntil > 0 && lastPublishAt) {
    s.steamUpdatedAt = iso(Date.parse(lastPublishAt) + 40_000);
    s.processingUntil = 0;
  }
  const timeUpdated = s.steamUpdatedAt;
  const size = packContents(project, s).totalSize;
  const visibilityCode = { public: 0, friendsOnly: 1, private: 2, unlisted: 3 }[project.config.visibility];
  const live = processing ? "processing" : timeUpdated && lastPublishAt && timeUpdated >= lastPublishAt ? "live" : "unknown";

  return {
    workshopId,
    found: true,
    title: project.config.title,
    visibility: project.config.visibility,
    timeUpdated,
    size,
    banned: false,
    banReason: null,
    live,
    lastPublishAt,
    raw: {
      publishedfileid: workshopId,
      result: 1,
      creator: "76561198012345678",
      creator_app_id: 730,
      consumer_app_id: 730,
      filename: "",
      file_size: String(size),
      file_url: "",
      hcontent_file: "8124590331276540112",
      preview_url: `https://images.steamusercontent.com/ugc/2462981230984/8A1F04C2E7B${workshopId.slice(-4)}/`,
      hcontent_preview: "2462981230984113870",
      title: project.config.title,
      description: project.config.description ?? "Custom HUD for community servers: money, round timer, scoreboard and kill feed.",
      time_created: Math.floor((NOW - 160 * DAY) / 1000),
      time_updated: timeUpdated ? Math.floor(Date.parse(timeUpdated) / 1000) : 0,
      visibility: visibilityCode,
      banned: 0,
      ban_reason: "",
      subscriptions: 1834,
      favorited: 212,
      lifetime_subscriptions: 2410,
      lifetime_favorited: 260,
      views: 9812,
      tags: project.config.tags.map((tag) => ({ tag })),
    },
  };
}

/* ---------- publish ---------- */

async function stage(ctx: MockContext, name: PublishStage, message: string | null, fraction: number | null = null) {
  ctx.emit("publish.progress", { stage: name, fraction, message });
  await sleep(between(250, 500), ctx);
}

async function publish(req: PublishRequest, ctx: MockContext): Promise<PublishResult> {
  const project = requireProject(req.path);
  const s = state(project);
  const warnings: string[] = [];
  const hasRemote = project.git.remote !== null;

  if (s.conflictPending && project.config.workshopId && !req.allowSourceFolderChange) {
    await sleep(300, ctx);
    throw new MockError(
      "conflict",
      `Workshop item ${project.config.workshopId} was last published from addon "${project.config.addon}_old", not "${project.config.addon}", change AllowSourceFolderChange to allow this.`,
      { workshopId: project.config.workshopId, previousAddon: `${project.config.addon}_old`, addon: project.config.addon },
    );
  }
  s.conflictPending = false;

  if (!req.skipBuild) {
    await stage(ctx, "build", "Building changed files");
    const result = await runBuild(project, "changed", undefined, ctx);
    const failed = result.items.filter((item) => item.status === "error");
    if (failed.length > 0) {
      throw new MockError("internal", `The build has errors in ${failed.length === 1 ? "1 file" : `${failed.length} files`}, nothing was uploaded.`, {
        failed: failed.map((item) => item.source),
      });
    }
  }

  const contents = packContents(project, s);
  for (let step = 0; step <= 4; step++) await stage(ctx, "pack", `Packing ${contents.fileCount} files`, step / 4);

  const totalMb = Math.max(1, Math.round(contents.totalSize / MIB));
  await stage(ctx, "upload", "Preparing config", 0);
  await stage(ctx, "upload", "Preparing content", 0.02);
  for (let step = 1; step <= 10; step++) {
    ctx.emit("publish.progress", { stage: "upload", fraction: 0.05 + (step / 10) * 0.85, message: `Uploading content (${Math.round((step / 10) * totalMb)} of ${totalMb} MB)` });
    await sleep(between(220, 420), ctx);
  }
  await stage(ctx, "upload", "Uploading preview", 0.94);
  await stage(ctx, "upload", "Committing changes", 0.99);

  const created = project.config.workshopId === null;
  const workshopId = project.config.workshopId ?? "3401887654";
  if (created) project.config.workshopId = workshopId;

  const now = Date.now();
  const head = s.commits[0];
  const tags: string[] = [];
  if (head) {
    const deployTag = `deploy/${stamp(now)}`;
    await stage(ctx, "tag", deployTag);
    tags.push(deployTag);
    head.tags.push(deployTag);
    head.deploys.unshift({ tag: deployTag, at: iso(now), workshopId, note: req.changeNote.split("\n")[0] ?? null });
    if (req.version) {
      const versionTag = `v${req.version}`;
      if (s.commits.some((commit) => commit.tags.includes(versionTag))) {
        warnings.push(`Tag ${versionTag} already exists, skipped it.`);
      } else {
        tags.push(versionTag);
        head.tags.push(versionTag);
        head.version = req.version;
      }
    }
  } else {
    warnings.push("The repo has no commits yet, so nothing was tagged.");
  }

  if (req.push) {
    if (hasRemote) {
      await stage(ctx, "push", "Pushing tags to origin");
      await gitProgress(ctx, "push");
    } else {
      warnings.push("No remote is set up, the tags were not pushed.");
    }
  }

  let releaseUrl: string | null = null;
  if (req.githubRelease) {
    await stage(ctx, "release", "Creating the GitHub release");
    if (!hasRemote) warnings.push("No GitHub remote, skipped the release.");
    else if (!req.version) warnings.push("A GitHub release needs a version, skipped it.");
    else releaseUrl = `${project.git.remote!.replace(/\.git$/, "")}/releases/tag/v${req.version}`;
  }

  await stage(ctx, "done", "Published", 1);

  s.lastPublish = { at: iso(now), workshopId, commit: head?.hash ?? null, files: new Map(contents.files.map((file) => [file.path, file.size])) };
  s.processingUntil = Date.now() + 25_000;

  return {
    workshopId,
    created,
    url: `https://steamcommunity.com/sharedfiles/filedetails/?id=${workshopId}`,
    needsAgreement: created,
    tags,
    releaseUrl,
    warnings,
  };
}

/* ---------- handlers ---------- */

export const handlers: Record<string, MockHandler> = {
  /* git */
  "git.status": async ({ path }: PathParams): Promise<GitStatus> => snapshot(state(requireProject(path))),

  "git.stage": async ({ path, files }: { path: string; files: string[] }): Promise<GitStatus> => {
    const s = state(requireProject(path));
    stageFiles(s, files);
    return snapshot(s);
  },

  "git.unstage": async ({ path, files }: { path: string; files: string[] }): Promise<GitStatus> => {
    const s = state(requireProject(path));
    unstageFiles(s, files);
    return snapshot(s);
  },

  "git.discard": async ({ path, files }: { path: string; files: string[] }): Promise<GitStatus> => {
    const s = state(requireProject(path));
    s.status.files = s.status.files.flatMap((file): GitFileChange[] => {
      if (!files.includes(file.path)) return [file];
      return file.staged ? [{ ...file, worktree: "unmodified" }] : [];
    });
    return snapshot(s);
  },

  "git.commit": async (req: CommitRequest, ctx): Promise<CommitResult> => {
    const project = requireProject(req.path);
    const s = state(project);
    if (req.message.trim() === "") throw new MockError("invalid_params", "The commit message is empty.");
    if (req.files) stageFiles(s, req.files);
    const staged = s.status.files.filter((file) => file.staged);
    if (staged.length === 0) throw new MockError("git_failed", "Nothing to commit. Stage some files first.");

    await sleep(250, ctx);
    const commit = newCommit(s, req.message);
    s.files.set(
      commit.hash,
      staged.map((file) => ({
        path: file.path,
        status: file.index,
        additions: file.lfs ? null : between(1, 60),
        deletions: file.lfs ? null : between(0, 20),
        binary: file.lfs,
      })),
    );
    s.status.files = s.status.files.flatMap((file): GitFileChange[] => {
      if (!file.staged) return [file];
      return file.worktree === "unmodified" ? [] : [{ ...file, index: "unmodified", staged: false }];
    });
    s.status.ahead++;

    let pushed = false;
    if (req.push && project.git.remote && s.status.behind === 0) {
      await gitProgress(ctx, "push", staged.filter((file) => file.lfs).reduce((sum, file) => sum + (file.size ?? 0), 0));
      s.status.ahead = 0;
      pushed = true;
    }

    const version = project.config.autoPublish.enabled ? matchVersion(project.config, commit.subject) : null;
    if (version) commit.version = version;
    return { hash: commit.hash, pushed, autoPublish: version ? { version } : null, status: snapshot(s) };
  },

  "git.push": async ({ path }: PathParams, ctx): Promise<GitStatus> => {
    const project = requireProject(path);
    const s = state(project);
    if (!project.git.remote) throw new MockError("git_failed", "No remote is set up for this repo. Use Create private GitHub repo, or add one by hand.");
    if (s.status.behind > 0) throw new MockError("git_failed", "The remote has commits you do not have yet. Pull first, then push.");
    await gitProgress(ctx, "push");
    s.status.ahead = 0;
    return snapshot(s);
  },

  "git.pull": async ({ path }: PathParams, ctx): Promise<GitStatus> => {
    const project = requireProject(path);
    const s = state(project);
    if (!project.git.remote) throw new MockError("git_failed", "No remote is set up for this repo.");
    await gitProgress(ctx, "pull");
    if (s.remoteCommits > 0) {
      const head = s.commits[0];
      const remote = newCommit(s, "Fix kill feed overlap with the radar", "Sam Levi");
      if (head) remote.parents = [head.hash];
      remote.refs = ["HEAD -> main", "origin/main"];
      s.files.set(remote.hash, [{ path: "content/panorama/styles/custom_game/hud.css", status: "modified", additions: 6, deletions: 2, binary: false }]);
      s.remoteCommits = 0;
    }
    s.status.behind = 0;
    return snapshot(s);
  },

  "git.fetch": async ({ path }: PathParams, ctx): Promise<GitStatus> => {
    const project = requireProject(path);
    const s = state(project);
    if (!project.git.remote) throw new MockError("git_failed", "No remote is set up for this repo.");
    await gitProgress(ctx, "fetch");
    s.status.behind = s.remoteCommits;
    return snapshot(s);
  },

  "git.log": async ({ path, limit, skip }: { path: string; limit: number; skip: number }): Promise<GitCommit[]> => {
    const s = state(requireProject(path));
    return structuredClone(s.commits.slice(skip, skip + limit));
  },

  "git.show": async ({ path, hash }: { path: string; hash: string }): Promise<GitCommitDetail> => {
    const s = state(requireProject(path));
    const commit = s.commits.find((entry) => entry.hash === hash || entry.short === hash);
    if (!commit) throw new MockError("not_found", `No commit ${hash}.`);
    return { commit: structuredClone(commit), files: structuredClone(s.files.get(commit.hash) ?? []) };
  },

  "git.githubOwners": async (_params: undefined, ctx): Promise<GithubOwners> => {
    await sleep(400, ctx);
    return { user: "you", orgs: ["example-community", "example-studio"] };
  },

  "git.publishRepo": async (
    { path, owner, name }: { path: string; owner: string | null; name: string; private: boolean; description: string | null },
    ctx,
  ): Promise<ProjectInfo> => {
    const project = requireProject(path);
    const s = state(project);
    if (!/^[A-Za-z0-9._-]{1,100}$/.test(name)) throw new MockError("invalid_params", "Use letters, digits, . _ or - for the repo name.");
    if (project.git.remote) throw new MockError("conflict", `This repo already has a remote (${project.git.remote}).`);
    await sleep(600, ctx);
    await gitProgress(ctx, "push", 120 * MIB);
    project.git.remote = `https://github.com/${owner ?? "you"}/${name}.git`;
    s.status.upstream = "origin/main";
    s.status.ahead = 0;
    s.status.behind = 0;
    return structuredClone(project);
  },

  /* build */
  "build.plan": async ({ path }: PathParams): Promise<BuildPlan> => {
    const project = requireProject(path);
    return plan(project, state(project));
  },

  "build.run": async ({ path, mode, files }: { path: string; mode: BuildMode; files?: string[] }, ctx): Promise<BuildResult> =>
    runBuild(requireProject(path), mode, files, ctx),

  "build.clean": async ({ path }: PathParams, ctx): Promise<{ removed: number }> => {
    const project = requireProject(path);
    const s = state(project);
    if (s.building) throw new MockError("busy", "A build is running for this project.");
    await sleep(400, ctx);
    const removed = s.sizes.size;
    s.sizes.clear();
    s.orphans = [];
    s.roots = s.roots.filter((root) => root.status !== "deleted");
    for (const root of s.roots) {
      if (root.status === "unsupported") continue;
      root.status = "new";
      root.reason = "The build folder was cleaned";
      root.lastResult = null;
      root.lastBuiltAt = null;
    }
    for (const entry of s.prebuilt) entry.status = "new";
    return { removed };
  },

  /* pack */
  "pack.contents": async ({ path }: PathParams): Promise<PackContents> => {
    const project = requireProject(path);
    return packContents(project, state(project));
  },

  "pack.export": async ({ path, outDir }: { path: string; outDir?: string | null }, ctx): Promise<PackExportResult> => {
    const project = requireProject(path);
    const contents = packContents(project, state(project));
    await sleep(900, ctx);
    const dir = outDir ?? `${project.path}\\.addonstudio\\export\\${stamp(Date.now())}`;
    const prefix = project.config.workshopId ?? project.config.addon;
    return { dir, files: chunkNames(prefix, contents.totalSize).map((name) => `${dir}\\${name}`), totalSize: contents.totalSize };
  },

  /* publish and steam */
  "publish.run": async (req: PublishRequest, ctx) => publish(req, ctx),

  "steam.status": async ({ path }: PathParams, ctx): Promise<SteamItemStatus> => {
    const project = requireProject(path);
    await sleep(250, ctx);
    return steamStatus(project, state(project));
  },

  /* watch */
  "watch.set": async ({ path, enabled }: { path: string; enabled: boolean }, ctx) => {
    const project = requireProject(path);
    const s = state(project);
    if (s.watchTimer) clearTimeout(s.watchTimer);
    s.watchTimer = null;
    if (enabled && project.path === HUD) {
      // one save in the editor a few seconds after watching starts, so auto-build has something to do
      s.watchTimer = setTimeout(() => {
        s.watchTimer = null;
        const root = s.roots.find((entry) => entry.source.endsWith("custom_game/hud.css"));
        if (root && root.status === "unchanged") {
          root.status = "changed";
          root.reason = "Source changed";
        }
        s.cssBroken = false;
        const file = "content/panorama/styles/custom_game/hud.css";
        if (!s.status.files.some((entry) => entry.path === file)) {
          s.status.files.push({ path: file, origPath: null, index: "unmodified", worktree: "modified", staged: false, lfs: false, size: 8_844 });
        }
        ctx.emitGlobal("watch.changed", { path: project.path, files: [file] });
      }, 9_000);
    }
    return { enabled };
  },

  /* project config and link */
  "projects.saveConfig": async ({ path, config }: { path: string; config: WorkshopConfig }): Promise<ProjectInfo> => {
    const project = requireProject(path);
    if (!/^[a-z0-9_]{1,64}$/.test(config.addon)) throw new MockError("invalid_params", "addon must match ^[a-z0-9_]{1,64}$.");
    if (config.title.trim() === "") throw new MockError("invalid_params", "title is empty.");
    project.config = structuredClone(config);
    project.configErrors = [];
    return structuredClone(project);
  },

  "projects.link": async ({ path, backup }: { path: string; backup: boolean }): Promise<ProjectInfo> => {
    const project = requireProject(path);
    if (project.link.state === "conflict" && !backup) {
      throw new MockError("conflict", "A real folder is in the way. Link with backup to rename it first.");
    }
    const linkPath = project.link.linkPath ?? `${CS2_PATH}\\content\\csgo_addons\\${project.config.addon}`;
    project.link = {
      state: "linked",
      linkPath,
      target: project.contentDir,
      detail: backup ? `Moved the old folder to ${linkPath}.addonstudio-backup-${stamp(Date.now())}` : null,
    };
    return structuredClone(project);
  },
};
