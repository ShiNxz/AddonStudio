// Build: the plan grouped by status, live per-file compile progress from build.run events, per-file logs, Clean,
// and auto-build (watch.set while this tab is open, a changed build on watch.changed, one at a time, one queued).
import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { Button, Chip, ProgressBar, Skeleton, cn } from "@heroui/react";
import { AnimatePresence, motion } from "motion/react";
import {
  ChevronRight,
  Circle,
  CircleCheck,
  CircleMinus,
  CircleX,
  Eraser,
  FileText,
  Hammer,
  LoaderCircle,
  Play,
  RefreshCw,
  Square,
  TriangleExclamationPoint,
  X,
  Zap,
} from "lucide-react";
import { call, isRpcError, subscribe } from "../../rpc/client";
import type { BuildItemResult, BuildMode, BuildPlan, BuildResult, BuildRoot, EventMessage, EventsOf, ItemStatus, RootStatus } from "../../rpc/types";
import type { ProjectViewProps } from "./types";
import { BuildQueue, formatBytes, formatDuration, plural, relativeTime, samePath } from "./logic";
import { createKeyedStore } from "./store";
import { ConfirmDialog, CopyBlock, ErrorNotice, FilePath, ListSkeleton, LogModal, Notice, Panel, ViewPage } from "./ui";

/* ---------- run state (kept per project, survives tab switches) ---------- */

type RowStatus = "queued" | "running" | ItemStatus;

interface BuildRow {
  source: string;
  status: RowStatus;
  startedAt: number | null;
  durationMs: number | null;
  lines: string[];
  item: BuildItemResult | null;
}

interface BuildRun {
  id: number;
  mode: BuildMode;
  auto: boolean;
  startedAt: number;
  finishedAt: number | null;
  total: number | null;
  rows: BuildRow[];
  result: BuildResult | null;
  error: unknown;
  cancelled: boolean;
}

interface BuildSlot {
  run: BuildRun | null;
  queue: BuildQueue;
  controller: AbortController | null;
  pending: boolean;
}

const buildStore = createKeyedStore<BuildSlot>(() => ({ run: null, queue: new BuildQueue(), controller: null, pending: false }));
let runCounter = 0;
const MAX_LIVE_LINES = 2000;

const newRow = (source: string): BuildRow => ({ source, status: "queued", startedAt: null, durationMs: null, lines: [], item: null });

function withRow(rows: BuildRow[], source: string, update: (row: BuildRow) => BuildRow): BuildRow[] {
  const index = rows.findIndex((row) => row.source === source);
  if (index < 0) return [...rows, update(newRow(source))];
  const next = rows.slice();
  next[index] = update(rows[index]!);
  return next;
}

function applyEvent(run: BuildRun, message: EventMessage<EventsOf<"build.run">>): BuildRun {
  switch (message.event) {
    case "build.started":
      return { ...run, total: message.data.total };
    case "build.itemStarted":
      return { ...run, total: message.data.total, rows: withRow(run.rows, message.data.source, (row) => ({ ...row, status: "running", startedAt: Date.now() })) };
    case "build.log": {
      const { source, line } = message.data;
      return { ...run, rows: withRow(run.rows, source, (row) => ({ ...row, lines: row.lines.length >= MAX_LIVE_LINES ? row.lines : [...row.lines, line] })) };
    }
    case "build.item": {
      const { item } = message.data;
      return { ...run, rows: withRow(run.rows, item.source, (row) => ({ ...row, status: item.status, durationMs: item.durationMs, item })) };
    }
    case "build.finished":
      return { ...run, result: message.data.result };
    default:
      return run;
  }
}

/** Seeds the row list with what the plan says will compile, so the queue shows up front. */
function plannedSources(plan: BuildPlan | undefined, mode: BuildMode, files?: string[]): string[] {
  if (mode === "files") return files ?? [];
  if (!plan) return [];
  return plan.roots
    .filter((root) => root.status !== "deleted" && root.status !== "unsupported")
    .filter((root) => mode === "full" || root.status === "new" || root.status === "changed" || root.status === "failed")
    .map((root) => root.source);
}

