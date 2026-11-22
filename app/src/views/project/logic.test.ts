// Run with: node --test src/views/project/logic.test.ts (Node 24 strips the types itself).
import { test } from "node:test";
import assert from "node:assert/strict";
import type { GitFileChange, PackFile, WorkshopConfig } from "../../rpc/types";

// a computed specifier so tsc does not need allowImportingTsExtensions, while node loads the .ts file directly
const logic: typeof import("./logic") = await import(new URL("./logic.ts", import.meta.url).href);
const { BuildQueue, autoPublishVersion, classifyLogLine, filterPackFiles, folderSummary, formatBytes, shownState, splitChanges, validateConfig } = logic;

function deferred() {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<void>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const tick = () => new Promise((resolve) => setTimeout(resolve, 0));

test("BuildQueue runs one at a time and keeps at most one queued", async () => {
  const queue = new BuildQueue();
  const log: string[] = [];
  const first = deferred();
  const second = deferred();

  assert.equal(queue.submit(async () => { log.push("first"); await first.promise; }, true), "started");
  assert.equal(queue.isBusy, true);
  assert.equal(queue.submit(async () => { log.push("dropped"); }, false), "dropped");
  assert.equal(queue.submit(async () => { log.push("replaced"); }, true), "queued");
  assert.equal(queue.submit(async () => { log.push("second"); await second.promise; }, true), "queued");
  assert.deepEqual(log, ["first"]);

  // a failing task still lets the queued one start
  first.reject(new Error("compile failed"));
  await tick();
  assert.deepEqual(log, ["first", "second"]);
  assert.equal(queue.isBusy, true);
  assert.equal(queue.hasPending, false);

  second.resolve();
  await tick();
  assert.equal(queue.isBusy, false);
  assert.equal(queue.submit(async () => { log.push("third"); }, true), "started");
  await tick();
  assert.deepEqual(log, ["first", "second", "third"]);
});

const baseConfig: WorkshopConfig = {
  addon: "next_hud",
  workshopId: "3389501123",
  title: "Next HUD",
  description: null,
  visibility: "public",
  tags: ["Custom"],
  preview: "preview.png",
  pack: { valveWhitelist: false, include: [], exclude: ["**/*.psd"] },
  autoBuild: true,
  autoPublish: { enabled: true, pattern: "^v(\\d+\\.\\d+\\.\\d+)\\b", githubRelease: false },
};

test("validateConfig accepts the default config and flags each bad field", () => {
  assert.deepEqual(validateConfig(baseConfig), {});

  const errors = validateConfig({
    ...baseConfig,
    addon: "Next-HUD",
    workshopId: "12ab",
    title: "  ",
    tags: ["Custom", "custom"],
    preview: "C:\\art\\preview.png",
    pack: { valveWhitelist: false, include: ["maps\\*.vpk"], exclude: ["../outside/**"] },
    autoPublish: { enabled: true, pattern: "^v\\d+", githubRelease: false },
  });
  assert.deepEqual(Object.keys(errors).sort(), ["addon", "exclude", "include", "pattern", "preview", "tags", "title", "workshopId"]);
  assert.match(errors.pattern!, /group/);

  assert.match(validateConfig({ ...baseConfig, autoPublish: { ...baseConfig.autoPublish, pattern: "^v(" } }).pattern!, /not a valid/);
  // an empty pattern is fine while auto-publish is off
  assert.deepEqual(validateConfig({ ...baseConfig, autoPublish: { enabled: false, pattern: "", githubRelease: false } }), {});
});

test("splitChanges puts a partly staged file in both groups", () => {
  const file = (path: string, index: GitFileChange["index"], worktree: GitFileChange["worktree"], staged: boolean): GitFileChange => ({
    path,
    origPath: null,
    index,
    worktree,
    staged,
    lfs: false,
    size: 1,
  });
  const partly = file("content/a.css", "modified", "modified", true);
  const stagedOnly = file("content/b.png", "added", "unmodified", true);
  const untracked = file("content/c.js", "untracked", "untracked", false);
  const { staged, changes } = splitChanges([partly, stagedOnly, untracked]);

  assert.deepEqual(staged.map((f) => f.path), ["content/a.css", "content/b.png"]);
  assert.deepEqual(changes.map((f) => f.path), ["content/a.css", "content/c.js"]);
  assert.equal(shownState(stagedOnly, "staged"), "added");
  assert.equal(shownState(untracked, "changes"), "untracked");
});

test("filterPackFiles searches all terms, filters and sorts with a stable tie-break", () => {
  const files: PackFile[] = [
    { path: "panorama/layout/hud.vxml_c", size: 4000, change: "changed", origin: "compiled" },
    { path: "panorama/images/coin_png.vtex_c", size: 90_000, change: "added", origin: "compiled" },
    { path: "models/agent_t.vmdl_c", size: 90_000, change: "same", origin: "prebuilt" },
    { path: "panorama/layout/scoreboard.vxml_c", size: 6000, change: "same", origin: "compiled" },
  ];
  const all = { search: "", origin: "all", change: "all", sort: "size", descending: true } as const;

  assert.deepEqual(
    filterPackFiles(files, all).map((f) => f.path),
    ["models/agent_t.vmdl_c", "panorama/images/coin_png.vtex_c", "panorama/layout/scoreboard.vxml_c", "panorama/layout/hud.vxml_c"],
  );
  assert.deepEqual(filterPackFiles(files, { ...all, search: "LAYOUT vxml" }).map((f) => f.path), ["panorama/layout/scoreboard.vxml_c", "panorama/layout/hud.vxml_c"]);
  assert.deepEqual(filterPackFiles(files, { ...all, origin: "prebuilt" }).map((f) => f.path), ["models/agent_t.vmdl_c"]);
  assert.deepEqual(filterPackFiles(files, { ...all, change: "added", sort: "path", descending: false }).map((f) => f.path), ["panorama/images/coin_png.vtex_c"]);
  // the input is not reordered
  assert.equal(files[0]!.path, "panorama/layout/hud.vxml_c");
});

test("folderSummary groups the whitelist drops by their top folders", () => {
  const groups = folderSummary([
    { path: "panorama/images/icons/skillgroups/skillgroup1_png.vtex_c", size: 10 },
    { path: "panorama/images/icons/skillgroups/skillgroup2_png.vtex_c", size: 20 },
    { path: "Panorama/Images/logo_png.vtex_c", size: 5 },
    { path: "characters/models/emote/dance.vmdl_c", size: 100 },
    { path: "Thumbs.db", size: 1 },
  ]);
  assert.deepEqual(groups, [
    { folder: "panorama/images/", count: 3, size: 35 },
    { folder: "characters/models/", count: 1, size: 100 },
    { folder: "", count: 1, size: 1 },
  ]);
  assert.deepEqual(folderSummary([{ path: "a/b/c.txt", size: 1 }], 1), [{ folder: "a/", count: 1, size: 1 }]);
});

test("log lines, auto-publish versions and sizes", () => {
  assert.equal(classifyLogLine('ERROR: Failed to compile resource "panorama/styles/hud.vcss"'), "error");
  assert.equal(classifyLogLine("panorama/x.css failed to compile"), "error");
  assert.equal(classifyLogLine("WARNING: texture is not a power of two"), "warning");
  assert.equal(classifyLogLine("1 resource compiled (0.42 s)"), null);

  assert.equal(autoPublishVersion("^v(\\d+\\.\\d+\\.\\d+)\\b", "v1.5.0 New scoreboard"), "1.5.0");
  assert.equal(autoPublishVersion("^v(\\d+\\.\\d+\\.\\d+)\\b", "Fix v1.5.0 typo"), null);
  assert.equal(autoPublishVersion("^v(", "v1.5.0"), null);

  assert.equal(formatBytes(0), "0 B");
  assert.equal(formatBytes(18_344), "17.9 KB");
  assert.equal(formatBytes(3 * 1024 ** 3), "3 GB");
  assert.equal(formatBytes(1.5 * 1024 ** 2), "1.5 MB");
});
