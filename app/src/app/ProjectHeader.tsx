// Project header: title, addon, workshop id (opens the Steam page), branch, and the CS2 link state with its fix.
import type { ReactNode } from "react";
import { useMutation } from "@tanstack/react-query";
import { Button, Chip, Dropdown, Label, Tooltip } from "@heroui/react";
import { Copy, ExternalLink, GitBranch, Link2, Ellipsis, Trash2, Unlink, CloudOff } from "lucide-react";
import { call } from "../rpc/client";
import type { LinkState, ProjectInfo } from "../rpc/types";
import type { ProjectTab, PublishIntent } from "../views/project/types";
import { ProjectAvatar } from "../components/ProjectAvatar";
import { CodeText } from "../components/CodeText";
import { InfoBar } from "../components/InfoBar";
import { confirm } from "../components/confirm";
import { copyText } from "../components/clipboard";
import { notifyError, notifySuccess } from "../components/notify";
import { openExternal, steamItemUrl } from "./platform";
import { useRemoveProject } from "./useRemoveProject";

const LINK: Record<LinkState, { label: string; color: "success" | "warning" | "danger" | "default"; fixable: boolean }> = {
  linked: { label: "Linked to CS2", color: "success", fixable: false },
  missing: { label: "Not linked", color: "warning", fixable: true },
  conflict: { label: "Folder in the way", color: "danger", fixable: true },
  wrongTarget: { label: "Linked elsewhere", color: "warning", fixable: true },
  noCs2: { label: "CS2 not found", color: "default", fixable: false },
};

interface ProjectHeaderProps {
  project: ProjectInfo;
  onUpdated: (project: ProjectInfo) => void;
  onRemoved: () => void;
  goTo: (tab: ProjectTab, intent?: PublishIntent) => void;
}

export function ProjectHeader({ project, onUpdated, onRemoved, goTo }: ProjectHeaderProps) {
  const { config, link, git } = project;
  const linkInfo = LINK[link.state];
  const remove = useRemoveProject(onRemoved);

  const linkMutation = useMutation({
    mutationFn: async () => {
      const ok = await confirm({
        title: "Link to CS2?",
        tone: link.state === "conflict" ? "warning" : "default",
        confirmLabel: link.state === "conflict" ? "Rename and link" : "Link",
        body: <LinkConfirmBody project={project} />,
      });
      if (!ok) return null;
      return call("projects.link", { path: project.path, backup: true });
    },
    onSuccess: (next) => {
      if (!next) return;
      onUpdated(next);
      if (next.link.state === "linked") {
        notifySuccess("Linked to CS2", link.state === "conflict" ? "The old folder was kept as a backup next to it." : undefined);
      } else {
        notifyError(new Error(next.link.detail ?? `The link is still "${next.link.state}".`), "Could not link to CS2");
      }
    },
  });

  return (
    <header className="flex shrink-0 flex-col gap-3 px-6 pt-5 pb-2 select-none">
      <div className="flex items-start gap-4">
        <ProjectAvatar title={config.title} seed={config.addon} size="lg" />
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <h1 className="truncate text-subtitle font-semibold">{config.title || config.addon}</h1>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-caption text-muted">
            <Meta>
              <code className="text-caption text-fg-secondary">{config.addon}</code>
            </Meta>
            {config.workshopId ? (
              <button
                type="button"
                onClick={() => void openExternal(steamItemUrl(config.workshopId!)).catch((error: unknown) => notifyError(error, "Could not open the Steam page"))}
                className="flex items-center gap-1 rounded-sm text-accent transition-colors duration-200 outline-none hover:underline focus-visible:ring-2 focus-visible:ring-focus"
                title="Open the Workshop page"
              >
                Workshop {config.workshopId}
                <ExternalLink className="size-3" />
              </button>
            ) : (
              <Meta>Not published yet</Meta>
            )}
            {git.isRepo ? (
              <Meta icon={<GitBranch className="size-3" />}>{git.branch ?? "detached"}</Meta>
            ) : (
              <Meta>Not a git repo</Meta>
            )}
            {git.isRepo && !git.remote && <Meta icon={<CloudOff className="size-3" />}>No remote</Meta>}
          </div>
        </div>

        <div className="flex shrink-0 items-center gap-2">
          <Tooltip delay={400} closeDelay={0}>
            <Tooltip.Trigger>
              <Chip size="sm" variant="soft" color={linkInfo.color} className="gap-1">
                {link.state === "linked" ? <Link2 className="size-3" /> : <Unlink className="size-3" />}
                <Chip.Label>{linkInfo.label}</Chip.Label>
              </Chip>
            </Tooltip.Trigger>
            <Tooltip.Content placement="bottom" className="max-w-sm">
              <LinkTooltip project={project} />
            </Tooltip.Content>
          </Tooltip>
          {linkInfo.fixable && (
            <Button size="sm" variant="primary" isPending={linkMutation.isPending} onPress={() => linkMutation.mutate()}>
              <Link2 />
              Link to CS2
            </Button>
          )}
          <Dropdown>
            <Button isIconOnly size="sm" variant="ghost" aria-label="More">
              <Ellipsis />
            </Button>
            <Dropdown.Popover placement="bottom end">
              <Dropdown.Menu
                onAction={(key) => {
                  if (key === "copy") void copyText(project.path).then(() => notifySuccess("Path copied"));
                  if (key === "remove") remove.mutate({ path: project.path, title: config.title || config.addon });
                }}
              >
                <Dropdown.Item id="copy" textValue="Copy folder path">
                  <Copy className="size-4 text-muted" />
                  <Label>Copy folder path</Label>
                </Dropdown.Item>
                <Dropdown.Item id="remove" textValue="Remove from list">
                  <Trash2 className="size-4 text-muted" />
                  <Label>Remove from list</Label>
                </Dropdown.Item>
              </Dropdown.Menu>
            </Dropdown.Popover>
          </Dropdown>
        </div>
      </div>

      {project.configErrors.length > 0 && (
        <InfoBar
          severity="warning"
          title="workshop.json has problems"
          actions={
            <Button size="sm" variant="secondary" onPress={() => goTo("settings")}>
              Open settings
            </Button>
          }
        >
          {project.configErrors.join(" ")}
        </InfoBar>
      )}
    </header>
  );
}

