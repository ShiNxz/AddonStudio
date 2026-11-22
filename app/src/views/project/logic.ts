// Pure helpers for the project views. Only type imports here, so `node --test` can load this file as is.
import type { FileChange, GitFileChange, GitState, PackFile, PackOrigin, WorkshopConfig } from "../../rpc/types";

/* ---------- formatting ---------- */

const SIZE_UNITS = ["B", "KB", "MB", "GB", "TB"];

/** 1024-based like Explorer: "0 B", "17.9 KB", "1.5 MB", "3 GB". */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 1024) {
    return `${Math.max(0, Math.round(bytes ?? 0))} B`;
  }
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < SIZE_UNITS.length - 1) {
    value /= 1024;
    unit++;
  }
  const text = value >= 100 ? String(Math.round(value)) : value >= 10 ? value.toFixed(1).replace(/\.0$/, "") : value.toFixed(2).replace(/\.?0+$/, "");
  return `${text} ${SIZE_UNITS[unit]}`;
}

/** "850 ms", "4.2 s", "2 min 5 s". */
export function formatDuration(ms: number): string {
  if (ms < 1000) return `${Math.max(0, Math.round(ms))} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1).replace(/\.0$/, "")} s`;
  const minutes = Math.floor(ms / 60_000);
  const seconds = Math.round((ms % 60_000) / 1000);
  return seconds === 0 ? `${minutes} min` : `${minutes} min ${seconds} s`;
}

/** "just now", "5 minutes ago", "yesterday", then a short date after a month. */
export function relativeTime(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return "never";
  const time = Date.parse(iso);
  if (Number.isNaN(time)) return iso;
  const seconds = Math.round((now - time) / 1000);
  if (seconds < 45) return "just now";
  const rtf = new Intl.RelativeTimeFormat("en", { numeric: "auto" });
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return rtf.format(-minutes, "minute");
  const hours = Math.round(minutes / 60);
  if (hours < 24) return rtf.format(-hours, "hour");
  const days = Math.round(hours / 24);
  if (days < 30) return rtf.format(-days, "day");
  return new Date(time).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" });
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "-";
  const time = Date.parse(iso);
  if (Number.isNaN(time)) return iso;
  return new Date(time).toLocaleString("en-GB", { dateStyle: "medium", timeStyle: "short" });
}

export function plural(count: number, one: string, many = `${one}s`): string {
  return `${count.toLocaleString("en-US")} ${count === 1 ? one : many}`;
}

/* ---------- paths and links ---------- */

/** Windows paths compare ignoring case and slash style. */
export function samePath(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) return false;
  const norm = (p: string) => p.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
  return norm(a) === norm(b);
}

/** "a/b/c.txt" -> { dir: "a/b/", name: "c.txt" } (either slash). */
export function splitPath(path: string): { dir: string; name: string } {
  const cut = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
  return cut < 0 ? { dir: "", name: path } : { dir: path.slice(0, cut + 1), name: path.slice(cut + 1) };
}

export function workshopUrl(workshopId: string): string {
  return `https://steamcommunity.com/sharedfiles/filedetails/?id=${encodeURIComponent(workshopId)}`;
}

/* ---------- compiler logs ---------- */

export type LogTone = "error" | "warning" | null;

/** Same rule as the core (SPEC 4): ERROR / Error: / FAILED / failed to compile are errors, WARNING lines warnings. */
export function classifyLogLine(line: string): LogTone {
  if (/error|failed/i.test(line)) return "error";
  if (/warning/i.test(line)) return "warning";
  return null;
}

/* ---------- build queue (auto-build) ---------- */

export type QueueOutcome = "started" | "queued" | "dropped";

/**
 * Runs one build at a time. While one runs, a queued submit keeps exactly one task waiting (a newer one replaces
 * it), an unqueued submit is dropped. The waiting task starts when the running one settles, even if it failed.
 */
export class BuildQueue {
  private running = false;
  private pending: (() => Promise<unknown>) | null = null;
  private readonly listeners = new Set<() => void>();

  get isBusy(): boolean {
    return this.running;
  }

