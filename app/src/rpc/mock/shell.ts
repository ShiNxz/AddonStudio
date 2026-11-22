// Mock handlers for app-wide methods: app, doctor, settings, the project list, create/clone/import, Steam items.
// Data lines up with mock/demo.ts: a Windows PC, the Next HUD addon (linked) and the 2.8 GB Next Models addon whose
// CS2 content folder is a real folder in the way (conflict), plus two addons that are not projects yet.
// Add ?nocompile to the dev URL to see the app as it looks on a PC that can not compile (first-run banner).
import type {
  AppInfo,
  AppSettings,
  AppSettingsPatch,
  Cs2Addon,
  DoctorReport,
  ImportAction,
  ImportItem,
  ImportPlan,
  ImportResult,
  PathParams,
  ProjectInfo,
  RecentProject,
  SteamItem,
  WorkshopConfig,
} from "../types";
import { ADDONS_DIR, CS2_PATH, demoAppInfo, demoDoctor, demoGitStatus, demoProjects, demoRecent, demoSettings, findDemoProject } from "./demo";
import { MockError, delay, type MockContext, type MockHandler } from "./types";

const GIB = 1024 ** 3;
const MIB = 1024 ** 2;
const KIB = 1024;
const DEFAULT_MAX_PACK_BYTES = 3 * GIB;

let settings: AppSettings = { ...structuredClone(demoSettings), maxPackBytes: demoSettings.maxPackBytes || DEFAULT_MAX_PACK_BYTES };
let recent: RecentProject[] = structuredClone(demoRecent);

const contentLink = (addon: string) => `${CS2_PATH}\\content\\csgo_addons\\${addon}`;
const gameDir = (addon: string) => `${CS2_PATH}\\game\\csgo_addons\\${addon}`;

function noCompile(): boolean {
  return typeof location !== "undefined" && new URLSearchParams(location.search).has("nocompile");
}

/* ---------- deterministic randomness, so the demo looks the same on every reload ---------- */

