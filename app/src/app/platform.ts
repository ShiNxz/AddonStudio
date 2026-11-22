// Small bridges to the desktop host: folder picker, external links, Mica, native window theme.
// Every function has a browser fallback so `npm run dev` works without Tauri.
import { isTauri } from "../rpc/client";

export { isTauri };

/** Opens a URL in the user's browser (Tauri opener plugin, or a new tab in the browser). */
export async function openExternal(url: string): Promise<void> {
  if (isTauri()) {
    const { openUrl } = await import("@tauri-apps/plugin-opener");
    await openUrl(url);
    return;
  }
  window.open(url, "_blank", "noopener,noreferrer");
}

export function steamItemUrl(workshopId: string): string {
  return `https://steamcommunity.com/sharedfiles/filedetails/?id=${encodeURIComponent(workshopId)}`;
}

/** Native folder picker. Returns null when cancelled, or when there is no picker (browser: type the path instead). */
export async function pickFolder(title: string, defaultPath?: string | null): Promise<string | null> {
  if (!isTauri()) return null;
  const { open } = await import("@tauri-apps/plugin-dialog");
  const picked = await open({ directory: true, multiple: false, title, defaultPath: defaultPath || undefined });
  return typeof picked === "string" ? picked : null;
}

/** True when a native folder picker exists. */
export function canPickFolders(): boolean {
  return isTauri();
}

/** Joins a folder and a name with the separator the folder already uses (Windows paths keep backslashes). */
export function joinPath(folder: string, name: string): string {
  const trimmed = folder.replace(/[\\/]+$/, "");
  if (trimmed.length === 0) return name;
  const separator = trimmed.includes("\\") || /^[a-zA-Z]:$/.test(trimmed) ? "\\" : "/";
  return `${trimmed}${separator}${name}`;
}

/** The last segment of a path or URL ("C:\\a\\b" -> "b"). */
export function baseName(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, "");
  const index = Math.max(trimmed.lastIndexOf("\\"), trimmed.lastIndexOf("/"));
  return index >= 0 ? trimmed.slice(index + 1) : trimmed;
}

/** The parent folder of a path ("C:\\a\\b" -> "C:\\a"). */
export function parentPath(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, "");
  const index = Math.max(trimmed.lastIndexOf("\\"), trimmed.lastIndexOf("/"));
  return index > 0 ? trimmed.slice(0, index) : trimmed;
}

/**
 * Mica needs Windows 11 (build 22000+) and a transparent Tauri window. WebView2 reports the Windows 11 platform
 * version as 13 or higher through User-Agent Client Hints; Windows 10 reports 1 to 10.
 */
export async function detectMica(): Promise<boolean> {
  if (!isTauri()) return false;
  const nav = navigator as Navigator & {
    userAgentData?: { platform?: string; getHighEntropyValues?: (hints: string[]) => Promise<{ platformVersion?: string }> };
  };
  if (!/windows/i.test(nav.userAgentData?.platform ?? navigator.userAgent)) return false;
  try {
    const values = await nav.userAgentData?.getHighEntropyValues?.(["platformVersion"]);
    const major = Number.parseInt(values?.platformVersion?.split(".")[0] ?? "0", 10);
    return major >= 13;
  } catch {
    return false;
  }
}

/** Makes the native title bar follow the app theme (null = follow the system). Ignored outside Tauri. */
export async function setNativeTheme(theme: "dark" | "light" | null): Promise<void> {
  if (!isTauri()) return;
  try {
    const { getCurrentWindow } = await import("@tauri-apps/api/window");
    await getCurrentWindow().setTheme(theme);
  } catch (error) {
    console.debug("setTheme is not allowed or not supported", error);
  }
}
