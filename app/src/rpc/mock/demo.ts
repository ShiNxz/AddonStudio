// Demo data for the browser mock: two projects on a Windows machine.
import type { AppInfo, AppSettings, DoctorReport, GitStatus, ProjectInfo, RecentProject, WorkshopConfig } from "../types";

export const CS2_PATH = "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Counter-Strike Global Offensive";
export const ADDONS_DIR = "C:\\Users\\dev\\Addons";

const contentLink = (addon: string) => `${CS2_PATH}\\content\\csgo_addons\\${addon}`;
const buildDir = (addon: string) => `${CS2_PATH}\\game\\csgo_addons\\${addon}`;

export const demoAppInfo: AppInfo = {
  version: "0.1.0",
  os: "windows",
  canCompile: true,
  canPublish: true,
};

export const demoDoctor: DoctorReport = {
  os: "windows",
  canCompile: true,
  canPublish: true,
  cs2Path: CS2_PATH,
  checks: [
    { id: "os", label: "Windows", status: "ok", detail: "Windows 11 Pro 24H2", fix: null },
    { id: "steam", label: "Steam", status: "ok", detail: "C:\\Program Files (x86)\\Steam", fix: null },
    { id: "cs2", label: "Counter-Strike 2", status: "ok", detail: CS2_PATH, fix: null },
    {
      id: "workshopTools",
      label: "Workshop Tools",
      status: "ok",
      detail: `${CS2_PATH}\\game\\bin\\win64\\resourcecompiler.exe`,
      fix: null,
    },
    { id: "steamRunning", label: "Steam running", status: "ok", detail: "Logged in", fix: null },
    { id: "git", label: "Git", status: "ok", detail: "git version 2.51.0.windows.1", fix: null },
    { id: "gitLfs", label: "Git LFS", status: "ok", detail: "git-lfs/3.7.0", fix: null },
    {
      id: "gh",
      label: "GitHub CLI",
      status: "warn",
      detail: "gh is installed but not logged in",
      fix: "Run: gh auth login",
    },
  ],
};

const hudConfig: WorkshopConfig = {
  addon: "next_hud",
  workshopId: "3389501123",
  title: "Next HUD",
  description: null,
  visibility: "public",
  tags: ["Custom"],
  preview: "preview.png",
  pack: { include: [], exclude: ["**/*.psd"] },
  autoBuild: true,
  autoPublish: { enabled: true, pattern: "^v(\\d+\\.\\d+\\.\\d+)\\b", githubRelease: false },
};

const modelsConfig: WorkshopConfig = {
  addon: "next_models",
  workshopId: null,
  title: "Next Models",
  description: "Player and weapon models for Next servers.",
  visibility: "private",
  tags: ["Custom"],
  preview: "preview.png",
  pack: { include: [], exclude: ["**/*.psd"] },
  autoBuild: false,
  autoPublish: { enabled: false, pattern: "^v(\\d+\\.\\d+\\.\\d+)\\b", githubRelease: false },
};

export const demoProjects: ProjectInfo[] = [
  {
    path: `${ADDONS_DIR}\\next-hud`,
    config: hudConfig,
    configErrors: [],
    contentDir: `${ADDONS_DIR}\\next-hud\\content`,
    prebuiltDir: `${ADDONS_DIR}\\next-hud\\prebuilt`,
    buildDir: buildDir("next_hud"),
    link: { state: "linked", linkPath: contentLink("next_hud"), target: `${ADDONS_DIR}\\next-hud\\content`, detail: null },
    git: { isRepo: true, branch: "main", remote: "https://github.com/example/next-hud.git", lfs: true },
  },
  {
    path: `${ADDONS_DIR}\\next-models`,
    config: modelsConfig,
    configErrors: [],
    contentDir: `${ADDONS_DIR}\\next-models\\content`,
    prebuiltDir: `${ADDONS_DIR}\\next-models\\prebuilt`,
    buildDir: buildDir("next_models"),
    link: {
      state: "conflict",
      linkPath: contentLink("next_models"),
      target: null,
      detail: "A real folder is in the way. Linking renames it to a backup first.",
    },
    git: { isRepo: true, branch: "main", remote: null, lfs: true },
  },
];

export const demoRecent: RecentProject[] = [
  { path: demoProjects[0]!.path, addon: "next_hud", title: "Next HUD", lastOpened: "2026-11-22T09:12:00Z", exists: true },
  { path: demoProjects[1]!.path, addon: "next_models", title: "Next Models", lastOpened: "2026-11-21T18:40:00Z", exists: true },
];

export const demoSettings: AppSettings = {
  cs2PathOverride: null,
  compileParallelism: 4,
  compilerArgs: '-nop4 -i "{input}"',
  maxPackBytes: 3 * 1024 ** 3,
  theme: "dark",
  recentProjects: demoRecent,
};

export const demoGitStatus: Record<string, GitStatus> = {
  [demoProjects[0]!.path]: {
    isRepo: true,
    branch: "main",
    upstream: "origin/main",
    ahead: 1,
    behind: 0,
    merging: false,
    lfsAvailable: true,
    files: [
      {
        path: "content/panorama/layout/custom_game/hud.xml",
        origPath: null,
        index: "unmodified",
        worktree: "modified",
        staged: false,
        lfs: false,
        size: 4210,
      },
      {
        path: "content/panorama/images/coin.png",
        origPath: null,
        index: "added",
        worktree: "unmodified",
        staged: true,
        lfs: true,
        size: 18_344,
      },
    ],
  },
  [demoProjects[1]!.path]: {
    isRepo: true,
    branch: "main",
    upstream: null,
    ahead: 0,
    behind: 0,
    merging: false,
    lfsAvailable: true,
    files: [],
  },
};

/** The demo project at this path (Windows paths compare ignoring case). */
export function findDemoProject(path: string): ProjectInfo | undefined {
  const wanted = path.toLowerCase().replace(/[\\/]+$/, "");
  return demoProjects.find((project) => project.path.toLowerCase() === wanted);
}