function Meta({ icon, children }: { icon?: ReactNode; children: ReactNode }) {
  return (
    <span className="flex items-center gap-1">
      {icon}
      {children}
    </span>
  );
}

function LinkTooltip({ project }: { project: ProjectInfo }) {
  const { link } = project;
  return (
    <div className="flex flex-col gap-1 py-0.5 text-caption">
      {link.detail && <span>{link.detail}</span>}
      {link.linkPath && <span className="break-all text-muted">Link: {link.linkPath}</span>}
      {link.target && <span className="break-all text-muted">Points to: {link.target}</span>}
      {!link.detail && !link.linkPath && <span>CS2's content folder links to this project's sources.</span>}
    </div>
  );
}

function LinkConfirmBody({ project }: { project: ProjectInfo }) {
  const { link, contentDir, config } = project;
  const linkPath = link.linkPath ?? `<CS2>\\content\\csgo_addons\\${config.addon}`;
  if (link.state === "conflict") {
    return (
      <div className="flex flex-col gap-2">
        <p>A real folder is already here:</p>
        <CodeText value={linkPath} copyLabel="Copy path" />
        <p>
          It gets renamed to <code className="text-foreground">{config.addon}.addonstudio-backup-(date)</code> and a link to this
          project's content folder takes its place. Nothing is deleted.
        </p>
      </div>
    );
  }
  if (link.state === "wrongTarget") {
    return (
      <div className="flex flex-col gap-2">
        <p>The link points somewhere else{link.target ? ":" : "."}</p>
        {link.target && <CodeText value={link.target} copyLabel="Copy path" />}
        <p>It will point to this project's content folder instead:</p>
        <CodeText value={contentDir} copyLabel="Copy path" />
      </div>
    );
  }
  return (
    <div className="flex flex-col gap-2">
      <p>Creates a folder link so CS2's tools see this project's sources:</p>
      <CodeText value={linkPath} copyLabel="Copy path" />
      <p>It points to:</p>
      <CodeText value={contentDir} copyLabel="Copy path" />
    </div>
  );
}
