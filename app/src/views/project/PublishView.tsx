// Publish: change note, version, push tags / GitHub release toggles, warnings, the stage stepper from
// publish.progress (upload fraction included), the result, the source-folder conflict retry, and the Steam status.
import { useEffect, useState, type ReactNode } from "react";
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { Button, Chip, Description, FieldError, Input, Label, ProgressBar, Skeleton, Switch, TextArea, TextField, cn } from "@heroui/react";
import { AnimatePresence, motion } from "motion/react";
import { Ban, Check, ChevronRight, Circle, CircleX, CloudUpload, ExternalLink, Eye, EyeOff, Globe, Hammer, LoaderCircle, Lock, RefreshCw, Square, Users, X } from "lucide-react";
import { call, isRpcError } from "../../rpc/client";
import type { EventMessage, EventsOf, PublishRequest, PublishResult, PublishStage, SteamItemStatus, Visibility } from "../../rpc/types";
import type { ProjectTab, ProjectViewProps, PublishIntent } from "./types";
import { formatBytes, formatDateTime, formatDuration, plural, relativeTime, workshopUrl } from "./logic";
import { createKeyedStore } from "./store";
import { ConfirmDialog, CopyButton, ErrorNotice, Notice, Panel, ViewPage, errorText, openExternal, type NoticeTone } from "./ui";

/* ---------- run state (kept per project, survives tab switches) ---------- */

interface PublishRun {
  id: number;
  request: PublishRequest;
  startedAt: number;
  finishedAt: number | null;
  stage: PublishStage | null;
  fraction: number | null;
  message: string | null;
  build: { done: number; total: number | null; current: string | null; errors: number };
  result: PublishResult | null;
  error: unknown;
  cancelled: boolean;
}

interface PublishSlot {
  run: PublishRun | null;
  controller: AbortController | null;
  /** a request that failed with "conflict", waiting for the user to confirm */
  conflict: { request: PublishRequest; message: string } | null;
}

const publishStore = createKeyedStore<PublishSlot>(() => ({ run: null, controller: null, conflict: null }));
const startedIntents = new WeakSet<PublishIntent>();
let runCounter = 0;

const AGREEMENT_URL = "https://steamcommunity.com/sharedfiles/workshoplegalagreement";

function applyEvent(run: PublishRun, message: EventMessage<EventsOf<"publish.run">>): PublishRun {
  switch (message.event) {
    case "publish.progress":
      return { ...run, stage: message.data.stage, fraction: message.data.fraction, message: message.data.message };
    case "build.started":
      return { ...run, build: { ...run.build, total: message.data.total } };
    case "build.itemStarted":
      return { ...run, build: { ...run.build, total: message.data.total, current: message.data.source } };
    case "build.item":
      return {
        ...run,
        build: { ...run.build, done: run.build.done + 1, errors: run.build.errors + (message.data.item.status === "error" ? 1 : 0) },
      };
    default:
      return run;
  }
}

function startPublish(queryClient: QueryClient, refreshProject: () => Promise<void>, request: PublishRequest) {
  const path = request.path;
  if (publishStore.get(path).controller) return;
  const controller = new AbortController();
  const id = ++runCounter;
  publishStore.set(path, () => ({
    controller,
    conflict: null,
    run: {
      id,
      request,
      startedAt: Date.now(),
      finishedAt: null,
      stage: null,
      fraction: null,
      message: null,
      build: { done: 0, total: null, current: null, errors: 0 },
      result: null,
      error: null,
      cancelled: false,
    },
  }));
  const update = (change: (run: PublishRun) => PublishRun) =>
    publishStore.set(path, (slot) => (slot.run && slot.run.id === id ? { ...slot, run: change(slot.run) } : slot));

  void (async () => {
    try {
      const result = await call("publish.run", request, { signal: controller.signal, onEvent: (event) => update((run) => applyEvent(run, event)) });
      update((run) => ({ ...run, result, stage: "done", fraction: 1, finishedAt: Date.now() }));
      void refreshProject();
    } catch (error) {
      if (isRpcError(error, "conflict")) {
        publishStore.set(path, (slot) => ({ ...slot, run: null, conflict: { request, message: errorText(error) } }));
      } else {
        const cancelled = isRpcError(error, "cancelled");
        update((run) => ({ ...run, finishedAt: Date.now(), cancelled, error: cancelled ? null : error }));
      }
    } finally {
      publishStore.set(path, (slot) => ({ ...slot, controller: slot.controller === controller ? null : slot.controller }));
      for (const key of ["steam.status", "git.log", "git.status", "pack.contents", "build.plan"]) {
        void queryClient.invalidateQueries({ queryKey: [key, path] });
      }
    }
  })();
}