function seeded(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function stamp(date = new Date()): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}-${p(date.getHours())}${p(date.getMinutes())}${p(date.getSeconds())}`;
}

/* ---------- CS2 addons on disk ---------- */

interface AddonSeed {
  name: string;
  title: string;
  /** content-relative source files */
  sources: { path: string; size: number }[];
  /** build-relative compiled files with no source (models from other items, maps, ...) */
  extraGame: { path: string; size: number }[];
}

function modelsSeed(): AddonSeed {
  const rand = seeded(2_800);
  const sources: AddonSeed["sources"] = [];
  const extraGame: AddonSeed["extraGame"] = [];
  const agents = ["ct_sas_desert", "ct_gign_night", "t_phoenix_urban", "t_leet_arctic", "t_balkan_forest", "ct_swat_heavy"];
  for (const agent of agents) {
    sources.push({ path: `models/player/custom/${agent}/${agent}.vmdl`, size: 6 * KIB + Math.round(rand() * 4 * KIB) });
    sources.push({ path: `models/player/custom/${agent}/${agent}.fbx`, size: Math.round((18 + rand() * 30) * MIB) });
    for (const part of ["body", "head", "gloves"]) {
      sources.push({ path: `materials/player/custom/${agent}/${part}.vmat`, size: 1 * KIB + Math.round(rand() * KIB) });
      sources.push({ path: `materials/player/custom/${agent}/${part}_color.tga`, size: Math.round((8 + rand() * 24) * MIB) });
      sources.push({ path: `materials/player/custom/${agent}/${part}_normal.tga`, size: Math.round((8 + rand() * 24) * MIB) });
    }
  }
  // weapon models taken from other Workshop items: compiled only, they go to prebuilt/
  const borrowed = ["ak47_neon_rider", "m4a1s_printstream", "awp_dragon_lore", "deagle_blaze", "usp_kill_confirmed", "knife_karambit_fade", "glock_fade", "m4a4_howl"];
  for (const weapon of borrowed) {
    extraGame.push({ path: `models/weapons/custom/${weapon}/${weapon}.vmdl_c`, size: Math.round((140 + rand() * 120) * MIB) });
    extraGame.push({ path: `models/weapons/custom/${weapon}/${weapon}_lod1.vmdl_c`, size: Math.round((25 + rand() * 30) * MIB) });
    for (const map of ["color", "normal", "rough"]) {
      extraGame.push({ path: `materials/weapons/custom/${weapon}/${map}_tga_${Math.floor(rand() * 0xffff).toString(16)}.vtex_c`, size: Math.round((10 + rand() * 24) * MIB) });
    }
    extraGame.push({ path: `materials/weapons/custom/${weapon}/${weapon}.vmat_c`, size: 2 * KIB });
  }
  extraGame.push({ path: "panorama/layout/custom_game/agent_select.vxml_c", size: 9 * KIB });
  extraGame.push({ path: "panorama/styles/custom_game/agent_select.vcss_c", size: 14 * KIB });
  extraGame.push({ path: "sounds/agents/select_click.vsnd_c", size: 120 * KIB });
  return { name: "next_models", title: "Next Models", sources, extraGame };
}

function hudSeed(): AddonSeed {
  const sources = [
    "panorama/layout/custom_game/hud.xml",
    "panorama/layout/custom_game/killfeed.xml",
    "panorama/styles/custom_game/hud.css",
    "panorama/styles/custom_game/killfeed.css",
    "panorama/scripts/custom_game/hud.js",
    "panorama/images/coin.png",
    "panorama/images/hud_bg.png",
    "panorama/images/icons/skull.svg",
  ].map((path, index) => ({ path, size: path.endsWith(".png") ? (180 + index * 40) * KIB : (3 + index) * KIB }));
  return { name: "next_hud", title: "Next HUD", sources, extraGame: [] };
}

function soundsSeed(): AddonSeed {
  const rand = seeded(64);
  const sources: AddonSeed["sources"] = [];
  for (let i = 1; i <= 24; i += 1) sources.push({ path: `sounds/retakes/announcer/line_${String(i).padStart(2, "0")}.wav`, size: Math.round((0.6 + rand() * 2.4) * MIB) });
  sources.push({ path: "soundevents/retakes_announcer.vsndevts", size: 6 * KIB });
  const extraGame = [
    { path: "sounds/retakes/music/round_start.vsnd_c", size: 4 * MIB },
    { path: "sounds/retakes/music/round_win.vsnd_c", size: 3 * MIB },
    { path: "soundevents/retakes_music.vsndevts_c", size: 2 * KIB },
  ];
  return { name: "retakes_sounds", title: "Retakes Sounds", sources, extraGame };
}

function jailSeed(): AddonSeed {
  return {
    name: "jb_maps",
    title: "Jailbreak Maps",
    sources: [],
    extraGame: [
      { path: "maps/jb_summer_v3.vpk", size: Math.round(0.72 * GIB) },
      { path: "maps/jb_winter_v2.vpk", size: Math.round(0.46 * GIB) },
      { path: "maps/jb_summer_v3/minimap.vtex_c", size: 4 * MIB },
    ],
  };
}

const SEEDS: AddonSeed[] = [hudSeed(), modelsSeed(), soundsSeed(), jailSeed()];

/** What resourcecompiler makes from a source (SPEC 4 table), build-relative. */
function outputOf(source: string): string | null {
  const lower = source.toLowerCase();
  const image = /^(panorama\/images\/.+)\.(png|jpe?g|tga|psd)$/i.exec(source);
  if (image) return `${image[1]}_${image[2]!.toLowerCase()}.vtex_c`;
  const swap = (ext: string) => source.replace(/\.[^.]+$/, ext);
  if (lower.startsWith("panorama/") && lower.endsWith(".xml")) return swap(".vxml_c");
  if (lower.startsWith("panorama/") && lower.endsWith(".css")) return swap(".vcss_c");
  if (lower.startsWith("panorama/") && lower.endsWith(".js")) return swap(".vjs_c");
  if (lower.endsWith(".svg")) return swap(".vsvg_c");
  if (lower.startsWith("sounds/") && /\.(wav|mp3)$/.test(lower)) return swap(".vsnd_c");
  if (/\.(vmdl|vmat|vtex|vpcf|vsndevts|vdata)$/.test(lower)) return `${source}_c`;
  return null;
}

/** The source a compiled file decompiles back to, when ValveResourceFormat does it cleanly (SPEC 6.4). */
function decompiledSource(output: string): string | null {
  const lower = output.toLowerCase();
  const image = /^(panorama\/images\/.+)_(png|jpe?g|tga)\.vtex_c$/i.exec(output);
  if (image) return `${image[1]}.png`;
  if (!lower.startsWith("panorama/") && !lower.startsWith("sounds/") && !lower.endsWith(".vsndevts_c") && !lower.endsWith(".vsvg_c")) return null;
  if (lower.endsWith(".vxml_c")) return output.replace(/\.vxml_c$/i, ".xml");
  if (lower.endsWith(".vcss_c")) return output.replace(/\.vcss_c$/i, ".css");
  if (lower.endsWith(".vjs_c")) return output.replace(/\.vjs_c$/i, ".js");
  if (lower.endsWith(".vsvg_c")) return output.replace(/\.vsvg_c$/i, ".svg");
  if (lower.endsWith(".vsnd_c")) return output.replace(/\.vsnd_c$/i, ".wav");
  if (lower.endsWith(".vsndevts_c")) return output.replace(/\.vsndevts_c$/i, ".vsndevts");
  return null;
}

function compiledSize(source: { path: string; size: number }): number {
  if (source.path.endsWith(".fbx")) return 0;
  if (/\.(tga|png)$/i.test(source.path)) return Math.round(source.size * 0.7);
  if (source.path.endsWith(".vmdl")) return 40 * MIB;
  return Math.max(1 * KIB, Math.round(source.size * 1.2));
}

function gameFilesOf(seed: AddonSeed): { path: string; size: number; from: string | null }[] {
  const files: { path: string; size: number; from: string | null }[] = [];
  for (const source of seed.sources) {
    const output = outputOf(source.path);
    if (output) files.push({ path: output, size: compiledSize(source), from: source.path });
  }
  for (const extra of seed.extraGame) files.push({ ...extra, from: null });
  if (files.length > 0) {
    files.push({ path: "tools_asset_info.bin", size: 220 * KIB, from: null });
    files.push({ path: "readonly_tools_asset_info.bin", size: 40 * KIB, from: null });
  }
  return files;
}

const VALVE_FILES = new Set(["tools_asset_info.bin", "readonly_tools_asset_info.bin"]);

function cs2Addons(): Cs2Addon[] {
  return SEEDS.map((seed) => {
    const game = gameFilesOf(seed);
    const project = demoProjects.find((entry) => entry.config.addon === seed.name && entry.link.state === "linked");
    const sizeBytes = seed.sources.reduce((sum, file) => sum + file.size, 0) + game.reduce((sum, file) => sum + file.size, 0);
    return {
      name: seed.name,
      hasContent: seed.sources.length > 0,
      hasGame: game.length > 0,
      contentFiles: seed.sources.length,
      gameFiles: game.length,
      sizeBytes,
      linkedTo: project ? project.contentDir : null,
    };
  });
}

/** SPEC 6.4: sources are copied, every build file is skipped (we build it / Valve's), decompiled or kept prebuilt. */
export function planImport(addon: string, path: string, decompile: boolean): ImportPlan {
  const seed = SEEDS.find((entry) => entry.name === addon);
  if (!seed) throw new MockError("not_found", `No addon named ${addon} in CS2's csgo_addons folders.`);
  const items: ImportItem[] = [];
  const sourcePaths = new Set(seed.sources.map((source) => source.path.toLowerCase()));

  for (const source of seed.sources) {
    items.push({ path: `content/${source.path}`, size: source.size, action: "copySource", target: `content/${source.path}`, reason: null });
  }
  for (const file of gameFilesOf(seed)) {
    let action: ImportAction;
    let target: string | null = null;
    let reason: string | null = null;
    if (VALVE_FILES.has(file.path)) {
      action = "skip";
      reason = "Valve tools file";
    } else if (file.from) {
      action = "skip";
      reason = `Built from content/${file.from}`;
    } else {
      const back = decompile ? decompiledSource(file.path) : null;
      if (back && !sourcePaths.has(back.toLowerCase())) {
        action = "decompile";
        target = `content/${back}`;
        reason = "Comes back cleanly as a source";
      } else {
        action = "prebuilt";
        target = `prebuilt/${file.path}`;
        reason = file.path.startsWith("maps/") ? "Maps can not be rebuilt here" : "No source for this compiled file";
      }
    }
    items.push({ path: `game/${file.path}`, size: file.size, action, target, reason });
  }

  const warnings: string[] = [];
  const existing = findDemoProject(path);
  if (existing) warnings.push(`${path} is already a project. Imported files are added to it, nothing there is deleted.`);
  if (seed.sources.length > 0) warnings.push(`The content folder ${contentLink(seed.name)} is renamed to a backup and replaced with a link.`);
  if (seed.extraGame.some((file) => file.path.endsWith(".vmdl_c"))) {
    warnings.push("Some models have no source here. They go to prebuilt/ and are copied into the build as they are.");
  }

  return {
    addon,
    path,
    sourceFiles: seed.sources.length,
    items,
    totalBytes: items.filter((item) => item.action !== "skip").reduce((sum, item) => sum + item.size, 0),
    warnings,
  };
}