  get hasPending(): boolean {
    return this.pending !== null;
  }

  submit(task: () => Promise<unknown>, queueIfBusy: boolean): QueueOutcome {
    if (this.running) {
      if (!queueIfBusy) return "dropped";
      this.pending = task;
      this.notify();
      return "queued";
    }
    void this.start(task);
    return "started";
  }

  /** Forgets the waiting task (the running one keeps going). */
  clearPending(): void {
    if (this.pending === null) return;
    this.pending = null;
    this.notify();
  }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private async start(task: () => Promise<unknown>): Promise<void> {
    this.running = true;
    this.notify();
    try {
      await task();
    } catch {
      // the task reports its own errors
    } finally {
      this.running = false;
      const next = this.pending;
      this.pending = null;
      if (next) void this.start(next);
      else this.notify();
    }
  }

  private notify(): void {
    for (const listener of [...this.listeners]) listener();
  }
}

/* ---------- git ---------- */

export function gitStateLetter(state: GitState): string {
  switch (state) {
    case "modified":
      return "M";
    case "added":
      return "A";
    case "deleted":
      return "D";
    case "renamed":
      return "R";
    case "copied":
      return "C";
    case "untracked":
      return "U";
    case "conflicted":
      return "!";
    case "typeChanged":
      return "T";
    case "ignored":
      return "I";
    default:
      return " ";
  }
}

export type Tone = "success" | "warning" | "danger" | "accent" | "muted";

export function gitStateTone(state: GitState): Tone {
  switch (state) {
    case "added":
    case "untracked":
      return "success";
    case "modified":
    case "typeChanged":
      return "warning";
    case "deleted":
    case "conflicted":
      return "danger";
    case "renamed":
    case "copied":
      return "accent";
    default:
      return "muted";
  }
}

export function gitStateLabel(state: GitState): string {
  return state === "typeChanged" ? "type changed" : state;
}

/**
 * Staged = in the index. Changes = not staged at all, or changed again in the work tree after staging
 * (a partly staged file shows in both groups, like git does).
 */
export function splitChanges(files: GitFileChange[]): { staged: GitFileChange[]; changes: GitFileChange[] } {
  const staged = files.filter((file) => file.staged);
  const changes = files.filter((file) => !file.staged || (file.worktree !== "unmodified" && file.worktree !== "ignored"));
  return { staged, changes };
}

/** The state a row shows: the index side for staged rows, the work tree side otherwise. */
export function shownState(file: GitFileChange, group: "staged" | "changes"): GitState {
  if (group === "staged") return file.index;
  return file.worktree === "unmodified" ? file.index : file.worktree;
}

/** The version a commit subject would auto-publish, null when it does not match or the pattern is invalid. */
export function autoPublishVersion(pattern: string, subject: string): string | null {
  try {
    const match = new RegExp(pattern).exec(subject);
    return match ? (match[1] ?? null) : null;
  } catch {
    return null;
  }
}

/* ---------- package ---------- */

export interface FolderSummary {
  /** The first `depth` folders of the path with a trailing "/", "" for files at the top of the build folder. */
  folder: string;
  count: number;
  size: number;
}

/** Build-relative files grouped by their first `depth` folders, biggest count first: the folders a list of files comes from, at a glance. */
export function folderSummary(files: { path: string; size: number }[], depth = 2): FolderSummary[] {
  const groups = new Map<string, FolderSummary>();
  for (const file of files) {
    const folders = file.path.split("/").slice(0, -1).slice(0, depth);
    const folder = folders.length === 0 ? "" : `${folders.join("/")}/`;
    const key = folder.toLowerCase();
    const group = groups.get(key) ?? { folder, count: 0, size: 0 };
    group.count++;
    group.size += file.size;
    groups.set(key, group);
  }
  return [...groups.values()].sort((a, b) => b.count - a.count || b.size - a.size || a.folder.localeCompare(b.folder));
}

export type PackSortKey = "path" | "size";

export interface PackFilter {
  search: string;
  origin: PackOrigin | "all";
  change: FileChange | "all";
  sort: PackSortKey;
  descending: boolean;
}