/* ---------- view ---------- */

const VERSION_RE = /^\d+(\.\d+){1,3}([-+][0-9A-Za-z.-]+)?$/;

export function PublishView({ project, refreshProject, goTo, intent }: ProjectViewProps) {
  const path = project.path;
  const queryClient = useQueryClient();
  const slot = publishStore.use(path);
  const hasRemote = project.git.remote !== null;

  const [note, setNote] = useState(intent?.note ?? "");
  const [version, setVersion] = useState(intent?.version ?? "");
  const [push, setPush] = useState(hasRemote);
  const [githubRelease, setGithubRelease] = useState(project.config.autoPublish.githubRelease && hasRemote);

  const appInfo = useQuery({ queryKey: ["app.info"], queryFn: () => call("app.info"), staleTime: Infinity });
  const status = useQuery({ queryKey: ["git.status", path], queryFn: () => call("git.status", { path }) });
  const plan = useQuery({ queryKey: ["build.plan", path], queryFn: () => call("build.plan", { path }) });
  const pack = useQuery({ queryKey: ["pack.contents", path], queryFn: () => call("pack.contents", { path }) });

  const versionValue = version.trim().replace(/^v/i, "");
  const versionError = versionValue !== "" && !VERSION_RE.test(versionValue) ? "Use a version like 1.4.0." : null;
  const running = slot.controller !== null;

  const request = (overrides: Partial<PublishRequest> = {}): PublishRequest => ({
    path,
    changeNote: note.trim(),
    version: versionValue === "" ? null : versionValue,
    skipBuild: false,
    push: push && hasRemote,
    githubRelease: githubRelease && hasRemote && versionValue !== "",
    allowSourceFolderChange: false,
    ...overrides,
  });

  // a version commit on the Changes tab lands here with autoStart: start once
  useEffect(() => {
    if (!intent?.autoStart || startedIntents.has(intent)) return;
    startedIntents.add(intent);
    const version = intent.version?.replace(/^v/i, "") ?? null;
    startPublish(queryClient, refreshProject, {
      path,
      changeNote: intent.note.trim(),
      version: version && VERSION_RE.test(version) ? version : null,
      skipBuild: false,
      push: hasRemote,
      githubRelease: project.config.autoPublish.githubRelease && hasRemote && version !== null,
      allowSourceFolderChange: false,
    });
  }, [intent, path, queryClient, refreshProject, hasRemote, project.config.autoPublish.githubRelease]);

  const warnings: { tone: NoticeTone; title: string; body?: ReactNode }[] = [];
  if (appInfo.data && !appInfo.data.canPublish) warnings.push({ tone: "danger", title: "Publishing needs Windows with Steam running", body: "The upload goes through the Steam client on this PC." });
  if (project.configErrors.length > 0) warnings.push({ tone: "danger", title: "workshop.json has problems", body: project.configErrors.join(" ") });
  if (!project.config.workshopId) warnings.push({ tone: "info", title: "This creates a new Workshop item", body: "There is no Workshop id yet. The new id is written to workshop.json." });
  if (status.data && status.data.files.length > 0)
    warnings.push({ tone: "warning", title: `${plural(status.data.files.length, "uncommitted change")}`, body: "They are published too, and the change note says so. Commit first to keep git and the Workshop in step." });
  if (plan.data && plan.data.counts.failed > 0)
    warnings.push({
      tone: "warning",
      title: `The last build has errors in ${plural(plan.data.counts.failed, "file")}`,
      body: "Publish builds first and stops if anything still fails.",
    });
  if (pack.data && pack.data.limitBytes > 0) {
    const ratio = pack.data.totalSize / pack.data.limitBytes;
    if (ratio > 1) warnings.push({ tone: "danger", title: "The package is over the size cap", body: `${formatBytes(pack.data.totalSize)} of ${formatBytes(pack.data.limitBytes)}.` });
    else if (ratio >= 0.85) warnings.push({ tone: "warning", title: "Close to the size cap", body: `${formatBytes(pack.data.totalSize)} of ${formatBytes(pack.data.limitBytes)}.` });
  }

  const blocked = project.configErrors.length > 0 || (appInfo.data !== undefined && !appInfo.data.canPublish);
  const canPublish = note.trim() !== "" && versionError === null && !running && !blocked;

  return (
    <ViewPage>
      <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,1fr)_360px]">
        <div className="flex min-w-0 flex-col gap-4">
          <AnimatePresence initial={false}>
            {slot.run && (
              <motion.div key={slot.run.id} initial={{ opacity: 0, y: -6 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0 }} transition={{ duration: 0.18 }}>
                <RunPanel
                  run={slot.run}
                  running={running}
                  goTo={goTo}
                  onCancel={() => slot.controller?.abort()}
                  onDismiss={() => publishStore.set(path, (current) => ({ ...current, run: null }))}
                />
              </motion.div>
            )}
          </AnimatePresence>

          <Panel title="Publish to the Workshop" description={project.config.workshopId ? `Updates item ${project.config.workshopId}` : "Creates a new item"}>
            <form
              className="flex flex-col gap-4"
              onSubmit={(event) => {
                event.preventDefault();
                if (canPublish) startPublish(queryClient, refreshProject, request());
              }}
            >
              <TextField value={note} onChange={setNote} isDisabled={running}>
                <Label>Change note</Label>
                <TextArea rows={4} placeholder="What changed in this version" />
                <Description>Shown on the Workshop page under Change Notes. The commit hash is added.</Description>
              </TextField>

              <TextField value={version} onChange={setVersion} isInvalid={versionError !== null} isDisabled={running} validationBehavior="aria" className="max-w-xs">
                <Label>Version</Label>
                <Input placeholder="1.4.0" />
                {versionError ? <FieldError>{versionError}</FieldError> : <Description>{versionValue ? `Tagged as v${versionValue}` : "Optional"}</Description>}
              </TextField>

              <div className="flex flex-col gap-3">
                <ToggleRow
                  label="Push tags"
                  description={hasRemote ? "Push the deploy and version tags to origin." : "No remote is set up."}
                  isSelected={push && hasRemote}
                  isDisabled={!hasRemote || running}
                  onChange={setPush}
                />
                <ToggleRow
                  label="GitHub release"
                  description={!hasRemote ? "No remote is set up." : versionValue ? `Create release v${versionValue} with the VPK files (needs gh).` : "Needs a version."}
                  isSelected={githubRelease && hasRemote && versionValue !== ""}
                  isDisabled={!hasRemote || versionValue === "" || running}
                  onChange={setGithubRelease}
                />
              </div>

              {warnings.length > 0 && (
                <div className="flex flex-col gap-2">
                  {warnings.map((warning) => (
                    <Notice key={warning.title} tone={warning.tone} title={warning.title}>
                      {warning.body}
                    </Notice>
                  ))}
                </div>
              )}

              <div className="flex items-center gap-3">
                <Button type="submit" variant="primary" size="lg" isDisabled={!canPublish} isPending={running}>
                  <CloudUpload className="size-4" />
                  {running ? "Publishing" : "Publish"}
                </Button>
                {note.trim() === "" && !running && <span className="text-xs text-muted">Write a change note first.</span>}
              </div>
            </form>
          </Panel>
        </div>

        <SteamCard project={project} />
      </div>

      <ConfirmDialog
        isOpen={slot.conflict !== null}
        onOpenChange={(open) => {
          if (!open) publishStore.set(path, (current) => ({ ...current, conflict: null }));
        }}
        title="Published from another folder before"
        tone="warning"
        confirmLabel="Publish from this project"
        onConfirm={() => {
          const conflict = publishStore.get(path).conflict;
          if (conflict) startPublish(queryClient, refreshProject, { ...conflict.request, allowSourceFolderChange: true });
        }}
      >
        <div className="flex flex-col gap-2">
          <p>This Workshop item was last uploaded from a different addon folder. Publishing from here replaces its content with this project.</p>
          {slot.conflict && <p className="rounded-sm bg-default/60 px-2.5 py-2 font-mono text-xs text-foreground wrap-anywhere">{slot.conflict.message}</p>}
        </div>
      </ConfirmDialog>
    </ViewPage>
  );
}

