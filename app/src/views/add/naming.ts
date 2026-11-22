// Names for new projects: the addon folder name rule from workshop.json, folder names, repo names from URLs.

/** workshop.json "addon": the folder name under csgo_addons. */
export const ADDON_PATTERN = /^[a-z0-9_]{1,64}$/;

export function isValidAddon(addon: string): boolean {
  return ADDON_PATTERN.test(addon);
}

/** "Next HUD v2!" -> "next_hud_v2". Letters outside a-z0-9 become underscores, runs collapse, max 64. */
export function addonFromTitle(title: string): string {
  return title
    .normalize("NFKD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .slice(0, 64)
    .replace(/_+$/g, "");
}

/** The repo folder for an addon: "next_hud" -> "next-hud". */
export function folderFromAddon(addon: string): string {
  return addon.replace(/_/g, "-");
}

/**
 * The folder git would clone into: "https://github.com/example/next-hud.git" -> "next-hud",
 * "git@github.com:example/next-hud.git" -> "next-hud". Empty when the URL has no usable name.
 */
export function repoNameFromUrl(url: string): string {
  const trimmed = url.trim().replace(/[?#].*$/, "").replace(/[\\/]+$/, "");
  const last = trimmed.split(/[/:\\]/).pop() ?? "";
  const name = last.replace(/\.git$/i, "");
  return /^[\w.-]+$/.test(name) && name !== "." && name !== ".." ? name : "";
}

/** Folder names Windows refuses (or that break tools): reserved device names and forbidden characters. */
export function isValidFolderName(name: string): boolean {
  if (name.trim().length === 0 || name !== name.trim()) return false;
  if (/[<>:"/\\|?*\u0000-\u001f]/.test(name)) return false;
  if (/[. ]$/.test(name)) return false;
  return !/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$/i.test(name);
}
