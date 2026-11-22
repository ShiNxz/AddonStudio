// The start page when no project is open: the three ways to add one, and recent projects.
import type { LucideIcon } from "lucide-react";
import { ChevronRight, FolderPlus, GitFork, Import } from "lucide-react";
import { Skeleton } from "@heroui/react";
import type { AddMode } from "../add/AddProjectModal";
import { ProjectAvatar } from "../../components/ProjectAvatar";
import { formatRelative } from "../../components/format";
import { useProjects } from "../../app/queries";

interface HomeViewProps {
  onAdd: (mode: AddMode) => void;
  onOpen: (path: string) => void;
}

const ACTIONS: { mode: AddMode; icon: LucideIcon; title: string; text: string }[] = [
  { mode: "create", icon: FolderPlus, title: "Create a new addon", text: "A git repo with content/, prebuilt/ and workshop.json." },
  { mode: "clone", icon: GitFork, title: "Clone a repo", text: "Get an addon project from GitHub or any git URL." },
  { mode: "import", icon: Import, title: "Import a CS2 addon", text: "Turn an addon from csgo_addons into a project." },
];

export function HomeView({ onAdd, onOpen }: HomeViewProps) {
  const projects = useProjects();
  const recent = (projects.data ?? []).filter((project) => project.exists);

  return (
    <div className="flex-1 overflow-y-auto">
      <div className="mx-auto flex max-w-3xl flex-col gap-8 px-8 py-10">
        <div className="flex flex-col gap-1">
          <h1 className="text-title font-semibold">Addon Studio</h1>
          <p className="text-body text-fg-secondary">
            CS2 Workshop addons as git repos. Edit sources, commit, build only what changed and publish with one button.
          </p>
        </div>

        <section className="flex flex-col gap-2">
          <h2 className="text-body font-semibold">Add a project</h2>
          <div className="grid grid-cols-1 gap-1 lg:grid-cols-3">
            {ACTIONS.map(({ mode, icon: Icon, title, text }) => (
              <button
                key={mode}
                type="button"
                onClick={() => onAdd(mode)}
                className="group flex items-start gap-3 rounded-lg bg-surface p-4 text-left shadow-card transition-colors duration-200 outline-none hover:bg-surface-hover focus-visible:ring-2 focus-visible:ring-focus active:bg-subtle-pressed"
              >
                <span className="flex size-9 shrink-0 items-center justify-center rounded-md bg-accent-soft text-accent">
                  <Icon className="size-[18px]" />
                </span>
                <span className="flex min-w-0 flex-col gap-0.5">
                  <span className="text-body font-semibold">{title}</span>
                  <span className="text-caption text-muted">{text}</span>
                </span>
              </button>
            ))}
          </div>
        </section>

        {(projects.isPending || recent.length > 0) && (
          <section className="flex flex-col gap-2">
            <h2 className="text-body font-semibold">Recent</h2>
            <div className="flex flex-col gap-1">
              {projects.isPending
                ? [0, 1].map((index) => <Skeleton key={index} className="h-[60px] rounded-lg" />)
                : recent.map((project) => (
                    <button
                      key={project.path}
                      type="button"
                      onClick={() => onOpen(project.path)}
                      className="group flex items-center gap-3 rounded-lg bg-surface px-4 py-3 text-left shadow-card transition-colors duration-200 outline-none hover:bg-surface-hover focus-visible:ring-2 focus-visible:ring-focus"
                    >
                      <ProjectAvatar title={project.title} seed={project.addon} size="md" />
                      <span className="flex min-w-0 flex-1 flex-col">
                        <span className="truncate text-body">{project.title}</span>
                        <span className="truncate text-caption text-muted">{project.path}</span>
                      </span>
                      <span className="shrink-0 text-caption text-muted">{formatRelative(project.lastOpened)}</span>
                      <ChevronRight className="size-4 shrink-0 text-muted transition-colors duration-200 group-hover:text-foreground" />
                    </button>
                  ))}
            </div>
          </section>
        )}
      </div>
    </div>
  );
}
