// Run: node --import ./node_modules/jiti/lib/jiti-register.mjs --test src/app/tests/*.test.ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { addonFromTitle, isValidAddon, isValidFolderName, repoNameFromUrl } from "../../views/add/naming";
import { joinPath } from "../platform";
import { splitFix } from "../../views/doctor/fix";
import { bytesToGb, diffSettings, gbToBytes, toDraft } from "../../views/settings/draft";
import { planImport } from "../../rpc/mock/shell";
import { summarizePlan } from "../../views/add/plan";

const summarizePlanForTest = (items: Parameters<typeof summarizePlan>[0]) => {
  const summary = summarizePlan(items);
  return { copySource: summary.copySource.count, decompile: summary.decompile.count, prebuilt: summary.prebuilt.count, skip: summary.skip.count };
};

test("project names: addon from title, repo folder from URL, Windows path joins", () => {
  assert.equal(addonFromTitle("Next HUD v2!"), "next_hud_v2");
  assert.equal(addonFromTitle("  Café -- Models  "), "cafe_models");
  assert.equal(addonFromTitle("x".repeat(80)).length, 64);
  assert.ok(isValidAddon("next_hud"));
  assert.ok(!isValidAddon("Next-HUD"));

  assert.equal(repoNameFromUrl("https://github.com/example/next-hud.git"), "next-hud");
  assert.equal(repoNameFromUrl("git@github.com:example/next-hud.git"), "next-hud");
  assert.equal(repoNameFromUrl("https://github.com/example/next-hud/"), "next-hud");
  assert.equal(repoNameFromUrl("https://github.com/"), "github.com");
  assert.ok(!isValidFolderName("con"));
  assert.ok(!isValidFolderName("a:b"));

  assert.equal(joinPath("C:\\Users\\dev\\Addons\\", "next-hud"), "C:\\Users\\dev\\Addons\\next-hud");
  assert.equal(joinPath("D:", "x"), "D:\\x");
  assert.equal(joinPath("/home/dev/addons", "x"), "/home/dev/addons/x");
});

test("doctor fix texts split a trailing command into its own copy block", () => {
  assert.deepEqual(splitFix("Run: gh auth login"), { text: null, command: "gh auth login" });
  assert.deepEqual(splitFix("Install Git LFS: winget install GitHub.GitLFS"), { text: "Install Git LFS:", command: "winget install GitHub.GitLFS" });
  assert.deepEqual(splitFix("Steam > CS2 > Properties > DLC > Counter-Strike 2 Workshop Tools"), {
    text: "Steam > CS2 > Properties > DLC > Counter-Strike 2 Workshop Tools",
    command: null,
  });
});

test("settings form sends only what changed, the cap in bytes, an empty CS2 path as null", () => {
  const saved = toDraft({ cs2PathOverride: "D:\\CS2", compileParallelism: 4, compilerArgs: '-nop4 -i "{input}"', theme: "dark", recentProjects: [], maxPackBytes: 3221225472 });
  assert.equal(saved.packCapGb, 3);
  assert.deepEqual(diffSettings(saved, saved), {});
  assert.deepEqual(diffSettings(saved, { ...saved, cs2PathOverride: "  ", packCapGb: 3.5 }), { cs2PathOverride: null, maxPackBytes: gbToBytes(3.5) });
  assert.equal(bytesToGb(gbToBytes(2.8)), 2.8);
  // an older core without maxPackBytes still shows the 3 GB default
  assert.equal(toDraft({ cs2PathOverride: null, compileParallelism: 1, compilerArgs: "", theme: "dark", recentProjects: [] }).packCapGb, 3);
});

test("import plan for the models addon: sources copied, built outputs skipped, borrowed models prebuilt", () => {
  const path = "C:\\Users\\dev\\Addons\\next-models";
  const plan = planImport("next_models", path, true);
  const summary = summarizePlanForTest(plan.items);
  assert.ok(summary.copySource > 0 && summary.prebuilt > 0 && summary.skip > 0 && summary.decompile > 0);
  assert.ok(plan.items.some((item) => item.path === "game/tools_asset_info.bin" && item.action === "skip"));
  const borrowed = plan.items.find((item) => item.path.endsWith("ak47_neon_rider.vmdl_c"));
  assert.equal(borrowed?.action, "prebuilt");
  assert.equal(borrowed?.target, "prebuilt/models/weapons/custom/ak47_neon_rider/ak47_neon_rider.vmdl_c");
  const built = plan.items.filter((item) => item.path.startsWith("game/") && item.reason?.startsWith("Built from"));
  assert.ok(built.length > 0 && built.every((item) => item.target === null));
  // the build dir of the models addon is about 2.8 GB
  const gameBytes = plan.items.filter((item) => item.path.startsWith("game/")).reduce((sum, item) => sum + item.size, 0);
  assert.ok(gameBytes > 2.5 * 1024 ** 3 && gameBytes < 3 * 1024 ** 3, `game folder is ${(gameBytes / 1024 ** 3).toFixed(2)} GiB`);
  // without decompile, the panorama files stay prebuilt
  assert.equal(summarizePlanForTest(planImport("next_models", path, false).items).decompile, 0);
});