function ToggleRow({
  label,
  description,
  isSelected,
  isDisabled,
  onChange,
}: {
  label: string;
  description: string;
  isSelected: boolean;
  isDisabled: boolean;
  onChange: (value: boolean) => void;
}) {
  return (
    <Switch isSelected={isSelected} isDisabled={isDisabled} onChange={onChange}>
      <Switch.Content className="font-normal">
        <Switch.Control>
          <Switch.Thumb />
        </Switch.Control>
        {label}
      </Switch.Content>
      <Description className="text-xs">{description}</Description>
    </Switch>
  );
}

/* ---------- progress and result ---------- */

const STAGE_LABEL: Record<PublishStage, string> = {
  build: "Build",
  pack: "Pack",
  upload: "Upload",
  tag: "Tag",
  push: "Push",
  release: "Release",
  done: "Done",
};

function stagesFor(request: PublishRequest): PublishStage[] {
  const stages: PublishStage[] = [];
  if (!request.skipBuild) stages.push("build");
  stages.push("pack", "upload", "tag");
  if (request.push) stages.push("push");
  if (request.githubRelease) stages.push("release");
  stages.push("done");
  return stages;
}

function useNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const timer = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(timer);
  }, [active]);
  return now;
}

function RunPanel({
  run,
  running,
  goTo,
  onCancel,
  onDismiss,
}: {
  run: PublishRun;
  running: boolean;
  goTo: (tab: ProjectTab) => void;
  onCancel: () => void;
  onDismiss: () => void;
}) {
  const now = useNow(running);
  const stages = stagesFor(run.request);
  const currentIndex = run.result ? stages.length : run.stage ? Math.max(0, stages.indexOf(run.stage)) : 0;
  const failed = run.error !== null;
  const elapsed = (run.finishedAt ?? now) - run.startedAt;

  return (
    <Panel
      title={run.result ? "Published" : failed ? "Publish failed" : run.cancelled ? "Publish cancelled" : "Publishing"}
      description={`${run.request.version ? `v${run.request.version}, ` : ""}${formatDuration(elapsed)}`}
      actions={
        running ? (
          <Button size="sm" variant="danger-soft" onPress={onCancel}>
            <Square className="size-3.5" />
            Cancel
          </Button>
        ) : (
          <Button size="sm" variant="ghost" isIconOnly aria-label="Close" onPress={onDismiss}>
            <X className="size-4" />
          </Button>
        )
      }
    >
      <ol className="flex flex-wrap items-center gap-x-1 gap-y-2">
        {stages.map((stage, index) => {
          const state = index < currentIndex || run.result ? "done" : index === currentIndex ? (failed ? "error" : run.cancelled ? "stopped" : "active") : "pending";
          return (
            <li key={stage} className="flex items-center gap-1">
              <span
                className={cn(
                  "flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs transition-colors duration-200",
                  state === "done" && "bg-success-soft text-success-soft-foreground",
                  state === "active" && "bg-accent-soft text-accent-soft-foreground",
                  state === "error" && "bg-danger-soft text-danger-soft-foreground",
                  (state === "pending" || state === "stopped") && "bg-default text-muted",
                )}
              >
                {state === "done" && <Check className="size-3" />}
                {state === "active" && <LoaderCircle className="size-3 animate-spin" />}
                {state === "error" && <CircleX className="size-3" />}
                {(state === "pending" || state === "stopped") && <Circle className="size-3" />}
                {STAGE_LABEL[stage]}
              </span>
              {index < stages.length - 1 && <ChevronRight className="size-3 text-muted" />}
            </li>
          );
        })}
      </ol>

      {running && (
        <div className="mt-4 flex flex-col gap-2">
          {run.stage === "build" && run.build.total !== null ? (
            <>
              <ProgressBar aria-label="Build progress" size="sm" value={run.build.total > 0 ? (run.build.done / run.build.total) * 100 : 100} color={run.build.errors > 0 ? "danger" : "accent"}>
                <ProgressBar.Track>
                  <ProgressBar.Fill />
                </ProgressBar.Track>
              </ProgressBar>
              <p className="text-xs text-muted wrap-anywhere">
                {run.build.total === 0 ? "Nothing to compile" : `Compiling ${Math.min(run.build.done + 1, run.build.total)} of ${run.build.total}`}
                {run.build.current && run.build.done < run.build.total ? `: ${run.build.current}` : ""}
              </p>
            </>
          ) : (
            <>
              <ProgressBar aria-label="Stage progress" size="sm" value={(run.fraction ?? 0) * 100} isIndeterminate={run.fraction === null}>
                <ProgressBar.Track>
                  <ProgressBar.Fill />
                </ProgressBar.Track>
              </ProgressBar>
              <div className="flex items-center justify-between gap-3 text-xs text-muted">
                <span className="wrap-anywhere">{run.message ?? "Starting"}</span>
                {run.stage === "upload" && run.fraction !== null && <span className="shrink-0 tabular-nums">{Math.round(run.fraction * 100)}%</span>}
              </div>
            </>
          )}
        </div>
      )}

      {failed && (
        <div className="mt-4 flex flex-col gap-2">
          <ErrorNotice title={run.stage === "build" || run.build.errors > 0 ? "The build has errors" : "Publish stopped"} error={run.error} />
          {(run.stage === "build" || run.build.errors > 0) && (
            <div>
              <Button size="sm" variant="secondary" onPress={() => goTo("build")}>
                <Hammer className="size-3.5" />
                Open Build
              </Button>
            </div>
          )}
        </div>
      )}

      {run.result && <ResultBody result={run.result} />}
    </Panel>
  );
}

