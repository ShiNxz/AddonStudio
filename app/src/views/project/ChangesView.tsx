// Changes: the working tree grouped Staged / Changes, stage on toggle, discard with confirm, commit (and push),
// pull / push / fetch with ahead/behind, and the "Put this on GitHub" card when the repo has no remote.
import { useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Checkbox, Chip, Description, Disclosure, Input, Label, ProgressBar, Skeleton, TextArea, TextField, cn } from "@heroui/react";
import { AnimatePresence, motion } from "motion/react";
import { ArrowDown, ArrowUp, CloudDownload, CloudUpload, GitBranch, GitCommitHorizontal, Lock, RefreshCw, Rocket, Undo2 } from "lucide-react";
import { call, subscribe } from "../../rpc/client";
import type { GitFileChange, GitProgressEvent, GitStatus, ProjectInfo } from "../../rpc/types";
import type { ProjectViewProps } from "./types";
import {
  autoPublishVersion,
  formatBytes,
  gitStateLabel,
  gitStateLetter,
  gitStateTone,
  plural,
  samePath,
  shownState,
  splitChanges,
  type Tone,
} from "./logic";
import { ConfirmDialog, CopyBlock, ErrorNotice, FilePath, ListSkeleton, Notice, Panel, ViewPage, errorText, openExternal } from "./ui";

type SyncOp = "fetch" | "pull" | "push";
type Group = "staged" | "changes";

const TONE_TEXT: Record<Tone, string> = {
  success: "text-success",
  warning: "text-warning",
  danger: "text-danger",
  accent: "text-accent",
  muted: "text-muted",
};

