// Where the shell is: a project, the doctor, the app settings, or the empty home.
export type Route = { kind: "home" } | { kind: "project"; path: string } | { kind: "doctor" } | { kind: "settings" };

export const LAST_PROJECT_KEY = "addonstudio.lastProject";

export function routeKey(route: Route): string {
  return route.kind === "project" ? `project:${route.path.toLowerCase()}` : route.kind;
}

/** Windows paths compare ignoring case and trailing separators. */
export function samePath(a: string, b: string): boolean {
  const norm = (path: string) => path.replace(/[\\/]+$/, "").replace(/\//g, "\\").toLowerCase();
  return norm(a) === norm(b);
}

export function readLastProject(): string | null {
  try {
    return localStorage.getItem(LAST_PROJECT_KEY);
  } catch {
    return null;
  }
}

export function writeLastProject(path: string | null): void {
  try {
    if (path) localStorage.setItem(LAST_PROJECT_KEY, path);
    else localStorage.removeItem(LAST_PROJECT_KEY);
  } catch {
    // a per-viewer convenience only
  }
}
