// The navigation pane: projects on top, doctor and settings at the bottom. The selected pill (with the small
// accent bar on its left edge) slides between items with a shared layoutId, like Windows 11 NavigationView.
import type { ReactNode } from "react";
import { Button, Skeleton, Tooltip } from "@heroui/react";
import { motion } from "motion/react";
import { ArrowDown, ArrowUp, CircleAlert, CircleCheck, CircleX, FolderX, Plus, Settings, Stethoscope } from "lucide-react";
import type { CheckStatus, RecentProject } from "../rpc/types";
import { ProjectAvatar } from "../components/ProjectAvatar";
import { doctorLevel, useDoctor, useProjects, useSidebarGitStatus } from "./queries";
import { samePath, type Route } from "./route";

interface SidebarProps {
  route: Route;
  onNavigate: (route: Route) => void;
  onAddProject: () => void;
}

export function Sidebar({ route, onNavigate, onAddProject }: SidebarProps) {
  const projects = useProjects();
  const doctor = useDoctor();
  const git = useSidebarGitStatus(projects.data);
  const level = doctorLevel(doctor.data);

  return (
    <aside className="flex w-[260px] shrink-0 flex-col select-none" aria-label="Navigation">
      <div className="flex items-center justify-between pt-3 pr-3 pb-1 pl-5">
        <span className="text-caption font-semibold text-muted">Projects</span>
        <Tooltip delay={500} closeDelay={0}>
          <Button isIconOnly size="sm" variant="ghost" aria-label="Add project" onPress={onAddProject}>
            <Plus />
          </Button>
          <Tooltip.Content placement="right">Add project</Tooltip.Content>
        </Tooltip>
      </div>

      <nav className="flex min-h-0 flex-1 flex-col gap-0.5 overflow-y-auto px-2 pb-2">
        {projects.isPending ? (
          <ProjectListSkeleton />
        ) : projects.data && projects.data.length > 0 ? (
          projects.data.map((project) => (
            <ProjectNavItem
              key={project.path}
              project={project}
              selected={route.kind === "project" && samePath(route.path, project.path)}
              status={git.get(project.path)}
              onSelect={() => onNavigate({ kind: "project", path: project.path })}
            />
          ))
        ) : (
          <button
            type="button"
            onClick={onAddProject}
            className="mx-1 mt-1 flex flex-col items-start gap-0.5 rounded-[4px] bg-subtle-hover px-3 py-2.5 text-left transition-colors duration-200 hover:bg-subtle-selected"
          >
            <span className="text-body">No projects yet</span>
            <span className="text-caption text-muted">Create one, clone a repo or import a CS2 addon.</span>
          </button>
        )}
      </nav>

      <div className="flex flex-col gap-0.5 px-2 pt-1 pb-2">
        <NavItem selected={route.kind === "doctor"} onSelect={() => onNavigate({ kind: "doctor" })} icon={<Stethoscope className="size-4" />}>
          <span className="flex-1">Doctor</span>
          <DoctorDot level={level} loading={doctor.isPending} />
        </NavItem>
        <NavItem selected={route.kind === "settings"} onSelect={() => onNavigate({ kind: "settings" })} icon={<Settings className="size-4" />}>
          <span className="flex-1">Settings</span>
        </NavItem>
      </div>
    </aside>
  );
}

interface NavItemProps {
  selected: boolean;
  onSelect: () => void;
  icon: ReactNode;
  children: ReactNode;
  tall?: boolean;
  dimmed?: boolean;
}

function NavItem({ selected, onSelect, icon, children, tall = false, dimmed = false }: NavItemProps) {
  return (
    <button
      type="button"
      aria-current={selected ? "page" : undefined}
      onClick={onSelect}
      className={`group relative flex w-full items-center gap-3 rounded-[4px] px-3 text-left text-body outline-none transition-colors duration-200 focus-visible:ring-2 focus-visible:ring-focus ${
        tall ? "py-1.5" : "h-9"
      } ${selected ? "" : "hover:bg-subtle-hover active:bg-subtle-pressed"} ${dimmed ? "text-muted" : "text-foreground"}`}
    >
      {selected && (
        <motion.span
          layoutId="nav-selected"
          className="absolute inset-0 rounded-[4px] bg-subtle-selected"
          transition={{ type: "spring", bounce: 0.12, duration: 0.32 }}
        >
          <span className="absolute top-1/2 left-0 h-4 w-[3px] -translate-y-1/2 rounded-full bg-accent" />
        </motion.span>
      )}
      <span className="relative flex shrink-0 items-center text-fg-secondary">{icon}</span>
      <span className="relative flex min-w-0 flex-1 items-center gap-2">{children}</span>
    </button>
  );
}

interface ProjectNavItemProps {
  project: RecentProject;
  selected: boolean;
  status: { dirty: boolean; ahead: number; behind: number; isRepo: boolean } | undefined;
  onSelect: () => void;
}

function ProjectNavItem({ project, selected, status, onSelect }: ProjectNavItemProps) {
  return (
    <NavItem
      tall
      selected={selected}
      onSelect={onSelect}
      dimmed={!project.exists}
      icon={
        project.exists ? (
          <ProjectAvatar title={project.title} seed={project.addon} />
        ) : (
          <span className="flex size-7 items-center justify-center rounded-[4px] bg-default text-muted">
            <FolderX className="size-4" />
          </span>
        )
      }
    >
      <span className="flex min-w-0 flex-1 flex-col">
        <span className="truncate leading-5">{project.title || project.addon}</span>
        <span className="truncate text-caption text-muted">{project.exists ? project.addon : "Folder not found"}</span>
      </span>
      {status && (status.ahead > 0 || status.behind > 0 || status.dirty) && (
        <span className="flex shrink-0 items-center gap-1.5 text-caption text-muted">
          {status.ahead > 0 && (
            <span className="flex items-center" title={`${status.ahead} to push`}>
              <ArrowUp className="size-3" />
              {status.ahead}
            </span>
          )}
          {status.behind > 0 && (
            <span className="flex items-center" title={`${status.behind} to pull`}>
              <ArrowDown className="size-3" />
              {status.behind}
            </span>
          )}
          {status.dirty && <span className="size-2 rounded-full bg-accent" title="Uncommitted changes" />}
        </span>
      )}
    </NavItem>
  );
}

function DoctorDot({ level, loading }: { level: CheckStatus | null; loading: boolean }) {
  if (loading) return <Skeleton className="size-4 rounded-full" />;
  if (level === "error") return <CircleX className="size-4 text-danger" aria-label="Problems found" />;
  if (level === "warn") return <CircleAlert className="size-4 text-warning" aria-label="Warnings" />;
  if (level === "ok") return <CircleCheck className="size-4 text-success" aria-label="All checks passed" />;
  return null;
}

function ProjectListSkeleton() {
  return (
    <div className="flex flex-col gap-0.5" aria-hidden>
      {[0, 1, 2].map((index) => (
        <div key={index} className="flex items-center gap-3 px-3 py-1.5">
          <Skeleton className="size-7 shrink-0" />
          <div className="flex flex-1 flex-col gap-1.5">
            <Skeleton className="h-3 w-3/5" />
            <Skeleton className="h-2.5 w-2/5" />
          </div>
        </div>
      ))}
    </div>
  );
}