/** Search terms (space separated) must all appear in the path, case-insensitive. */
export function filterPackFiles(files: PackFile[], filter: PackFilter): PackFile[] {
  const terms = filter.search.toLowerCase().split(/\s+/).filter(Boolean);
  const out = files.filter(
    (file) =>
      (filter.origin === "all" || file.origin === filter.origin) &&
      (filter.change === "all" || file.change === filter.change) &&
      terms.every((term) => file.path.toLowerCase().includes(term)),
  );
  const sign = filter.descending ? -1 : 1;
  out.sort((a, b) => {
    const primary = filter.sort === "size" ? a.size - b.size : a.path.localeCompare(b.path, "en", { sensitivity: "base" });
    return primary !== 0 ? primary * sign : a.path.localeCompare(b.path);
  });
  return out;
}

/* ---------- workshop.json validation ---------- */

export type ConfigField = "addon" | "workshopId" | "title" | "description" | "tags" | "preview" | "include" | "exclude" | "pattern";
export type ConfigErrors = Partial<Record<ConfigField, string>>;

const isAbsolute = (path: string) => /^[a-zA-Z]:/.test(path) || /^[\\/]/.test(path);
const hasParentSegment = (path: string) => path.split(/[\\/]/).includes("..");

function globError(globs: string[]): string | undefined {
  for (const glob of globs) {
    if (glob.trim() === "") return "Remove the empty rule.";
    if (glob.includes("\\")) return `Use / in "${glob}", not \\.`;
    if (isAbsolute(glob) || hasParentSegment(glob)) return `"${glob}" must be relative to the build folder.`;
  }
  const seen = new Set<string>();
  for (const glob of globs) {
    if (seen.has(glob)) return `"${glob}" is there twice.`;
    seen.add(glob);
  }
  return undefined;
}

/** Number of capture groups a pattern has, or null when it does not compile. */
export function regexGroupCount(pattern: string): number | null {
  try {
    return (new RegExp(`${pattern}|`).exec("")?.length ?? 1) - 1;
  } catch {
    return null;
  }
}

export function validateConfig(config: WorkshopConfig): ConfigErrors {
  const errors: ConfigErrors = {};

  if (!/^[a-z0-9_]{1,64}$/.test(config.addon)) errors.addon = "Use 1 to 64 lowercase letters, digits or _.";
  if (config.workshopId !== null && !/^\d{1,20}$/.test(config.workshopId)) errors.workshopId = "Digits only, or leave it empty.";

  if (config.title.trim() === "") errors.title = "A title is needed.";
  else if (config.title.length > 128) errors.title = "Steam allows 128 characters.";

  if (config.description !== null && config.description.length > 8000) errors.description = "Steam allows 8000 characters.";

  const tagSeen = new Set<string>();
  for (const tag of config.tags) {
    const key = tag.trim().toLowerCase();
    if (key === "") {
      errors.tags = "Remove the empty tag.";
      break;
    }
    if (tag.length > 255) {
      errors.tags = "Tags are at most 255 characters.";
      break;
    }
    if (tagSeen.has(key)) {
      errors.tags = `"${tag}" is there twice.`;
      break;
    }
    tagSeen.add(key);
  }

  if (config.preview !== null && config.preview !== "") {
    if (isAbsolute(config.preview) || hasParentSegment(config.preview)) errors.preview = "Use a path inside the repo, like preview.png.";
    else if (!/\.(png|jpe?g|gif)$/i.test(config.preview)) errors.preview = "Use a .png, .jpg or .gif image.";
  }

  const include = globError(config.pack.include);
  if (include) errors.include = include;
  const exclude = globError(config.pack.exclude);
  if (exclude) errors.exclude = exclude;

  if (config.autoPublish.enabled || config.autoPublish.pattern !== "") {
    const groups = regexGroupCount(config.autoPublish.pattern);
    if (config.autoPublish.pattern === "") errors.pattern = "A pattern is needed for auto-publish.";
    else if (groups === null) errors.pattern = "This is not a valid regular expression.";
    else if (groups < 1) errors.pattern = "Add a group ( ) around the version.";
  }

  return errors;
}