function ResultBody({ result }: { result: PublishResult }) {
  return (
    <motion.div initial={{ opacity: 0, y: 4 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.2 }} className="mt-4 flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="primary" size="sm" onPress={() => void openExternal(result.url)}>
          Open on Steam
          <ExternalLink className="size-3.5" />
        </Button>
        {result.releaseUrl && (
          <Button variant="secondary" size="sm" onPress={() => void openExternal(result.releaseUrl!)}>
            GitHub release
            <ExternalLink className="size-3.5" />
          </Button>
        )}
        <span className="flex items-center gap-1 text-xs text-muted">
          Item <span className="font-mono">{result.workshopId}</span>
          <CopyButton text={result.workshopId} label="Copy the Workshop id" />
        </span>
      </div>
      {result.created && <Notice tone="success" title="New Workshop item created">Its id is saved in workshop.json.</Notice>}
      {result.needsAgreement && (
        <Notice
          tone="warning"
          title="Accept the Workshop agreement"
          action={
            <Button size="sm" variant="ghost" className="text-inherit" onPress={() => void openExternal(AGREEMENT_URL)}>
              Open
              <ExternalLink className="size-3.5" />
            </Button>
          }
        >
          Steam keeps the item hidden until you accept the Workshop legal agreement.
        </Notice>
      )}
      {result.tags.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs text-muted">Tags</span>
          {result.tags.map((tag) => (
            <Chip key={tag} size="sm" variant="soft">
              {tag}
            </Chip>
          ))}
        </div>
      )}
      {result.warnings.map((warning) => (
        <Notice key={warning} tone="warning" title={warning} />
      ))}
      <p className="text-xs text-muted">Steam processes the upload for a few minutes before it is live for players.</p>
    </motion.div>
  );
}

