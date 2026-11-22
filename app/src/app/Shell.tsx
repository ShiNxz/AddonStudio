// The app frame: navigation pane on the Mica backdrop, the content pane on a layer fill, quick enter-only
// page transitions, the add project modal and the first-run banner.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { motion, MotionConfig } from "motion/react";
import type { ProjectInfo, RecentProject } from "../rpc/types";
import type { ProjectTab } from "../views/project/types";
import { AddProjectModal, type AddMode } from "../views/add/AddProjectModal";
import { DoctorView } from "../views/doctor/DoctorView";
import { SettingsView } from "../views/settings/SettingsView";
import { HomeView } from "../views/projects/HomeView";
import { Sidebar } from "./Sidebar";
import { ProjectArea } from "./ProjectArea";
import { FirstRunBanner } from "./FirstRunBanner";
import { keys, useProjects, useSettings } from "./queries";
import { readLastProject, routeKey, samePath, writeLastProject, type Route } from "./route";
import { useApplyTheme, useMica } from "./theme";

export function Shell() {
  const queryClient = useQueryClient();
  const projects = useProjects();
  const settings = useSettings();
  useApplyTheme(settings.data?.theme);
  useMica();

  const [route, setRoute] = useState<Route>(() => {
    const last = readLastProject();
    return last ? { kind: "project", path: last } : { kind: "home" };
  });
  const [tabs, setTabs] = useState<Record<string, ProjectTab>>({});
  const [addMode, setAddMode] = useState<AddMode | null>(null);

  // once, when the list first arrives: drop a remembered project that is no longer listed, or open the first one.
  // Later changes (add, remove) set the route themselves.
  const checkedStart = useRef(false);
  useEffect(() => {
    if (!projects.data || checkedStart.current) return;
    checkedStart.current = true;
    const first = projects.data.find((entry) => entry.exists);
    if (route.kind === "project" && !projects.data.some((entry) => samePath(entry.path, route.path))) {
      setRoute(first ? { kind: "project", path: first.path } : { kind: "home" });
    } else if (route.kind === "home" && first) {
      setRoute({ kind: "project", path: first.path });
    }
  }, [projects.data, route]);

  useEffect(() => {
    if (route.kind === "project") writeLastProject(route.path);
  }, [route]);

  const navigate = useCallback((next: Route) => setRoute(next), []);

  const openProject = useCallback(
    (project: ProjectInfo) => {
      queryClient.setQueryData(keys.project(project.path), project);
      // show it in the sidebar right away; the refetch brings the core's own entry
      queryClient.setQueryData<RecentProject[]>(keys.projects, (list = []) =>
        list.some((entry) => samePath(entry.path, project.path))
          ? list
          : [{ path: project.path, addon: project.config.addon, title: project.config.title, lastOpened: new Date().toISOString(), exists: true }, ...list],
      );
      void queryClient.invalidateQueries({ queryKey: keys.projects });
      setRoute({ kind: "project", path: project.path });
    },
    [queryClient],
  );

  const onRemoved = useCallback(() => {
    const list = queryClient.getQueryData<typeof projects.data>(keys.projects) ?? [];
    const next = list.find((entry) => entry.exists && !(route.kind === "project" && samePath(entry.path, route.path)));
    if (!next) writeLastProject(null);
    setRoute(next ? { kind: "project", path: next.path } : { kind: "home" });
  }, [queryClient, route]);

  const recent = useMemo(
    () => (route.kind === "project" ? projects.data?.find((entry) => samePath(entry.path, route.path)) : undefined),
    [projects.data, route],
  );

  const page = (() => {
    switch (route.kind) {
      case "project":
        return (
          <ProjectArea
            key={route.path.toLowerCase()}
            path={route.path}
            recent={recent}
            tab={tabs[route.path.toLowerCase()] ?? "changes"}
            onTabChange={(tab) => setTabs((current) => ({ ...current, [route.path.toLowerCase()]: tab }))}
            onRemoved={onRemoved}
          />
        );
      case "doctor":
        return <DoctorView />;
      case "settings":
        return <SettingsView />;
      default:
        return <HomeView onAdd={setAddMode} onOpen={(path) => navigate({ kind: "project", path })} />;
    }
  })();

  return (
    <MotionConfig reducedMotion="user">
      <div className="flex h-full text-foreground">
        <Sidebar route={route} onNavigate={navigate} onAddProject={() => setAddMode("create")} />
        <main className="flex min-w-0 flex-1 flex-col overflow-hidden rounded-tl-lg bg-layer">
          {route.kind !== "doctor" && <FirstRunBanner onOpenDoctor={() => navigate({ kind: "doctor" })} />}
          <motion.div
            key={routeKey(route)}
            initial={{ opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.18, ease: [0.2, 0, 0, 1] }}
            className="flex min-h-0 flex-1 flex-col"
          >
            {page}
          </motion.div>
        </main>
      </div>
      <AddProjectModal mode={addMode} onModeChange={setAddMode} onClose={() => setAddMode(null)} onOpened={openProject} />
    </MotionConfig>
  );
}