function buildTask(queryClient: QueryClient, path: string, mode: BuildMode, files: string[] | undefined, auto: boolean) {
  return async () => {
    const controller = new AbortController();
    const id = ++runCounter;
    const plan = queryClient.getQueryData<BuildPlan>(["build.plan", path]);
    buildStore.set(path, (slot) => ({
      ...slot,
      pending: slot.queue.hasPending,
      controller,
      run: {
        id,
        mode,
        auto,
        startedAt: Date.now(),
        finishedAt: null,
        total: null,
        rows: plannedSources(plan, mode, files).map(newRow),
        result: null,
        error: null,
        cancelled: false,
      },
    }));
    const update = (change: (run: BuildRun) => BuildRun) =>
      buildStore.set(path, (slot) => (slot.run && slot.run.id === id ? { ...slot, run: change(slot.run) } : slot));

    try {
      const result = await call(
        "build.run",
        files ? { path, mode, files } : { path, mode },
        { signal: controller.signal, onEvent: (event) => update((run) => applyEvent(run, event)) },
      );
      update((run) => {
        const byItem = new Map(result.items.map((item) => [item.source, item]));
        const rows = run.rows
          .filter((row) => byItem.has(row.source))
          .map((row) => {
            const item = byItem.get(row.source)!;
            return { ...row, status: item.status, durationMs: item.durationMs, item };
          });
        for (const item of result.items) {
          if (!rows.some((row) => row.source === item.source)) rows.push({ ...newRow(item.source), status: item.status, durationMs: item.durationMs, item });
        }
        return { ...run, result, rows, finishedAt: Date.now(), cancelled: result.cancelled };
      });
    } catch (error) {
      const cancelled = isRpcError(error, "cancelled");
      update((run) => ({
        ...run,
        finishedAt: Date.now(),
        cancelled,
        error: cancelled ? null : error,
        rows: run.rows.map((row) => (row.status === "running" || row.status === "queued" ? { ...row, status: "skipped" } : row)),
      }));
    } finally {
      buildStore.set(path, (slot) => ({ ...slot, controller: slot.controller === controller ? null : slot.controller, pending: slot.queue.hasPending }));
      void queryClient.invalidateQueries({ queryKey: ["build.plan", path] });
      void queryClient.invalidateQueries({ queryKey: ["pack.contents", path] });
    }
  };
}

/* ---------- view ---------- */

const STATUS_ORDER: RootStatus[] = ["failed", "new", "changed", "deleted", "unsupported", "unchanged"];
const STATUS_TITLE: Record<RootStatus, string> = {
  failed: "Failed",
  new: "New",
  changed: "Changed",
  deleted: "Deleted",
  unsupported: "Can not compile",
  unchanged: "Up to date",
};

