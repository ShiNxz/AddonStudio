// One open project: header (title, addon, workshop id, branch, CS2 link) and the six tabs.
import { useCallback, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Skeleton } from "@heroui/react";
import { motion } from "motion/react";
import { FolderX, RefreshCw } from "lucide-react";
import { call, isRpcError } from "../rpc/client";
import type { ProjectInfo, RecentProject } from "../rpc/types";
import type { ProjectTab, ProjectViewProps, PublishIntent } from "../views/project/types";
import { ChangesView } from "../views/project/ChangesView";
import { HistoryView } from "../views/project/HistoryView";
import { BuildView } from "../views/project/BuildView";
import { PackageView } from "../views/project/PackageView";
import { PublishView } from "../views/project/PublishView";
import { ProjectSettingsView } from "../views/project/ProjectSettingsView";
import { EmptyState } from "../components/EmptyState";
import { CodeText } from "../components/CodeText";
import { describeError } from "../components/notify";
import { ProjectHeader } from "./ProjectHeader";
import { keys } from "./queries";
import { useRemoveProject } from "./useRemoveProject";

export const PROJECT_TABS: { id: ProjectTab; label: string; View: (props: ProjectViewProps) => React.JSX.Element }[] = [
  { id: "changes", label: "Changes", View: ChangesView },
  { id: "history", label: "History", View: HistoryView },
  { id: "build", label: "Build", View: BuildView },
  { id: "package", label: "Package", View: PackageView },
  { id: "publish", label: "Publish", View: PublishView },
  { id: "settings", label: "Settings", View: ProjectSettingsView },
];

interface ProjectAreaProps {
  path: string;
  recent: RecentProject | undefined;
  tab: ProjectTab;
  onTabChange: (tab: ProjectTab) => void;
  onRemoved: () => void;
}

export function ProjectArea({ path, recent, tab, onTabChange, onRemoved }: ProjectAreaProps) {
  const queryClient = useQueryClient();
  const [intent, setIntent] = useState<PublishIntent | null>(null);

  const project = useQuery({
    queryKey: keys.project(path),
    queryFn: () => call("projects.open", { path }),
    staleTime: 15_000,
    retry: false,
    meta: { silent: true },
  });

  const refreshProject = useCallback(async () => {
    const fresh = await call("projects.open", { path });
    queryClient.setQueryData(keys.project(path), fresh);
  }, [queryClient, path]);

  const goTo = useCallback(
    (next: ProjectTab, nextIntent?: PublishIntent) => {
      setIntent(nextIntent ?? null);
      onTabChange(next);
    },
    [onTabChange],
  );

  if (project.isPending) return <ProjectSkeleton />;

  if (project.isError || !project.data) {
    return <ProjectOpenError path={path} recent={recent} error={project.error} onRetry={() => void project.refetch()} onRemoved={onRemoved} />;
  }

  const active = PROJECT_TABS.find((entry) => entry.id === tab) ?? PROJECT_TABS[0]!;
  const View = active.View;

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <ProjectHeader project={project.data} onUpdated={(next) => queryClient.setQueryData(keys.project(path), next)} onRemoved={onRemoved} goTo={goTo} />
      <ProjectTabs tab={active.id} onChange={(next) => goTo(next)} />
      <div className="min-h-0 flex-1 overflow-y-auto">
        <motion.div
          key={active.id}
          initial={{ opacity: 0, y: 6 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.16, ease: [0.2, 0, 0, 1] }}
          className="min-h-full"
        >
          <View project={project.data as ProjectInfo} refreshProject={refreshProject} goTo={goTo} intent={active.id === "publish" ? intent : null} />
        </motion.div>
      </div>
    </div>
  );
}

function ProjectTabs({ tab, onChange }: { tab: ProjectTab; onChange: (tab: ProjectTab) => void }) {
  return (
    <div role="tablist" aria-label="Project" className="flex shrink-0 items-center gap-1 px-6 pt-1 pb-2 select-none">
      {PROJECT_TABS.map((entry) => {
        const selected = entry.id === tab;
        return (
          <button
            key={entry.id}
            type="button"
            role="tab"
            aria-selected={selected}
            onClick={() => onChange(entry.id)}
            className={`relative h-8 rounded-[4px] px-3 text-body outline-none transition-colors duration-200 focus-visible:ring-2 focus-visible:ring-focus ${
              selected ? "text-foreground" : "text-fg-secondary hover:bg-subtle-hover hover:text-foreground active:bg-subtle-pressed"
            }`}
          >
            {selected && (
              <motion.span
                layoutId="project-tab-selected"
                className="absolute inset-0 rounded-[4px] bg-subtle-selected"
                transition={{ type: "spring", bounce: 0.12, duration: 0.32 }}
              >
                <span className="absolute bottom-0 left-1/2 h-[3px] w-4 -translate-x-1/2 rounded-full bg-accent" />
              </motion.span>
            )}
            <span className="relative">{entry.label}</span>
          </button>
        );
      })}
    </div>
  );
}

function ProjectSkeleton() {
  return (
    <div className="flex flex-1 flex-col" aria-busy="true" aria-label="Opening project">
      <div className="flex items-center gap-4 px-6 pt-5 pb-3">
        <Skeleton className="size-11 rounded-lg" />
        <div className="flex flex-1 flex-col gap-2">
          <Skeleton className="h-5 w-48" />
          <Skeleton className="h-3 w-80" />
        </div>
        <Skeleton className="h-7 w-28" />
      </div>
      <div className="flex gap-2 px-6 pt-1 pb-3">
        {[64, 60, 48, 64, 60, 62].map((width, index) => (
          <Skeleton key={index} className="h-8" style={{ width }} />
        ))}
      </div>
      <div className="flex flex-col gap-3 px-6 pt-2">
        <Skeleton className="h-24 rounded-lg" />
        <Skeleton className="h-40 rounded-lg" />
      </div>
    </div>
  );
}

interface ProjectOpenErrorProps {
  path: string;
  recent: RecentProject | undefined;
  error: unknown;
  onRetry: () => void;
  onRemoved: () => void;
}

function ProjectOpenError({ path, recent, error, onRetry, onRemoved }: ProjectOpenErrorProps) {
  const remove = useRemoveProject(onRemoved);
  const missing = recent?.exists === false || isRpcError(error, "not_found");
  const { code, message } = describeError(error);

  return (
    <div className="flex flex-1 items-center justify-center p-6">
      <EmptyState
        icon={FolderX}
        title={missing ? "This project folder is gone" : "Could not open this project"}
        actions={
          <>
            <Button variant="secondary" onPress={onRetry}>
              <RefreshCw />
              Try again
            </Button>
            <Button variant={missing ? "primary" : "secondary"} isPending={remove.isPending} onPress={() => remove.mutate({ path, title: recent?.title ?? path })}>
              Remove from list
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          <p>
            {missing ? "It was moved, renamed or deleted. Removing it from the list does not touch any files." : message}
            {!missing && code && <span className="ml-1 text-caption">({code})</span>}
          </p>
          <CodeText value={path} copyLabel="Copy path" className="text-left" />
        </div>
      </EmptyState>
    </div>
  );
}
