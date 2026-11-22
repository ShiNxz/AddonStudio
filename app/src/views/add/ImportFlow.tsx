// Import an existing CS2 addon: pick it, preview the plan (what is copied as source, decompiled, kept prebuilt or
// skipped), then run it with live progress.
import { useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Chip, Description, Label, Modal, ProgressBar, Skeleton, Switch } from "@heroui/react";
import { ArrowLeft, Box, CircleCheck, Link2, RefreshCw } from "lucide-react";
import { call } from "../../rpc/client";
import type { Cs2Addon, ImportAction, ImportPlan, ImportResult } from "../../rpc/types";
import { joinPath } from "../../app/platform";
import { keys } from "../../app/queries";
import { CodeText } from "../../components/CodeText";
import { EmptyState } from "../../components/EmptyState";
import { InfoBar } from "../../components/InfoBar";
import { Segmented } from "../../components/Segmented";
import { describeError } from "../../components/notify";
import { formatBytes, plural } from "../../components/format";
import { FolderField, useParentFolder } from "./FolderField";
import { folderFromAddon } from "./naming";
import { summarizePlan } from "./plan";
import type { AddFlowProps } from "./AddProjectModal";

type Step = "pick" | "plan" | "run" | "done";

const ACTION_LABEL: Record<ImportAction, string> = {
  copySource: "Copy source",
  decompile: "Decompile",
  prebuilt: "Prebuilt",
  skip: "Skip",
};

const ACTION_COLOR: Record<ImportAction, "accent" | "success" | "warning" | "default"> = {
  copySource: "accent",
  decompile: "success",
  prebuilt: "warning",
  skip: "default",
};

const PAGE = 300;