export function BuildView({ project }: ProjectViewProps) {
  const path = project.path;
  const queryClient = useQueryClient();
  const slot = buildStore.use(path);
  const plan = useQuery({ queryKey: ["build.plan", path], queryFn: () => call("build.plan", { path }) });
  const [confirmClean, setConfirmClean] = useState(false);
  const [actionError, setActionError] = useState<unknown>(null);
  const [lastAuto, setLastAuto] = useState<{ at: number; files: string[] } | null>(null);

  const running = slot.controller !== null;
  const cs2Ready = plan.data?.cs2Ready ?? false;
  const cs2ReadyRef = useRef(cs2Ready);
  cs2ReadyRef.current = cs2Ready;

  const start = (mode: BuildMode, files?: string[]) => {
    setActionError(null);
    buildStore.get(path).queue.submit(buildTask(queryClient, path, mode, files, false), false);
  };

  // auto-build: watch while this tab is open, build changed sources on every change
  const autoBuild = project.config.autoBuild;
  useEffect(() => {
    if (!autoBuild) return;
    void call("watch.set", { path, enabled: true }).catch(() => undefined);
    const unsubscribe = subscribe("watch.changed", (data) => {
      if (!samePath(data.path, path)) return;
      setLastAuto({ at: Date.now(), files: data.files });
      void queryClient.invalidateQueries({ queryKey: ["build.plan", path] });
      if (!cs2ReadyRef.current) return;
      const outcome = buildStore.get(path).queue.submit(buildTask(queryClient, path, "changed", undefined, true), true);
      if (outcome === "queued") buildStore.set(path, (current) => ({ ...current, pending: true }));
    });
    return () => {
      unsubscribe();
      void call("watch.set", { path, enabled: false }).catch(() => undefined);
    };
  }, [autoBuild, path, queryClient]);

  const clean = async () => {
    setActionError(null);
    try {
      await call("build.clean", { path });
      await queryClient.invalidateQueries({ queryKey: ["build.plan", path] });
      await queryClient.refetchQueries({ queryKey: ["build.plan", path] });
      start("full");
    } catch (error) {
      setActionError(error);
    }
  };

  const counts = plan.data?.counts;
  const toBuild = counts ? counts.new + counts.changed + counts.failed : 0;

  return (
    <ViewPage>
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="primary" isDisabled={!cs2Ready || running} onPress={() => start("changed")}>
          <Hammer className="size-4" />
          Build changed
          {toBuild > 0 && <span className="rounded-full bg-accent-foreground/20 px-1.5 text-xs tabular-nums">{toBuild}</span>}
        </Button>
        <Button variant="secondary" isDisabled={!cs2Ready || running} onPress={() => start("full")}>
          <RefreshCw className="size-4" />
          Full rebuild
        </Button>
        <Button variant="danger-soft" isDisabled={!cs2Ready || running} onPress={() => setConfirmClean(true)}>
          <Eraser className="size-4" />
          Clean
        </Button>
        <div className="ml-auto flex items-center gap-3">
          {autoBuild && (
            <span className="flex items-center gap-1.5 text-xs text-muted" title="Builds changed sources when files change">
              <Zap className="size-3.5 text-accent" />
              {lastAuto ? `Auto-build on, last change ${relativeTime(new Date(lastAuto.at).toISOString())}` : "Auto-build on"}
            </span>
          )}
          <Button size="sm" variant="ghost" isIconOnly aria-label="Refresh the plan" isDisabled={running} onPress={() => void plan.refetch()}>
            <RefreshCw className={cn("size-4", plan.isFetching && "animate-spin")} />
          </Button>
        </div>
      </div>

      {plan.data && !plan.data.cs2Ready && (
        <Notice tone="warning" title="Nothing can compile on this PC">
          CS2 with the Workshop Tools (and Windows) are needed for resourcecompiler.exe. Check the Doctor page. The plan below still shows what would build.
        </Notice>
      )}
      {actionError !== null && <ErrorNotice title="Clean failed" error={actionError} />}

      <AnimatePresence initial={false}>
        {slot.run && (
          <motion.div key={slot.run.id} initial={{ opacity: 0, y: -6 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0 }} transition={{ duration: 0.18 }}>
            <RunPanel
              run={slot.run}
              running={running}
              pending={slot.pending}
              onCancel={() => {
                buildStore.get(path).queue.clearPending();
                buildStore.set(path, (current) => ({ ...current, pending: false }));
                slot.controller?.abort();
              }}
              onDismiss={() => buildStore.set(path, (current) => ({ ...current, run: null }))}
            />
          </motion.div>
        )}
      </AnimatePresence>

      {plan.isPending ? (
        <PlanSkeleton />
      ) : plan.isError ? (
        <ErrorNotice title="Could not make the build plan" error={plan.error} onRetry={() => void plan.refetch()} />
      ) : (
        <PlanView plan={plan.data} busy={running || !cs2Ready} onBuildFile={(source) => start("files", [source])} />
      )}

      <ConfirmDialog
        isOpen={confirmClean}
        onOpenChange={setConfirmClean}
        title="Clean the build folder?"
        confirmLabel="Clean and rebuild"
        onConfirm={clean}
      >
        <div className="flex flex-col gap-2">
          <p>Everything in the build folder is deleted except Valve's own files, then a full build runs.</p>
          {plan.data?.buildDir && <CopyBlock text={plan.data.buildDir} />}
        </div>
      </ConfirmDialog>
    </ViewPage>
  );
}

/* ---------- live run ---------- */

function useNow(active: boolean, intervalMs = 250): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [active, intervalMs]);
  return now;
}

const MODE_TITLE: Record<BuildMode, string> = { changed: "Build changed", full: "Full rebuild", files: "Build file" };