/* ---------- projects ---------- */

function newConfig(addon: string, title: string): WorkshopConfig {
  return {
    addon,
    workshopId: null,
    title,
    description: null,
    visibility: "private",
    tags: ["Custom"],
    preview: "preview.png",
    pack: { include: [], exclude: ["**/*.psd"] },
    autoBuild: true,
    autoPublish: { enabled: false, pattern: "^v(\\d+\\.\\d+\\.\\d+)\\b", githubRelease: false },
  };
}

function addProject(path: string, addon: string, title: string, options: { remote?: string | null; linked?: boolean } = {}): ProjectInfo {
  const clean = path.replace(/[\\/]+$/, "");
  const project: ProjectInfo = {
    path: clean,
    config: newConfig(addon, title),
    configErrors: [],
    contentDir: `${clean}\\content`,
    prebuiltDir: `${clean}\\prebuilt`,
    buildDir: gameDir(addon),
    link: options.linked
      ? { state: "linked", linkPath: contentLink(addon), target: `${clean}\\content`, detail: null }
      : { state: "missing", linkPath: contentLink(addon), target: null, detail: "No link in CS2's content folder yet." },
    git: { isRepo: true, branch: "main", remote: options.remote ?? null, lfs: true },
  };
  demoProjects.push(project);
  demoGitStatus[project.path] = {
    isRepo: true,
    branch: "main",
    upstream: options.remote ? "origin/main" : null,
    ahead: 0,
    behind: 0,
    files: [],
    merging: false,
    lfsAvailable: true,
  };
  touchRecent(project);
  return structuredClone(project);
}