export function ImportFlow({ onDone, onCancel, setBusy }: AddFlowProps) {
  const queryClient = useQueryClient();
  const [step, setStep] = useState<Step>("pick");
  const [addon, setAddon] = useState<Cs2Addon | null>(null);
  const [parent, setParent] = useParentFolder();
  const [decompile, setDecompile] = useState(true);
  const [progress, setProgress] = useState<{ done: number; total: number; current: string } | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const abort = useRef<AbortController | null>(null);

  const addons = useQuery({ queryKey: keys.cs2Addons, queryFn: () => call("projects.cs2Addons"), staleTime: 10_000 });
  const path = addon && parent.trim() ? joinPath(parent.trim(), folderFromAddon(addon.name)) : "";

  const plan = useQuery({
    queryKey: ["import.plan", addon?.name, path, decompile],
    queryFn: () => call("import.plan", { addon: addon!.name, path, decompile }),
    enabled: step !== "pick" && addon !== null && path.length > 0,
    staleTime: Infinity,
    retry: false,
    meta: { silent: true },
  });

  const run = useMutation({
    mutationFn: async () => {
      const controller = new AbortController();
      abort.current = controller;
      setProgress({ done: 0, total: plan.data?.items.length ?? 0, current: "" });
      return call(
        "import.run",
        { addon: addon!.name, path, decompile },
        {
          signal: controller.signal,
          onEvent: (message) => {
            if (message.event === "import.progress") setProgress({ done: message.data.done, total: message.data.total, current: message.data.current });
          },
        },
      );
    },
    onMutate: () => {
      setBusy(true);
      setStep("run");
    },
    onSettled: () => {
      setBusy(false);
      abort.current = null;
    },
    onSuccess: (value) => {
      setParent(parent.trim());
      setResult(value);
      // the project is on the list now, even if the dialog is closed without opening it
      void queryClient.invalidateQueries({ queryKey: keys.projects });
      void queryClient.invalidateQueries({ queryKey: keys.cs2Addons });
      setStep("done");
    },
    onError: () => setStep("plan"),
  });

  if (step === "pick") {
    return (
      <>
        <Modal.Body className="mt-5 flex flex-col gap-3">
          <p className="text-body text-fg-secondary">Addons found in CS2's content and game folders. Pick one to turn into a project.</p>
          <AddonList
            addons={addons.data}
            loading={addons.isPending}
            error={addons.isError ? describeError(addons.error).message : null}
            selected={addon?.name ?? null}
            onSelect={setAddon}
            onRetry={() => void addons.refetch()}
          />
          {addon && (
            <FolderField
              label="Project folder goes in"
              value={parent}
              onChange={setParent}
              pickerTitle="Where the imported project goes"
              description={path ? `Creates ${path}` : undefined}
            />
          )}
        </Modal.Body>
        <Modal.Footer className="mt-6">
          <Button variant="secondary" onPress={onCancel}>
            Cancel
          </Button>
          <Button variant="primary" isDisabled={!addon || !path} onPress={() => setStep("plan")}>
            Preview import
          </Button>
        </Modal.Footer>
      </>
    );
  }

  if (step === "done" && result) {
    return <ImportDone result={result} onOpen={() => onDone(result.project)} />;
  }

  const running = step === "run";

  return (
    <>
      <Modal.Body className="mt-5 flex min-h-0 flex-col gap-4 [&>*]:shrink-0">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex min-w-0 items-center gap-2">
            <Button isIconOnly size="sm" variant="ghost" aria-label="Back" isDisabled={running} onPress={() => setStep("pick")}>
              <ArrowLeft />
            </Button>
            <div className="flex min-w-0 flex-col">
              <span className="text-body font-semibold">{addon?.name}</span>
              <span className="truncate text-caption text-muted">{path}</span>
            </div>
          </div>
          <Switch isSelected={decompile} onChange={setDecompile} isDisabled={running} size="sm">
            <Switch.Control>
              <Switch.Thumb />
            </Switch.Control>
            <Switch.Content>
              <Label className="text-body">Decompile what comes back cleanly</Label>
              <Description className="text-caption">Panorama, SVGs, panorama images and sounds become sources again.</Description>
            </Switch.Content>
          </Switch>
        </div>

        {running && progress ? (
          <div className="flex shrink-0 flex-col gap-2 rounded-lg bg-surface-secondary px-4 py-3">
            <div className="flex items-center justify-between gap-3 text-caption">
              <span className="font-semibold text-foreground">Importing</span>
              <span className="text-muted">
                {progress.done.toLocaleString()} of {progress.total.toLocaleString()}
              </span>
            </div>
            <ProgressBar aria-label="Import progress" size="sm" value={progress.total > 0 ? (progress.done / progress.total) * 100 : 0} isIndeterminate={progress.total === 0}>
              <ProgressBar.Track>
                <ProgressBar.Fill />
              </ProgressBar.Track>
            </ProgressBar>
            <span className="truncate font-mono text-caption text-muted">{progress.current || " "}</span>
          </div>
        ) : plan.isPending || plan.isFetching ? (
          <PlanSkeleton />
        ) : plan.isError ? (
          <InfoBar
            severity="error"
            title="The plan failed."
            actions={
              <Button size="sm" variant="secondary" onPress={() => void plan.refetch()}>
                <RefreshCw />
                Retry
              </Button>
            }
          >
            {describeError(plan.error).message}
          </InfoBar>
        ) : plan.data ? (
          <PlanView plan={plan.data} />
        ) : null}

      </Modal.Body>
      <Modal.Footer className="mt-6">
        {running ? (
          <Button variant="secondary" onPress={() => abort.current?.abort()}>
            Cancel import
          </Button>
        ) : (
          <Button variant="secondary" onPress={onCancel}>
            Cancel
          </Button>
        )}
        <Button variant="primary" isDisabled={!plan.data || plan.isFetching} isPending={running} onPress={() => run.mutate()}>
          Import {plan.data ? plural(plan.data.items.filter((item) => item.action !== "skip").length, "file") : ""}
        </Button>
      </Modal.Footer>
    </>
  );
}

interface AddonListProps {
  addons: Cs2Addon[] | undefined;
  loading: boolean;
  error: string | null;
  selected: string | null;
  onSelect: (addon: Cs2Addon) => void;
  onRetry: () => void;
}