/* ---------- Steam status ---------- */

const VISIBILITY: Record<Visibility, { label: string; Icon: typeof Globe }> = {
  public: { label: "Public", Icon: Globe },
  friendsOnly: { label: "Friends only", Icon: Users },
  private: { label: "Private", Icon: Lock },
  unlisted: { label: "Unlisted", Icon: EyeOff },
};

function SteamCard({ project }: { project: ProjectViewProps["project"] }) {
  const workshopId = project.config.workshopId;
  const steam = useQuery({
    queryKey: ["steam.status", project.path],
    queryFn: () => call("steam.status", { path: project.path }),
    enabled: workshopId !== null,
    refetchInterval: (query) => (query.state.data?.live === "processing" ? 15_000 : false),
  });

  return (
    <Panel
      title="On Steam"
      className="lg:sticky lg:top-4"
      actions={
        workshopId ? (
          <>
            <Button size="sm" variant="ghost" isIconOnly aria-label="Open on Steam" onPress={() => void openExternal(workshopUrl(workshopId))}>
              <ExternalLink className="size-4" />
            </Button>
            <Button size="sm" variant="ghost" isIconOnly aria-label="Refresh" onPress={() => void steam.refetch()} isDisabled={steam.isFetching}>
              <RefreshCw className={cn("size-4", steam.isFetching && "animate-spin")} />
            </Button>
          </>
        ) : undefined
      }
    >
      {!workshopId ? (
        <p className="text-[13px] text-muted">Not on the Workshop yet. The first publish creates the item.</p>
      ) : steam.isPending ? (
        <SteamSkeleton />
      ) : steam.isError ? (
        <ErrorNotice title="Could not ask Steam" error={steam.error} onRetry={() => void steam.refetch()} />
      ) : (
        <SteamDetails status={steam.data} />
      )}
    </Panel>
  );
}

function SteamSkeleton() {
  return (
    <div className="flex flex-col gap-3" aria-hidden>
      <Skeleton className="h-4 w-2/3 rounded-sm" />
      {Array.from({ length: 4 }, (_, index) => (
        <div key={index} className="flex justify-between">
          <Skeleton className="h-3 w-20 rounded-sm" />
          <Skeleton className="h-3 w-24 rounded-sm" />
        </div>
      ))}
    </div>
  );
}