function touchRecent(project: ProjectInfo) {
  const entry: RecentProject = {
    path: project.path,
    addon: project.config.addon,
    title: project.config.title,
    lastOpened: new Date().toISOString(),
    exists: true,
  };
  const others = recent.filter((item) => item.path.toLowerCase() !== project.path.toLowerCase());
  const index = recent.findIndex((item) => item.path.toLowerCase() === project.path.toLowerCase());
  // keep the order stable for projects already listed, new ones go on top (like the core's list)
  recent = index >= 0 ? recent.map((item, i) => (i === index ? entry : item)) : [entry, ...others].slice(0, 50);
}

function requireFreeFolder(path: string) {
  if (path.trim().length === 0) throw new MockError("invalid_params", "path is empty.");
  if (!/^[a-zA-Z]:\\|^\\\\|^\//.test(path)) throw new MockError("invalid_params", `${path} is not an absolute path.`);
  if (findDemoProject(path)) throw new MockError("conflict", `${path} already holds a project. Pick another folder.`);
}

async function cloneProgress(ctx: MockContext, url: string) {
  const objects = 4_212;
  const lfsFiles = 38;
  ctx.emit("git.progress", { phase: "Connecting", percent: null, line: `Cloning into '${url.split("/").pop()?.replace(/\.git$/, "")}'...` });
  await delay(350, ctx.signal);
  for (let percent = 0; percent <= 100; percent += 20) {
    ctx.emit("git.progress", { phase: "Counting objects", percent, line: `remote: Counting objects: ${percent}% (${Math.round((objects * percent) / 100)}/${objects})` });
    await delay(60, ctx.signal);
  }
  for (let percent = 0; percent <= 100; percent += 4) {
    const mib = ((percent / 100) * 46.3).toFixed(2);
    ctx.emit("git.progress", {
      phase: "Receiving objects",
      percent,
      line: `Receiving objects: ${String(percent).padStart(3)}% (${Math.round((objects * percent) / 100)}/${objects}), ${mib} MiB | 11.42 MiB/s`,
    });
    await delay(70, ctx.signal);
  }
  for (let percent = 0; percent <= 100; percent += 25) {
    ctx.emit("git.progress", { phase: "Resolving deltas", percent, line: `Resolving deltas: ${percent}% (${Math.round(1_870 * (percent / 100))}/1870)` });
    await delay(80, ctx.signal);
  }
  for (let done = 0; done <= lfsFiles; done += 2) {
    ctx.emit("git.progress", { phase: "Downloading LFS files", percent: (done / lfsFiles) * 100, line: `Downloading LFS objects: ${done} of ${lfsFiles} files` });
    await delay(90, ctx.signal);
  }
}

/* ---------- Steam ---------- */

const steamItems: SteamItem[] = [
  {
    workshopId: "3389501123",
    title: "Next HUD",
    visibility: "public",
    timeCreated: "2026-06-02T17:20:00Z",
    timeUpdated: "2026-11-18T21:04:00Z",
    size: 14_883_201,
    subscribers: 18_342,
    favorites: 1_204,
    views: 96_510,
    previewUrl: null,
  },
  {
    workshopId: "3401188420",
    title: "Retakes Sounds",
    visibility: "unlisted",
    timeCreated: "2026-08-11T10:02:00Z",
    timeUpdated: "2026-10-30T08:47:00Z",
    size: 71_301_877,
    subscribers: 6_120,
    favorites: 88,
    views: 12_900,
    previewUrl: null,
  },
  {
    workshopId: "3277409915",
    title: "Jailbreak Maps",
    visibility: "friendsOnly",
    timeCreated: "2026-03-14T12:00:00Z",
    timeUpdated: "2026-09-02T19:31:00Z",
    size: 1_268_951_020,
    subscribers: 2_904,
    favorites: 41,
    views: 7_310,
    previewUrl: null,
  },
];

/* ---------- handlers ---------- */

export const handlers: Record<string, MockHandler> = {
  "app.info": async (): Promise<AppInfo> => (noCompile() ? { ...demoAppInfo, os: "macos", canCompile: false, canPublish: false } : demoAppInfo),

  "env.doctor": async (_params: undefined, ctx): Promise<DoctorReport> => {
    await delay(300, ctx.signal);
    if (!noCompile()) return demoDoctor;
    return {
      os: "macos",
      canCompile: false,
      canPublish: false,
      cs2Path: null,
      checks: demoDoctor.checks.map((check) => {
        if (check.id === "os") return { ...check, label: "Windows", status: "error", detail: "macOS 26.1 (arm64)", fix: "Compiling and publishing need Windows. Edit, commit and push work here." };
        if (check.id === "steam" || check.id === "cs2" || check.id === "workshopTools" || check.id === "steamRunning") {
          return { ...check, status: "error", detail: "Not available on this OS", fix: check.id === "workshopTools" ? "Steam > CS2 > Properties > DLC > Counter-Strike 2 Workshop Tools" : null };
        }
        return check;
      }),
    };
  },

  "settings.get": async (): Promise<AppSettings> => ({ ...structuredClone(settings), recentProjects: structuredClone(recent) }),

  "settings.set": async (patch: AppSettingsPatch): Promise<AppSettings> => {
    if (patch.compileParallelism !== undefined && (!Number.isInteger(patch.compileParallelism) || patch.compileParallelism < 1)) {
      throw new MockError("invalid_params", "compileParallelism must be a whole number of 1 or more.");
    }
    if (patch.maxPackBytes !== undefined && (!Number.isFinite(patch.maxPackBytes) || patch.maxPackBytes <= 0)) {
      throw new MockError("invalid_params", "maxPackBytes must be more than 0.");
    }
    if (patch.compilerArgs !== undefined && !patch.compilerArgs.includes("{input}")) {
      throw new MockError("invalid_params", "compilerArgs must contain {input}.");
    }
    settings = {
      ...settings,
      cs2PathOverride: patch.cs2PathOverride !== undefined ? patch.cs2PathOverride || null : settings.cs2PathOverride,
      compileParallelism: patch.compileParallelism ?? settings.compileParallelism,
      compilerArgs: patch.compilerArgs ?? settings.compilerArgs,
      maxPackBytes: patch.maxPackBytes ?? settings.maxPackBytes,
      theme: patch.theme ?? settings.theme,
    };
    return { ...structuredClone(settings), recentProjects: structuredClone(recent) };
  },

  "projects.list": async (): Promise<RecentProject[]> => structuredClone(recent),

  "projects.open": async ({ path }: PathParams): Promise<ProjectInfo> => {
    const project = findDemoProject(path);
    if (!project) {
      const listed = recent.find((entry) => entry.path.toLowerCase() === path.toLowerCase());
      if (listed) listed.exists = false;
      throw new MockError("not_found", `No addon project at ${path}. The folder or its workshop.json is missing.`);
    }
    touchRecent(project);
    return structuredClone(project);
  },

  "projects.create": async ({ path, addon, title }: { path: string; addon: string; title: string }, ctx): Promise<ProjectInfo> => {
    if (!/^[a-z0-9_]{1,64}$/.test(addon)) throw new MockError("invalid_params", "addon must match ^[a-z0-9_]{1,64}$.");
    if (title.trim().length === 0) throw new MockError("invalid_params", "title is empty.");
    requireFreeFolder(path);
    await delay(450, ctx.signal);
    return addProject(path, addon, title.trim());
  },

  "projects.clone": async ({ url, path }: { url: string; path: string }, ctx): Promise<ProjectInfo> => {
    requireFreeFolder(path);
    if (/fail|missing|404/i.test(url)) {
      await delay(500, ctx.signal);
      throw new MockError("git_failed", `fatal: repository '${url}' not found`);
    }
    await cloneProgress(ctx, url);
    const name = path.replace(/[\\/]+$/, "").split(/[\\/]/).pop() ?? "addon";
    const addon = name.toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "").slice(0, 64) || "addon";
    const title = name.replace(/[-_]+/g, " ").replace(/\b\w/g, (letter) => letter.toUpperCase());
    return addProject(path, addon, title, { remote: url });
  },

  "projects.remove": async ({ path }: PathParams): Promise<RecentProject[]> => {
    recent = recent.filter((entry) => entry.path.toLowerCase() !== path.toLowerCase());
    return structuredClone(recent);
  },

  "projects.cs2Addons": async (): Promise<Cs2Addon[]> => cs2Addons(),

  "import.plan": async ({ addon, path, decompile }: { addon: string; path: string; decompile: boolean }, ctx): Promise<ImportPlan> => {
    await delay(250, ctx.signal);
    return planImport(addon, path, decompile);
  },

  "import.run": async ({ addon, path, decompile }: { addon: string; path: string; decompile: boolean }, ctx): Promise<ImportResult> => {
    const plan = planImport(addon, path, decompile);
    const work = plan.items.filter((item) => item.action !== "skip");
    const total = work.length;
    const step = Math.max(1, Math.ceil(total / 40));
    for (let done = 0; done < total; done += step) {
      ctx.emit("import.progress", { done, total, current: work[done]!.path });
      await delay(70, ctx.signal);
    }
    ctx.emit("import.progress", { done: total, total, current: "" });

    const seed = SEEDS.find((entry) => entry.name === addon)!;
    const existing = findDemoProject(path);
    let project: ProjectInfo;
    if (existing) {
      existing.link = { state: "linked", linkPath: contentLink(addon), target: existing.contentDir, detail: null };
      touchRecent(existing);
      project = structuredClone(existing);
    } else {
      project = addProject(path, addon, seed.title, { linked: true });
    }

    const count = (action: ImportAction) => plan.items.filter((item) => item.action === action).length;
    const warnings: string[] = [];
    const decompiled = plan.items.filter((item) => item.action === "decompile");
    if (decompiled.length > 1) {
      warnings.push(`${decompiled[0]!.path} did not decompile cleanly and was kept in prebuilt/ instead.`);
    }
    return {
      project,
      copied: count("copySource"),
      decompiled: Math.max(0, decompiled.length - (decompiled.length > 1 ? 1 : 0)),
      prebuilt: count("prebuilt") + (decompiled.length > 1 ? 1 : 0),
      skipped: count("skip"),
      backupPath: seed.sources.length > 0 ? `${contentLink(addon)}.addonstudio-backup-${stamp()}` : null,
      warnings,
    };
  },

  "steam.items": async (_params: undefined, ctx): Promise<SteamItem[]> => {
    await delay(200, ctx.signal);
    return structuredClone(steamItems);
  },
};

/** For tests and other mocks: the default parent folder of demo projects. */
export const DEMO_PARENT = ADDONS_DIR;