function AddonList({ addons, loading, error, selected, onSelect, onRetry }: AddonListProps) {
  if (loading) {
    return (
      <div className="flex flex-col gap-1" aria-hidden>
        {[0, 1, 2, 3].map((index) => (
          <div key={index} className="flex items-center gap-3 rounded-[4px] bg-surface px-3 py-2.5">
            <Skeleton className="size-8 shrink-0" />
            <div className="flex flex-1 flex-col gap-1.5">
              <Skeleton className="h-3.5 w-40" />
              <Skeleton className="h-3 w-64" />
            </div>
            <Skeleton className="h-3 w-14" />
          </div>
        ))}
      </div>
    );
  }
  if (error) {
    return (
      <InfoBar
        severity="error"
        title="Could not list CS2 addons."
        actions={
          <Button size="sm" variant="secondary" onPress={onRetry}>
            Retry
          </Button>
        }
      >
        {error}
      </InfoBar>
    );
  }
  if (!addons || addons.length === 0) {
    return (
      <EmptyState icon={Box} title="No addons found" className="py-8">
        Nothing in csgo_addons yet. Create a new project instead.
      </EmptyState>
    );
  }

  return (
    <div role="radiogroup" aria-label="CS2 addons" className="flex max-h-[340px] flex-col gap-1 overflow-y-auto">
      {addons.map((addon) => {
        const isSelected = addon.name === selected;
        const linked = addon.linkedTo !== null;
        return (
          <button
            key={addon.name}
            type="button"
            role="radio"
            aria-checked={isSelected}
            disabled={linked}
            onClick={() => onSelect(addon)}
            className={`relative flex items-center gap-3 rounded-[4px] px-3 py-2.5 text-left outline-none transition-colors duration-200 focus-visible:ring-2 focus-visible:ring-focus disabled:cursor-default ${
              isSelected ? "bg-subtle-selected" : linked ? "bg-surface opacity-60" : "bg-surface hover:bg-surface-hover"
            }`}
          >
            {isSelected && <span className="absolute top-1/2 left-0 h-4 w-[3px] -translate-y-1/2 rounded-full bg-accent" />}
            <span className="flex size-8 shrink-0 items-center justify-center rounded-[4px] bg-default text-fg-secondary">
              <Box className="size-4" />
            </span>
            <span className="flex min-w-0 flex-1 flex-col">
              <span className="truncate font-mono text-[13px]">{addon.name}</span>
              <span className="truncate text-caption text-muted">
                {linked
                  ? `Already a project: ${addon.linkedTo}`
                  : [addon.hasContent ? plural(addon.contentFiles, "source file") : "no sources", addon.hasGame ? plural(addon.gameFiles, "compiled file") : "not built"].join(", ")}
              </span>
            </span>
            {linked ? (
              <Chip size="sm" variant="soft" color="success" className="gap-1">
                <Link2 className="size-3" />
                <Chip.Label>Linked</Chip.Label>
              </Chip>
            ) : (
              <span className="shrink-0 text-caption text-muted">{formatBytes(addon.sizeBytes)}</span>
            )}
          </button>
        );
      })}
    </div>
  );
}

type Filter = "all" | ImportAction;