export function ChangesView({ project, refreshProject, goTo }: ProjectViewProps) {
  const queryClient = useQueryClient();
  const statusKey = useMemo(() => ["git.status", project.path], [project.path]);
  const status = useQuery({
    queryKey: statusKey,
    queryFn: () => call("git.status", { path: project.path }),
    refetchOnWindowFocus: true,
  });

  const [busyFiles, setBusyFiles] = useState<Set<string>>(new Set());
  const [actionError, setActionError] = useState<unknown>(null);
  const [discard, setDiscard] = useState<GitFileChange[] | null>(null);
  const [sync, setSync] = useState<{ op: SyncOp; progress: GitProgressEvent | null } | null>(null);
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("");
  const [committing, setCommitting] = useState<"commit" | "push" | null>(null);
  const [commitNote, setCommitNote] = useState<string | null>(null);

  const setStatus = (next: GitStatus) => queryClient.setQueryData(statusKey, next);

  useEffect(
    () =>
      subscribe("watch.changed", (data) => {
        if (samePath(data.path, project.path)) void queryClient.invalidateQueries({ queryKey: statusKey });
      }),
    [project.path, queryClient, statusKey],
  );

  const data = status.data;
  const { staged, changes } = useMemo(() => splitChanges(data?.files ?? []), [data]);
  const hasRemote = project.git.remote !== null || (data?.upstream ?? null) !== null;

  const withFiles = async (paths: string[], action: () => Promise<GitStatus>) => {
    setBusyFiles((previous) => new Set([...previous, ...paths]));
    setActionError(null);
    try {
      setStatus(await action());
    } catch (error) {
      setActionError(error);
    } finally {
      setBusyFiles((previous) => {
        const next = new Set(previous);
        for (const path of paths) next.delete(path);
        return next;
      });
    }
  };

  const toggle = (group: Group, files: GitFileChange[]) => {
    const paths = files.map((file) => file.path);
    if (paths.length === 0) return;
    void withFiles(paths, () =>
      group === "staged" ? call("git.unstage", { path: project.path, files: paths }) : call("git.stage", { path: project.path, files: paths }),
    );
  };

  const runSync = async (op: SyncOp) => {
    setSync({ op, progress: null });
    setActionError(null);
    try {
      const method = op === "fetch" ? "git.fetch" : op === "pull" ? "git.pull" : "git.push";
      const next = await call(method, { path: project.path }, { onEvent: (event) => event.event === "git.progress" && setSync({ op, progress: event.data }) });
      setStatus(next);
      if (op === "pull") void queryClient.invalidateQueries({ queryKey: ["git.log", project.path] });
    } catch (error) {
      setActionError(error);
    } finally {
      setSync(null);
    }
  };

  const message = body.trim() === "" ? subject.trim() : `${subject.trim()}\n\n${body.trim()}`;
  const commitFiles = staged.length > 0 ? null : changes.map((file) => file.path);
  const canCommit = subject.trim() !== "" && (staged.length > 0 || changes.length > 0) && committing === null && sync === null;
  const autoVersion = project.config.autoPublish.enabled ? autoPublishVersion(project.config.autoPublish.pattern, subject.trim()) : null;

  const commit = async (push: boolean) => {
    if (!canCommit) return;
    setCommitting(push ? "push" : "commit");
    setActionError(null);
    setCommitNote(null);
    try {
      const result = await call(
        "git.commit",
        { path: project.path, message, files: commitFiles, push },
        { onEvent: (event) => event.event === "git.progress" && setSync({ op: "push", progress: event.data }) },
      );
      setStatus(result.status);
      setSubject("");
      setBody("");
      void queryClient.invalidateQueries({ queryKey: ["git.log", project.path] });
      const short = result.hash.slice(0, 7);
      setCommitNote(push && !result.pushed ? `Committed ${short}, not pushed.` : `Committed ${short}${result.pushed ? " and pushed" : ""}.`);
      if (result.autoPublish) goTo("publish", { version: result.autoPublish.version, note: message, autoStart: true });
    } catch (error) {
      setActionError(error);
    } finally {
      setCommitting(null);
      setSync(null);
    }
  };

  if (status.isPending) {
    return (
      <ViewPage>
        <Skeleton className="h-12 rounded-lg" />
        <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,1fr)_340px]">
          <div className="rounded-lg bg-surface p-2">
            <ListSkeleton rows={6} />
          </div>
          <div className="flex flex-col gap-3 rounded-lg bg-surface p-4" aria-hidden>
            <Skeleton className="h-4 w-20 rounded-sm" />
            <Skeleton className="h-9 rounded-sm" />
            <Skeleton className="h-24 rounded-sm" />
            <Skeleton className="h-9 rounded-sm" />
          </div>
        </div>
      </ViewPage>
    );
  }

  if (status.isError) {
    return (
      <ViewPage>
        <ErrorNotice title="Could not read the git status" error={status.error} onRetry={() => void status.refetch()} />
      </ViewPage>
    );
  }

  if (!data!.isRepo) {
    return (
      <ViewPage>
        <Notice tone="warning" title="This folder is not a git repo">
          Addon Studio keeps the sources in git. Run git init in the project folder, then open it again.
        </Notice>
      </ViewPage>
    );
  }

  return (
    <ViewPage>
      <SyncBar
        status={data!}
        hasRemote={hasRemote}
        sync={sync}
        disabled={committing !== null}
        onSync={(op) => void runSync(op)}
        onRefresh={() => void status.refetch()}
        refreshing={status.isFetching}
      />

      {data!.merging && (
        <Notice tone="warning" title="A merge is in progress">
          Fix the conflicted files, stage them and commit to finish it.
        </Notice>
      )}
      {actionError !== null && <ErrorNotice title="Git failed" error={actionError} />}
      {!hasRemote && <GitHubCard project={project} refreshProject={refreshProject} onDone={() => void status.refetch()} />}

      <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,1fr)_340px]">
        <div className="flex min-w-0 flex-col gap-4">
          {staged.length === 0 && changes.length === 0 ? (
            <Panel>
              <div className="flex flex-col items-center gap-2 py-10 text-center">
                <GitCommitHorizontal className="size-6 text-muted" />
                <p className="text-sm">No changes</p>
                <p className="text-xs text-muted">Edit files in content/ or prebuilt/ and they show up here.</p>
              </div>
            </Panel>
          ) : (
            <>
              <FileGroup
                group="staged"
                title="Staged"
                files={staged}
                busy={busyFiles}
                onToggle={toggle}
                onDiscard={setDiscard}
                emptyText="Nothing staged. Tick files below, or commit all of them at once."
              />
              <FileGroup group="changes" title="Changes" files={changes} busy={busyFiles} onToggle={toggle} onDiscard={setDiscard} emptyText="Everything is staged." />
            </>
          )}
        </div>

        <Panel title="Commit" className="lg:sticky lg:top-4">
          <form
            className="flex flex-col gap-3"
            onSubmit={(event) => {
              event.preventDefault();
              void commit(false);
            }}
          >
            <TextField value={subject} onChange={setSubject}>
              <Label>Summary</Label>
              <Input placeholder={project.config.autoPublish.enabled ? "v1.5.0 What changed" : "What changed"} />
              <Description>
                <span className={cn("tabular-nums", subject.length > 72 && "text-warning")}>{subject.length}/72</span>
              </Description>
            </TextField>
            <TextField value={body} onChange={setBody}>
              <Label>Details</Label>
              <TextArea rows={4} placeholder="Optional" />
            </TextField>

            <AnimatePresence initial={false}>
              {autoVersion && (
                <motion.div
                  initial={{ opacity: 0, height: 0 }}
                  animate={{ opacity: 1, height: "auto" }}
                  exit={{ opacity: 0, height: 0 }}
                  transition={{ duration: 0.18 }}
                  className="overflow-hidden"
                >
                  <div className="flex items-center gap-2 rounded-sm bg-accent-soft px-2.5 py-2 text-xs text-accent-soft-foreground">
                    <Rocket className="size-3.5 shrink-0" />
                    This commit publishes v{autoVersion} to the Workshop.
                  </div>
                </motion.div>
              )}
            </AnimatePresence>

            <div className="flex flex-col gap-2">
              <Button type="submit" variant="primary" className="w-full" isDisabled={!canCommit} isPending={committing === "commit"}>
                <GitCommitHorizontal className="size-4" />
                {staged.length > 0 ? `Commit ${plural(staged.length, "file")}` : changes.length > 0 ? `Commit all ${changes.length}` : "Commit"}
              </Button>
              <Button variant="secondary" className="w-full" isDisabled={!canCommit || !hasRemote} isPending={committing === "push"} onPress={() => void commit(true)}>
                <CloudUpload className="size-4" />
                Commit and push
              </Button>
            </div>
            {commitNote && <p className="text-xs text-muted">{commitNote}</p>}
          </form>
        </Panel>
      </div>

      <ConfirmDialog
        isOpen={discard !== null}
        onOpenChange={(open) => {
          if (!open) setDiscard(null);
        }}
        title={discard && discard.length === 1 ? "Discard this change?" : `Discard ${discard?.length ?? 0} changes?`}
        confirmLabel={discard?.some((file) => file.worktree === "untracked") ? "Delete and discard" : "Discard"}
        onConfirm={async () => {
          const paths = (discard ?? []).map((file) => file.path);
          await withFiles(paths, () => call("git.discard", { path: project.path, files: paths }));
        }}
      >
        <DiscardBody files={discard ?? []} />
      </ConfirmDialog>
    </ViewPage>
  );
}