function RunPanel({ run, running, pending, onCancel, onDismiss }: { run: BuildRun; running: boolean; pending: boolean; onCancel: () => void; onDismiss: () => void }) {
  const now = useNow(running);
  const [logSource, setLogSource] = useState<string | null>(null);
  const finished = run.rows.filter((row) => row.status !== "queued" && row.status !== "running").length;
  const total = run.total ?? run.rows.length;
  const elapsed = (run.finishedAt ?? now) - run.startedAt;
  const errors = run.rows.filter((row) => row.status === "error").length;
  const warnings = run.rows.filter((row) => row.status === "warning").length;
  const ok = run.rows.filter((row) => row.status === "ok").length;
  const logRow = run.rows.find((row) => row.source === logSource) ?? null;

  let state: ReactNode;
  if (running) state = total > 0 ? `${finished} of ${total} files, ${formatDuration(elapsed)}` : `Starting, ${formatDuration(elapsed)}`;
  else if (run.cancelled) state = `Cancelled after ${formatDuration(elapsed)}`;
  else if (run.error !== null) state = "Failed";
  else if (run.result) state = `${run.result.ok ? "Done" : "Done with errors"} in ${formatDuration(run.result.durationMs)}`;
  else state = "Stopped";

  return (
    <Panel
      title={
        <span className="flex items-center gap-2">
          {running ? <LoaderCircle className="size-4 animate-spin text-accent" /> : <RunIcon run={run} />}
          {run.auto ? "Auto-build" : MODE_TITLE[run.mode]}
        </span>
      }
      description={state}
      actions={
        running ? (
          <Button size="sm" variant="danger-soft" onPress={onCancel}>
            <Square className="size-3.5" />
            Cancel
          </Button>
        ) : (
          <Button size="sm" variant="ghost" isIconOnly aria-label="Close the build result" onPress={onDismiss}>
            <X className="size-4" />
          </Button>
        )
      }
    >
      <div className="flex flex-col gap-3">
        <ProgressBar
          aria-label="Build progress"
          size="sm"
          value={total > 0 ? (finished / total) * 100 : 0}
          isIndeterminate={running && total === 0 && run.total === null}
          color={errors > 0 ? "danger" : !running && run.result?.ok ? "success" : "accent"}
        >
          <ProgressBar.Track>
            <ProgressBar.Fill />
          </ProgressBar.Track>
        </ProgressBar>

        <div className="flex flex-wrap items-center gap-2 text-xs">
          {ok > 0 && (
            <Chip size="sm" variant="soft" color="success">
              {ok} ok
            </Chip>
          )}
          {warnings > 0 && (
            <Chip size="sm" variant="soft" color="warning">
              {plural(warnings, "warning")}
            </Chip>
          )}
          {errors > 0 && (
            <Chip size="sm" variant="soft" color="danger">
              {plural(errors, "error")}
            </Chip>
          )}
          {run.result && run.result.prebuiltCopied > 0 && <span className="text-muted">{plural(run.result.prebuiltCopied, "prebuilt file")} copied</span>}
          {run.result && run.result.removed.length > 0 && <span className="text-muted">{plural(run.result.removed.length, "old output")} removed</span>}
          {pending && <span className="text-accent">Another build is queued</span>}
        </div>

        {run.error !== null && <ErrorNotice title="The build did not run" error={run.error} />}

        {run.rows.length > 0 ? (
          <ul className="flex max-h-[420px] flex-col overflow-y-auto">
            {run.rows.map((row) => (
              <li key={row.source}>
                <div className="group flex items-center gap-3 rounded-sm px-2 py-1.5 transition-colors duration-200 hover:bg-default/60">
                  <ItemIcon status={row.status} />
                  <FilePath path={row.source} className="flex-1 text-[13px]" />
                  {row.item && row.item.errors.length > 0 && <span className="shrink-0 text-xs text-danger">{plural(row.item.errors.length, "error")}</span>}
                  {row.item && row.item.errors.length === 0 && row.item.warnings.length > 0 && (
                    <span className="shrink-0 text-xs text-warning">{plural(row.item.warnings.length, "warning")}</span>
                  )}
                  <span className="w-16 shrink-0 text-right text-xs text-muted tabular-nums">
                    {row.durationMs !== null ? formatDuration(row.durationMs) : row.status === "running" && row.startedAt ? formatDuration(now - row.startedAt) : ""}
                  </span>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 shrink-0 px-2 text-xs"
                    isDisabled={row.status === "queued"}
                    onPress={() => setLogSource(row.source)}
                  >
                    <FileText className="size-3.5" />
                    Log
                  </Button>
                </div>
              </li>
            ))}
          </ul>
        ) : (
          !running && run.result && <p className="px-2 text-xs text-muted">Nothing needed compiling.</p>
        )}

        {run.result && run.result.removed.length > 0 && (
          <Collapsible title={`Removed from the build folder (${run.result.removed.length})`} defaultOpen={false} compact>
            <ul className="flex flex-col gap-0.5 px-2 pb-1">
              {run.result.removed.map((file) => (
                <li key={file} className="font-mono text-xs text-muted wrap-anywhere">
                  {file}
                </li>
              ))}
            </ul>
          </Collapsible>
        )}
      </div>

      <LogModal
        isOpen={logRow !== null}
        onOpenChange={(open) => {
          if (!open) setLogSource(null);
        }}
        title={logRow ? `Log: ${logRow.source.split("/").pop()}` : "Log"}
        subtitle={logRow?.source}
        text={logRow?.item?.log || logRow?.lines.join("\n") || ""}
        errors={logRow?.item?.errors ?? []}
        live={logRow?.status === "running"}
      />
    </Panel>
  );
}