function PlanView({ plan }: { plan: ImportPlan }) {
  const [filter, setFilter] = useState<Filter>("all");
  const [limit, setLimit] = useState(PAGE);
  const summary = useMemo(() => summarizePlan(plan.items), [plan.items]);
  const rows = useMemo(() => (filter === "all" ? plan.items : plan.items.filter((item) => item.action === filter)), [plan.items, filter]);

  const options = [
    { id: "all" as const, label: `All ${plan.items.length.toLocaleString()}` },
    ...(Object.keys(summary) as ImportAction[])
      .filter((action) => summary[action].count > 0)
      .map((action) => ({ id: action, label: `${ACTION_LABEL[action]} ${summary[action].count.toLocaleString()}` })),
  ];

  return (
    <div className="flex min-h-0 flex-col gap-3">
      <div className="grid grid-cols-4 gap-1">
        {(Object.keys(summary) as ImportAction[]).map((action) => (
          <div key={action} className="flex flex-col rounded-lg bg-surface px-3 py-2 shadow-card">
            <span className="text-caption text-muted">{ACTION_LABEL[action]}</span>
            <span className="text-body font-semibold">{summary[action].count.toLocaleString()}</span>
            <span className="text-caption text-muted">{formatBytes(summary[action].bytes)}</span>
          </div>
        ))}
      </div>

      {plan.warnings.length > 0 && <Notes notes={plan.warnings} />}

      <div className="flex items-center justify-between gap-3">
        <Segmented
          aria-label="Filter by action"
          options={options}
          value={filter}
          onChange={(value) => {
            setFilter(value);
            setLimit(PAGE);
          }}
        />
        <span className="text-caption text-muted">{formatBytes(plan.totalBytes)} in total</span>
      </div>

      <div className="max-h-[300px] min-h-[120px] overflow-y-auto rounded-lg bg-surface shadow-card">
        <table className="w-full table-fixed border-collapse text-left">
          <colgroup>
            <col />
            <col className="w-[84px]" />
            <col className="w-[116px]" />
            <col className="w-[34%]" />
          </colgroup>
          <thead className="sticky top-0 z-1 bg-overlay text-caption text-muted">
            <tr>
              <th className="px-3 py-2 font-normal">File</th>
              <th className="px-3 py-2 text-right font-normal">Size</th>
              <th className="px-3 py-2 font-normal">Action</th>
              <th className="px-3 py-2 font-normal">Goes to</th>
            </tr>
          </thead>
          <tbody>
            {rows.slice(0, limit).map((item) => (
              <tr key={item.path} className="align-top transition-colors duration-200 hover:bg-subtle-hover">
                <td className="px-3 py-1.5">
                  <code className="text-caption break-all text-fg-secondary">{item.path}</code>
                </td>
                <td className="px-3 py-1.5 text-right text-caption whitespace-nowrap text-muted">{formatBytes(item.size)}</td>
                <td className="px-3 py-1.5">
                  <Chip size="sm" variant="soft" color={ACTION_COLOR[item.action]}>
                    {ACTION_LABEL[item.action]}
                  </Chip>
                </td>
                <td className="px-3 py-1.5 text-caption">
                  {item.target ? <code className="break-all text-fg-secondary">{item.target}</code> : null}
                  {item.reason && <div className="text-muted">{item.reason}</div>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length === 0 && <p className="px-3 py-6 text-center text-caption text-muted">Nothing here.</p>}
        {rows.length > limit && (
          <div className="flex justify-center py-2">
            <Button size="sm" variant="ghost" onPress={() => setLimit((value) => value + PAGE * 4)}>
              Show more ({(rows.length - limit).toLocaleString()} left)
            </Button>
          </div>
        )}
      </div>
    </div>
  );
}

/** Plan and result warnings in one InfoBar, one line each. */
function Notes({ notes }: { notes: string[] }) {
  if (notes.length === 1) {
    return (
      <InfoBar severity="warning" title="Note">
        {notes[0]}
      </InfoBar>
    );
  }
  return (
    <InfoBar severity="warning" title={`${notes.length} notes`}>
      <ul className="mt-1 flex list-disc flex-col gap-0.5 pl-4">
        {notes.map((note, index) => (
          <li key={index} className="break-words">
            {note}
          </li>
        ))}
      </ul>
    </InfoBar>
  );
}

function PlanSkeleton() {
  return (
    <div className="flex flex-col gap-3" aria-hidden>
      <div className="grid grid-cols-4 gap-1">
        {[0, 1, 2, 3].map((index) => (
          <Skeleton key={index} className="h-[66px] rounded-lg" />
        ))}
      </div>
      <Skeleton className="h-7 w-80" />
      <div className="flex flex-col gap-2 rounded-lg bg-surface p-3">
        {Array.from({ length: 7 }, (_, index) => (
          <div key={index} className="flex gap-3">
            <Skeleton className="h-3.5 flex-1" />
            <Skeleton className="h-3.5 w-14" />
            <Skeleton className="h-3.5 w-20" />
            <Skeleton className="h-3.5 w-1/4" />
          </div>
        ))}
      </div>
    </div>
  );
}

function ImportDone({ result, onOpen }: { result: ImportResult; onOpen: () => void }) {
  return (
    <>
      <Modal.Body className="mt-5 flex flex-col gap-4">
        <div className="flex items-center gap-3">
          <CircleCheck className="size-6 shrink-0 text-success" />
          <div className="flex flex-col">
            <span className="text-body font-semibold">{result.project.config.title || result.project.config.addon} is a project now.</span>
            <span className="text-caption text-muted">
              {plural(result.copied, "source")} copied, {plural(result.decompiled, "file")} decompiled, {result.prebuilt.toLocaleString()} prebuilt,{" "}
              {result.skipped.toLocaleString()} skipped.
            </span>
          </div>
        </div>
        {result.backupPath && (
          <div className="flex flex-col gap-1.5">
            <span className="text-caption text-muted">The old content folder was kept as a backup:</span>
            <CodeText value={result.backupPath} copyLabel="Copy path" />
          </div>
        )}
        {result.warnings.length > 0 && <Notes notes={result.warnings} />}
      </Modal.Body>
      <Modal.Footer className="mt-6">
        <Button variant="primary" autoFocus onPress={onOpen}>
          Open project
        </Button>
      </Modal.Footer>
    </>
  );
}