/* ---------- sync bar ---------- */

function SyncBar({
  status,
  hasRemote,
  sync,
  disabled,
  onSync,
  onRefresh,
  refreshing,
}: {
  status: GitStatus;
  hasRemote: boolean;
  sync: { op: SyncOp; progress: GitProgressEvent | null } | null;
  disabled: boolean;
  onSync: (op: SyncOp) => void;
  onRefresh: () => void;
  refreshing: boolean;
}) {
  const busy = sync !== null || disabled;
  const percent = sync?.progress?.percent ?? null;

  return (
    <div className="flex flex-col gap-2 rounded-lg bg-surface px-4 py-2.5">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <div className="flex min-w-0 items-center gap-2 text-sm">
          <GitBranch className="size-4 text-muted" />
          <span className="font-medium">{status.branch ?? "detached HEAD"}</span>
          {status.upstream && <span className="text-xs text-muted">tracks {status.upstream}</span>}
        </div>
        <div className="flex items-center gap-3 text-xs tabular-nums">
          <span className={cn("flex items-center gap-1", status.ahead > 0 ? "text-foreground" : "text-muted")} title="Commits to push">
            <ArrowUp className="size-3.5" />
            {status.ahead}
          </span>
          <span className={cn("flex items-center gap-1", status.behind > 0 ? "text-warning" : "text-muted")} title="Commits to pull">
            <ArrowDown className="size-3.5" />
            {status.behind}
          </span>
        </div>
        <div className="ml-auto flex items-center gap-1.5">
          <Button size="sm" variant="ghost" isIconOnly aria-label="Refresh" onPress={onRefresh} isDisabled={busy}>
            <RefreshCw className={cn("size-4", refreshing && "animate-spin")} />
          </Button>
          <Button size="sm" variant="tertiary" isDisabled={!hasRemote || busy} isPending={sync?.op === "fetch"} onPress={() => onSync("fetch")}>
            <RefreshCw className="size-3.5" />
            Fetch
          </Button>
          <Button size="sm" variant={status.behind > 0 ? "secondary" : "tertiary"} isDisabled={!hasRemote || busy} isPending={sync?.op === "pull"} onPress={() => onSync("pull")}>
            <CloudDownload className="size-3.5" />
            Pull{status.behind > 0 ? ` ${status.behind}` : ""}
          </Button>
          <Button size="sm" variant={status.ahead > 0 ? "secondary" : "tertiary"} isDisabled={!hasRemote || busy} isPending={sync?.op === "push"} onPress={() => onSync("push")}>
            <CloudUpload className="size-3.5" />
            Push{status.ahead > 0 ? ` ${status.ahead}` : ""}
          </Button>
        </div>
      </div>
      <AnimatePresence initial={false}>
        {sync && (
          <motion.div
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: "auto" }}
            exit={{ opacity: 0, height: 0 }}
            transition={{ duration: 0.18 }}
            className="overflow-hidden"
          >
            <ProgressBar aria-label="Git progress" size="sm" value={percent ?? 0} isIndeterminate={percent === null} className="pt-1">
              <ProgressBar.Track>
                <ProgressBar.Fill />
              </ProgressBar.Track>
            </ProgressBar>
            <p className="mt-1.5 font-mono text-xs text-muted wrap-anywhere">{sync.progress?.line ?? `Starting ${sync.op}...`}</p>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

/* ---------- file groups ---------- */

function FileGroup({
  group,
  title,
  files,
  busy,
  onToggle,
  onDiscard,
  emptyText,
}: {
  group: Group;
  title: string;
  files: GitFileChange[];
  busy: Set<string>;
  onToggle: (group: Group, files: GitFileChange[]) => void;
  onDiscard: (files: GitFileChange[]) => void;
  emptyText: string;
}) {
  const totalSize = files.reduce((sum, file) => sum + (file.size ?? 0), 0);
  const anyBusy = files.some((file) => busy.has(file.path));

  return (
    <section className="rounded-lg bg-surface p-2">
      <div className="flex min-h-9 items-center gap-3 px-2 py-1">
        <Checkbox
          aria-label={group === "staged" ? "Unstage all" : "Stage all"}
          isSelected={group === "staged" && files.length > 0}
          isDisabled={files.length === 0 || anyBusy}
          onChange={() => onToggle(group, files)}
        >
          <Checkbox.Control>
            <Checkbox.Indicator />
          </Checkbox.Control>
        </Checkbox>
        <h3 className="text-sm font-semibold">{title}</h3>
        <span className="rounded-full bg-default px-1.5 text-xs text-muted tabular-nums">{files.length}</span>
        {totalSize > 0 && <span className="text-xs text-muted">{formatBytes(totalSize)}</span>}
        {group === "changes" && files.length > 1 && (
          <Button size="sm" variant="ghost" className="ml-auto h-7 text-xs text-muted hover:text-danger" onPress={() => onDiscard(files)} isDisabled={anyBusy}>
            <Undo2 className="size-3.5" />
            Discard all
          </Button>
        )}
      </div>

      {files.length === 0 ? (
        <p className="px-2 pt-1 pb-2 text-xs text-muted">{emptyText}</p>
      ) : (
        <ul className="flex flex-col">
          <AnimatePresence initial={false}>
            {files.map((file) => (
              <motion.li
                key={`${group}:${file.path}`}
                layout="position"
                initial={{ opacity: 0, y: -4 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0 }}
                transition={{ duration: 0.18 }}
              >
                <FileRow file={file} group={group} busy={busy.has(file.path)} onToggle={() => onToggle(group, [file])} onDiscard={() => onDiscard([file])} />
              </motion.li>
            ))}
          </AnimatePresence>
        </ul>
      )}
    </section>
  );
}

function FileRow({ file, group, busy, onToggle, onDiscard }: { file: GitFileChange; group: Group; busy: boolean; onToggle: () => void; onDiscard: () => void }) {
  const state = shownState(file, group);
  const tone = gitStateTone(state);

  return (
    <div className={cn("group flex items-center gap-3 rounded-sm px-2 py-1.5 transition-colors duration-200 hover:bg-default/60", busy && "opacity-60")}>
      <Checkbox aria-label={group === "staged" ? `Unstage ${file.path}` : `Stage ${file.path}`} isSelected={group === "staged"} isDisabled={busy} onChange={onToggle}>
        <Checkbox.Control>
          <Checkbox.Indicator />
        </Checkbox.Control>
      </Checkbox>
      <span className={cn("w-3 shrink-0 text-center font-mono text-xs font-semibold", TONE_TEXT[tone])} title={gitStateLabel(state)}>
        {gitStateLetter(state)}
      </span>
      <div className="min-w-0 flex-1 text-[13px] leading-5">
        <FilePath path={file.path} className={cn(state === "deleted" && "line-through decoration-muted")} />
        {file.origPath && (
          <span className="block text-xs text-muted wrap-anywhere">
            from <FilePath path={file.origPath} />
          </span>
        )}
      </div>
      {file.lfs && (
        <Chip size="sm" variant="soft" color="accent" className="shrink-0">
          LFS
        </Chip>
      )}
      <span className="w-16 shrink-0 text-right text-xs text-muted tabular-nums">{file.size !== null ? formatBytes(file.size) : ""}</span>
      {group === "changes" ? (
        <Button
          size="sm"
          variant="ghost"
          isIconOnly
          aria-label={`Discard ${file.path}`}
          className="size-7 min-w-7 opacity-0 group-hover:opacity-100 hover:text-danger focus-visible:opacity-100"
          isDisabled={busy}
          onPress={onDiscard}
        >
          <Undo2 className="size-3.5" />
        </Button>
      ) : (
        <span className="size-7 shrink-0" />
      )}
    </div>
  );
}

function DiscardBody({ files }: { files: GitFileChange[] }) {
  const untracked = files.filter((file) => file.worktree === "untracked");
  const shown = files.slice(0, 6);
  return (
    <div className="flex flex-col gap-2">
      <p>
        {untracked.length > 0
          ? `${untracked.length === files.length ? (files.length === 1 ? "This file is" : "These files are") : `${untracked.length} of them are`} not in git yet, so they get deleted and can not be restored.`
          : "The files go back to the last commit. This can not be undone."}
      </p>
      <ul className="flex flex-col gap-0.5 rounded-sm bg-default/60 px-2.5 py-2 font-mono text-xs text-foreground">
        {shown.map((file) => (
          <li key={file.path} className="wrap-anywhere">
            {file.path}
          </li>
        ))}
        {files.length > shown.length && <li className="text-muted">and {files.length - shown.length} more</li>}
      </ul>
    </div>
  );
}

/* ---------- GitHub card ---------- */

function GitHubCard({ project, refreshProject, onDone }: { project: ProjectInfo; refreshProject: () => Promise<void>; onDone: () => void }) {
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [progress, setProgress] = useState<string | null>(null);
  const name = project.config.addon.replace(/_/g, "-");

  const create = async () => {
    setPending(true);
    setError(null);
    setProgress(null);
    try {
      await call("git.publishRepo", { path: project.path, name, private: true }, { onEvent: (event) => setProgress(event.data.line) });
      await refreshProject();
      onDone();
    } catch (failure) {
      setError(failure);
    } finally {
      setPending(false);
    }
  };

  return (
    <Panel
      title="Put this on GitHub"
      description="Sources go to a GitHub repo, big binaries (images, sounds, prebuilt/) through Git LFS."
      actions={
        <Button variant="primary" size="sm" isPending={pending} onPress={() => void create()}>
          <Lock className="size-3.5" />
          Create private GitHub repo
        </Button>
      }
    >
      <div className="flex flex-col gap-3 text-[13px]">
        <p className="text-muted">
          GitHub gives 10 GiB of LFS storage free, then about $0.07 per GiB a month. Add a payment method on GitHub or pushes stop at the quota. The button needs
          <code className="mx-1 rounded-sm bg-default/60 px-1 font-mono text-xs text-foreground">gh auth login</code>
          once.
        </p>
        {pending && progress && <p className="font-mono text-xs text-muted wrap-anywhere">{progress}</p>}
        {error !== null && (
          <Notice tone="danger" title="Could not create the repo">
            {errorText(error)}
          </Notice>
        )}
        <Disclosure>
          <Disclosure.Heading>
            <Disclosure.Trigger className="flex cursor-pointer items-center gap-1.5 text-xs text-accent transition-colors duration-200">
              Do it by hand
              <Disclosure.Indicator />
            </Disclosure.Trigger>
          </Disclosure.Heading>
          <Disclosure.Content>
            <div className="flex flex-col gap-2 pt-2 text-xs text-muted">
              <p>1. Create an empty private repo on github.com (no README, no .gitignore).</p>
              <p>2. In the project folder run:</p>
              <CopyBlock text={`git remote add origin https://github.com/<you>/${name}.git\ngit push -u origin main`} />
              <p>
                All steps are in docs/GITHUB-LFS.md.{" "}
                <button
                  type="button"
                  className="cursor-pointer text-accent transition-colors duration-200 hover:underline"
                  onClick={() => void openExternal("https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-git-large-file-storage")}
                >
                  About Git LFS
                </button>
              </p>
            </div>
          </Disclosure.Content>
        </Disclosure>
      </div>
    </Panel>
  );
}