function SteamDetails({ status }: { status: SteamItemStatus }) {
  const [showRaw, setShowRaw] = useState(false);

  if (!status.found) {
    return (
      <Notice tone="warning" title="Steam does not know this item">
        Item {status.workshopId} was not found. It may be deleted, or owned by another account.
      </Notice>
    );
  }

  const visibility = status.visibility ? VISIBILITY[status.visibility] : null;
  const liveChip =
    status.live === "live" ? (
      <Chip size="sm" variant="soft" color="success">
        Live
      </Chip>
    ) : status.live === "processing" ? (
      <Chip size="sm" variant="soft" color="warning">
        <LoaderCircle className="size-3 animate-spin" />
        Processing
      </Chip>
    ) : (
      <Chip size="sm" variant="soft">
        Unknown
      </Chip>
    );

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-start justify-between gap-2">
        <span className="min-w-0 text-[13px] font-medium wrap-anywhere">{status.title ?? status.workshopId}</span>
        {liveChip}
      </div>

      {status.banned && (
        <Notice tone="danger" title="Banned on the Workshop">
          <span className="flex items-start gap-1.5">
            <Ban className="mt-0.5 size-3.5 shrink-0" />
            {status.banReason || "Steam gave no reason."}
          </span>
        </Notice>
      )}

      <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-2 text-[13px]">
        <dt className="text-muted">Updated</dt>
        <dd className="text-right" title={formatDateTime(status.timeUpdated)}>
          {status.timeUpdated ? relativeTime(status.timeUpdated) : "-"}
        </dd>
        <dt className="text-muted">Published here</dt>
        <dd className="text-right" title={formatDateTime(status.lastPublishAt)}>
          {status.lastPublishAt ? relativeTime(status.lastPublishAt) : "never"}
        </dd>
        <dt className="text-muted">Size</dt>
        <dd className="text-right tabular-nums">{status.size !== null ? formatBytes(status.size) : "-"}</dd>
        <dt className="text-muted">Visibility</dt>
        <dd className="flex items-center justify-end gap-1.5">
          {visibility ? (
            <>
              <visibility.Icon className="size-3.5 text-muted" />
              {visibility.label}
            </>
          ) : (
            "-"
          )}
        </dd>
        {status.workshopId && (
          <>
            <dt className="text-muted">Item id</dt>
            <dd className="flex items-center justify-end gap-1 font-mono text-xs">
              {status.workshopId}
              <CopyButton text={status.workshopId} label="Copy the Workshop id" />
            </dd>
          </>
        )}
      </dl>

      {status.live === "processing" && <p className="text-xs text-muted">Steam is still processing the last upload. This refreshes on its own.</p>}

      {status.raw && (
        <div>
          <button
            type="button"
            onClick={() => setShowRaw((value) => !value)}
            aria-expanded={showRaw}
            className="flex cursor-pointer items-center gap-1.5 text-xs text-accent transition-colors duration-200 hover:underline"
          >
            <Eye className="size-3.5" />
            {showRaw ? "Hide all fields" : "All fields from Steam"}
          </button>
          <AnimatePresence initial={false}>
            {showRaw && (
              <motion.div initial={{ height: 0, opacity: 0 }} animate={{ height: "auto", opacity: 1 }} exit={{ height: 0, opacity: 0 }} transition={{ duration: 0.2 }} className="overflow-hidden">
                <RawFields raw={status.raw} />
              </motion.div>
            )}
          </AnimatePresence>
        </div>
      )}
    </div>
  );
}

function RawFields({ raw }: { raw: Record<string, unknown> }) {
  const json = JSON.stringify(raw, null, 2);
  return (
    <div className="mt-2 rounded-sm bg-default/60 p-2">
      <div className="mb-1 flex items-center justify-between">
        <span className="text-[11px] text-muted">{plural(Object.keys(raw).length, "field")}</span>
        <CopyButton text={json} label="Copy as JSON" />
      </div>
      <dl className="grid max-h-80 grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 overflow-y-auto font-mono text-[11px]">
        {Object.entries(raw).map(([key, value]) => (
          <div key={key} className="contents">
            <dt className="text-muted">{key}</dt>
            <dd className="wrap-anywhere whitespace-pre-wrap">{typeof value === "string" ? value || '""' : JSON.stringify(value)}</dd>
          </div>
        ))}
      </dl>
    </div>
  );
}
