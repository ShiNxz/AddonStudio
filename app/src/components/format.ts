// Formatting helpers shared by the shell and the views.

const UNITS = ["B", "KB", "MB", "GB", "TB"] as const;

/** 1536 -> "1.5 KB", 3221225472 -> "3 GB" (binary units, shown the way Windows Explorer does). */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) return "-";
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const digits = unit === 0 || value >= 100 ? 0 : value >= 10 ? 1 : 2;
  return `${Number(value.toFixed(digits))} ${UNITS[unit]}`;
}

export const GIB = 1024 * 1024 * 1024;

/** "1 file", "3 files". */
export function plural(count: number, one: string, many = `${one}s`): string {
  return `${count.toLocaleString()} ${count === 1 ? one : many}`;
}

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

/** "2 minutes ago", "yesterday", or a date when older than a month. */
export function formatRelative(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "-";
  const time = Date.parse(iso);
  if (Number.isNaN(time)) return iso;
  const seconds = Math.round((time - now) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 45) return "just now";
  if (abs < 3600) return relative.format(Math.round(seconds / 60), "minute");
  if (abs < 86_400) return relative.format(Math.round(seconds / 3600), "hour");
  if (abs < 86_400 * 30) return relative.format(Math.round(seconds / 86_400), "day");
  return formatDate(iso);
}

export function formatDate(iso: string | null | undefined): string {
  if (!iso) return "-";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "-";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString(undefined, { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

export function formatDuration(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(seconds < 10 ? 1 : 0)} s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes} min ${Math.round(seconds % 60)} s`;
}