function RunIcon({ run }: { run: BuildRun }) {
  if (run.cancelled) return <CircleMinus className="size-4 text-muted" />;
  if (run.error !== null || (run.result && !run.result.ok)) return <CircleX className="size-4 text-danger" />;
  return <CircleCheck className="size-4 text-success" />;
}

function ItemIcon({ status }: { status: RowStatus }) {
  const common = "size-4 shrink-0";
  switch (status) {
    case "queued":
      return <Circle className={cn(common, "text-muted/50")} aria-label="Queued" />;
    case "running":
      return <LoaderCircle className={cn(common, "animate-spin text-accent")} aria-label="Compiling" />;
    case "ok":
      return <CircleCheck className={cn(common, "text-success")} aria-label="Compiled" />;
    case "warning":
      return <TriangleExclamationPoint className={cn(common, "text-warning")} aria-label="Compiled with warnings" />;
    case "error":
      return <CircleX className={cn(common, "text-danger")} aria-label="Failed" />;
    default:
      return <CircleMinus className={cn(common, "text-muted")} aria-label="Skipped" />;
  }
}

/* ---------- plan ---------- */

function PlanSkeleton() {
  return (
    <div className="flex flex-col gap-4" aria-hidden>
      <div className="grid grid-cols-3 gap-2 sm:grid-cols-6">
        {Array.from({ length: 6 }, (_, index) => (
          <Skeleton key={index} className="h-16 rounded-lg" />
        ))}
      </div>
      <div className="rounded-lg bg-surface p-2">
        <ListSkeleton rows={6} />
      </div>
    </div>
  );
}

