// React Query keys and hooks for the app-wide data the shell shows.
import { useQueries, useQuery } from "@tanstack/react-query";
import { call } from "../rpc/client";
import type { CheckStatus, DoctorReport, RecentProject } from "../rpc/types";

export const keys = {
  appInfo: ["app.info"] as const,
  doctor: ["env.doctor"] as const,
  settings: ["settings.get"] as const,
  projects: ["projects.list"] as const,
  project: (path: string) => ["projects.open", path] as const,
  gitStatus: (path: string) => ["git.status", path] as const,
  cs2Addons: ["projects.cs2Addons"] as const,
};

export function useAppInfo() {
  return useQuery({ queryKey: keys.appInfo, queryFn: () => call("app.info"), staleTime: Infinity });
}

export function useDoctor() {
  return useQuery({ queryKey: keys.doctor, queryFn: () => call("env.doctor"), staleTime: 60_000 });
}

export function useSettings() {
  return useQuery({ queryKey: keys.settings, queryFn: () => call("settings.get"), staleTime: 30_000 });
}

export function useProjects() {
  return useQuery({ queryKey: keys.projects, queryFn: () => call("projects.list"), staleTime: 30_000 });
}

/** git.status for every project in the sidebar (dirty dot, ahead/behind). Failures stay quiet. */
export function useSidebarGitStatus(projects: RecentProject[] | undefined) {
  const list = (projects ?? []).filter((project) => project.exists);
  return useQueries({
    queries: list.map((project) => ({
      queryKey: keys.gitStatus(project.path),
      queryFn: () => call("git.status", { path: project.path }),
      staleTime: 10_000,
      refetchInterval: 20_000,
      retry: false,
      meta: { silent: true },
    })),
    combine: (results) => {
      const map = new Map<string, { dirty: boolean; ahead: number; behind: number; isRepo: boolean }>();
      results.forEach((result, index) => {
        const project = list[index];
        if (!project || !result.data) return;
        map.set(project.path, {
          dirty: result.data.files.length > 0,
          ahead: result.data.ahead,
          behind: result.data.behind,
          isRepo: result.data.isRepo,
        });
      });
      return map;
    },
  });
}

/** The worst status of the doctor checks ("ok" when everything passed). */
export function doctorLevel(report: DoctorReport | undefined): CheckStatus | null {
  if (!report) return null;
  if (report.checks.some((check) => check.status === "error")) return "error";
  if (report.checks.some((check) => check.status === "warn")) return "warn";
  return "ok";
}