function PlanView({ plan, busy, onBuildFile }: { plan: BuildPlan; busy: boolean; onBuildFile: (source: string) => void }) {
  const groups = useMemo(() => {
    const map = new Map<RootStatus, BuildRoot[]>();
    for (const root of plan.roots) map.set(root.status, [...(map.get(root.status) ?? []), root]);
    return STATUS_ORDER.map((status) => ({ status, roots: map.get(status) ?? [] })).filter((group) => group.roots.length > 0);
  }, [plan.roots]);
  const prebuiltChanged = plan.prebuilt.filter((entry) => entry.status !== "unchanged");
  const c = plan.counts;

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-3 gap-2 sm:grid-cols-6">
        <Stat label="New" value={c.new} tone={c.new > 0 ? "accent" : undefined} />
        <Stat label="Changed" value={c.changed} tone={c.changed > 0 ? "warning" : undefined} />
        <Stat label="Failed" value={c.failed} tone={c.failed > 0 ? "danger" : undefined} />
        <Stat label="Deleted" value={c.deleted} />
        <Stat label="Up to date" value={c.unchanged} />
        <Stat label="Prebuilt changed" value={c.prebuiltChanged} />
      </div>

      {plan.conflicts.length > 0 && (
        <Notice tone="warning" title={`${plural(plan.conflicts.length, "file")} in both content and prebuilt`}>
          <p className="mb-1.5">The compiled file wins, the prebuilt copy is not used.</p>
          <ul className="flex flex-col gap-1 font-mono text-xs">
            {plan.conflicts.map((conflict) => (
              <li key={conflict.path} className="wrap-anywhere">
                {conflict.path} <span className="opacity-70">from {conflict.source}</span>
              </li>
            ))}
          </ul>
        </Notice>
      )}

      {plan.roots.length === 0 ? (
        <Panel>
          <div className="flex flex-col items-center gap-2 py-8 text-center">
            <Hammer className="size-6 text-muted" />
            <p className="text-sm">No sources to compile</p>
            <p className="text-xs text-muted">Put panorama, materials, models or sounds in content/.</p>
          </div>
        </Panel>
      ) : (
        <div className="rounded-lg bg-surface p-2">
          {groups.map((group, index) => (
            <div key={group.status} className={cn(index > 0 && "mt-1")}>
              <Collapsible
                title={
                  <span className="flex items-center gap-2">
                    {STATUS_TITLE[group.status]}
                    <span className="rounded-full bg-default px-1.5 text-xs font-normal text-muted tabular-nums">{group.roots.length}</span>
                  </span>
                }
                defaultOpen={group.status !== "unchanged"}
              >
                <ul className="flex flex-col">
                  {group.roots.map((root) => (
                    <RootRow key={root.source} root={root} busy={busy} onBuild={() => onBuildFile(root.source)} />
                  ))}
                </ul>
              </Collapsible>
            </div>
          ))}
        </div>
      )}

      {plan.prebuilt.length > 0 && (
        <Panel title="Prebuilt" description={`${plural(plan.prebuilt.length, "file")} copied from prebuilt/ as they are`}>
          {prebuiltChanged.length === 0 ? (
            <p className="text-xs text-muted">All copied already.</p>
          ) : (
            <ul className="flex flex-col">
              {prebuiltChanged.map((entry) => (
                <li key={entry.path} className="flex items-center gap-3 rounded-sm px-2 py-1.5 text-[13px] hover:bg-default/60">
                  <StatusTag status={entry.status} />
                  <FilePath path={entry.path} className="flex-1" />
                  <span className="shrink-0 text-xs text-muted tabular-nums">{formatBytes(entry.size)}</span>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      )}

      {plan.orphans.length > 0 && (
        <Panel title="Unknown files in the build folder" description="No source or prebuilt file explains these. Clean removes them.">
          <ul className="flex flex-col gap-0.5">
            {plan.orphans.map((orphan) => (
              <li key={orphan} className="px-2 py-0.5 font-mono text-xs text-muted wrap-anywhere">
                {orphan}
              </li>
            ))}
          </ul>
        </Panel>
      )}

      {plan.buildDir && (
        <div className="flex flex-col gap-1.5">
          <span className="text-xs text-muted">Build folder</span>
          <CopyBlock text={plan.buildDir} />
        </div>
      )}
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: "accent" | "warning" | "danger" }) {
  return (
    <div className="rounded-lg bg-surface px-3 py-2.5">
      <div
        className={cn(
          "text-xl font-semibold tabular-nums",
          tone === "accent" && "text-accent",
          tone === "warning" && "text-warning",
          tone === "danger" && "text-danger",
          tone === undefined && value === 0 && "text-muted",
        )}
      >
        {value}
      </div>
      <div className="text-xs text-muted">{label}</div>
    </div>
  );
}

const TAG_STYLE: Record<string, string> = {
  new: "bg-accent-soft text-accent-soft-foreground",
  changed: "bg-warning-soft text-warning-soft-foreground",
  failed: "bg-danger-soft text-danger-soft-foreground",
  deleted: "bg-danger-soft text-danger-soft-foreground",
  unsupported: "bg-default text-muted",
  unchanged: "bg-default text-muted",
};

function StatusTag({ status }: { status: string }) {
  return <span className={cn("w-16 shrink-0 rounded-sm px-1.5 py-px text-center text-[11px]", TAG_STYLE[status] ?? "bg-default text-muted")}>{status}</span>;
}

function RootRow({ root, busy, onBuild }: { root: BuildRoot; busy: boolean; onBuild: () => void }) {
  const [open, setOpen] = useState(false);
  const canBuild = root.status !== "deleted" && root.status !== "unsupported";

  return (
    <li>
      <div className="group flex items-center gap-2 rounded-sm px-2 py-1.5 transition-colors duration-200 hover:bg-default/60">
        <button
          type="button"
          onClick={() => setOpen((value) => !value)}
          aria-expanded={open}
          className="flex min-w-0 flex-1 cursor-pointer items-center gap-3 text-left"
        >
          <ChevronRight className={cn("size-3.5 shrink-0 text-muted transition-transform duration-200", open && "rotate-90")} />
          <LastResultIcon root={root} />
          <span className="min-w-0 flex-1">
            <FilePath path={root.source} className="text-[13px]" />
            {root.reason && <span className="block text-xs text-muted">{root.reason}</span>}
          </span>
          <span className="hidden shrink-0 rounded-sm bg-default px-1.5 text-[11px] text-muted sm:inline">{root.kind}</span>
          <span className="hidden w-28 shrink-0 text-right text-xs text-muted md:inline">{root.lastBuiltAt ? relativeTime(root.lastBuiltAt) : "never built"}</span>
        </button>
        {canBuild ? (
          <Button
            size="sm"
            variant="ghost"
            isIconOnly
            aria-label={`Build ${root.source}`}
            className="size-7 min-w-7 opacity-0 group-hover:opacity-100 focus-visible:opacity-100"
            isDisabled={busy}
            onPress={onBuild}
          >
            <Play className="size-3.5" />
          </Button>
        ) : (
          <span className="size-7 shrink-0" />
        )}
      </div>
      <AnimatePresence initial={false}>
        {open && (
          <motion.div initial={{ height: 0, opacity: 0 }} animate={{ height: "auto", opacity: 1 }} exit={{ height: 0, opacity: 0 }} transition={{ duration: 0.18 }} className="overflow-hidden">
            <div className="grid gap-3 py-2 pr-2 pl-12 text-xs sm:grid-cols-2">
              <div>
                <p className="mb-1 font-medium text-muted">Outputs</p>
                {root.outputs.length === 0 ? (
                  <p className="text-muted">None</p>
                ) : (
                  root.outputs.map((output) => (
                    <p key={output} className="font-mono wrap-anywhere">
                      {output}
                    </p>
                  ))
                )}
              </div>
              <div>
                <p className="mb-1 font-medium text-muted">Uses</p>
                {root.dependencies.length === 0 ? (
                  <p className="text-muted">No other files</p>
                ) : (
                  root.dependencies.map((dep) => (
                    <p key={dep} className="font-mono wrap-anywhere">
                      {dep}
                    </p>
                  ))
                )}
              </div>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </li>
  );
}

function LastResultIcon({ root }: { root: BuildRoot }) {
  if (root.status === "unsupported") return <CircleMinus className="size-4 shrink-0 text-muted" aria-label="Can not compile" />;
  if (root.lastResult === "error") return <CircleX className="size-4 shrink-0 text-danger" aria-label="Failed last time" />;
  if (root.lastResult === "warning") return <TriangleExclamationPoint className="size-4 shrink-0 text-warning" aria-label="Warnings last time" />;
  if (root.lastResult === "ok") return <CircleCheck className="size-4 shrink-0 text-success" aria-label="Built" />;
  return <Circle className="size-4 shrink-0 text-muted/50" aria-label="Not built yet" />;
}

function Collapsible({ title, defaultOpen, children, compact = false }: { title: ReactNode; defaultOpen: boolean; children: ReactNode; compact?: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div>
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
        className={cn(
          "flex w-full cursor-pointer items-center gap-2 rounded-sm px-2 text-left font-semibold transition-colors duration-200 hover:bg-default/60",
          compact ? "py-1 text-xs text-muted" : "py-1.5 text-sm",
        )}
      >
        <ChevronRight className={cn("size-3.5 shrink-0 text-muted transition-transform duration-200", open && "rotate-90")} />
        {title}
      </button>
      <AnimatePresence initial={false}>
        {open && (
          <motion.div initial={{ height: 0, opacity: 0 }} animate={{ height: "auto", opacity: 1 }} exit={{ height: 0, opacity: 0 }} transition={{ duration: 0.2 }} className="overflow-hidden">
            {children}
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
