// History: commits from git.log, paged. Version chips, a Deployed badge for commits with deploy tags, and a
// modal with the files a commit changed (git.show).
import { useState } from "react";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { Button, Chip, Modal, Skeleton, cn } from "@heroui/react";
import { motion } from "motion/react";
import { CloudUpload, Tag } from "lucide-react";
import { call } from "../../rpc/client";
import type { GitCommit } from "../../rpc/types";
import type { ProjectViewProps } from "./types";
import { formatDateTime, gitStateLabel, gitStateLetter, gitStateTone, plural, relativeTime, workshopUrl, type Tone } from "./logic";
import { CopyBlock, ErrorNotice, FilePath, LinkButton, ListSkeleton, Notice, ViewPage, listItemMotion } from "./ui";

const PAGE = 40;

const TONE_TEXT: Record<Tone, string> = {
  success: "text-success",
  warning: "text-warning",
  danger: "text-danger",
  accent: "text-accent",
  muted: "text-muted",
};

export function HistoryView({ project }: ProjectViewProps) {
  const [open, setOpen] = useState<GitCommit | null>(null);
  const log = useInfiniteQuery({
    queryKey: ["git.log", project.path],
    queryFn: ({ pageParam }) => call("git.log", { path: project.path, limit: PAGE, skip: pageParam }),
    initialPageParam: 0,
    getNextPageParam: (last, pages) => (last.length < PAGE ? undefined : pages.reduce((sum, page) => sum + page.length, 0)),
  });

  if (log.isPending) {
    return (
      <ViewPage>
        <div className="rounded-lg bg-surface p-2">
          <CommitSkeleton rows={8} />
        </div>
      </ViewPage>
    );
  }

  if (log.isError) {
    return (
      <ViewPage>
        <ErrorNotice title="Could not read the history" error={log.error} onRetry={() => void log.refetch()} />
      </ViewPage>
    );
  }

  const commits = log.data.pages.flat();

  if (commits.length === 0) {
    return (
      <ViewPage>
        <Notice title="No commits yet">Commit your first changes on the Changes tab.</Notice>
      </ViewPage>
    );
  }

  return (
    <ViewPage>
      <div className="rounded-lg bg-surface p-2">
        <ol className="flex flex-col">
          {commits.map((commit, index) => (
            <motion.li key={commit.hash} {...listItemMotion(index % PAGE)}>
              <CommitRow commit={commit} last={index === commits.length - 1} onOpen={() => setOpen(commit)} />
            </motion.li>
          ))}
        </ol>
        {log.isFetchingNextPage && <CommitSkeleton rows={3} />}
        <div className="flex items-center justify-between px-3 pt-2 pb-1">
          <span className="text-xs text-muted">{plural(commits.length, "commit")}</span>
          {log.hasNextPage ? (
            <Button size="sm" variant="tertiary" isPending={log.isFetchingNextPage} onPress={() => void log.fetchNextPage()}>
              Load more
            </Button>
          ) : (
            <span className="text-xs text-muted">That is all of them</span>
          )}
        </div>
      </div>

      <CommitModal path={project.path} commit={open} onClose={() => setOpen(null)} />
    </ViewPage>
  );
}

function CommitRow({ commit, last, onOpen }: { commit: GitCommit; last: boolean; onOpen: () => void }) {
  const deploy = commit.deploys[0] ?? null;
  const refs = commit.refs.filter((ref) => ref !== "origin/HEAD");

  return (
    <div className="group relative flex gap-3 rounded-sm transition-colors duration-200 hover:bg-default/60">
      {/* timeline */}
      <div className="relative flex w-6 shrink-0 justify-center" aria-hidden>
        <span className={cn("absolute top-0 w-px bg-separator", last ? "h-4" : "h-full")} />
        <span className={cn("relative mt-3 size-2.5 rounded-full", deploy ? "bg-success" : commit.version ? "bg-accent" : "bg-default-foreground/40")} />
      </div>

      <button type="button" onClick={onOpen} className="flex min-w-0 flex-1 cursor-pointer flex-col gap-1 py-2 pr-3 text-left">
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
          {commit.version && (
            <Chip size="sm" variant="soft" color="accent" className="shrink-0">
              <Tag className="size-3" />v{commit.version}
            </Chip>
          )}
          <span className="min-w-0 text-[13px] font-medium wrap-anywhere">{commit.subject || "(no message)"}</span>
          {refs.map((ref) => (
            <span key={ref} className="rounded-sm bg-default px-1.5 text-[11px] text-muted">
              {ref.replace("HEAD -> ", "")}
            </span>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
          <span className="font-mono">{commit.short}</span>
          <span>{commit.authorName}</span>
          <span title={formatDateTime(commit.date)}>{relativeTime(commit.date)}</span>
        </div>
      </button>

      {deploy && (
        <div className="flex shrink-0 items-center gap-1 self-center pr-2">
          <Chip size="sm" variant="soft" color="success">
            <CloudUpload className="size-3" />
            <span title={formatDateTime(deploy.at)}>
              Deployed {relativeTime(deploy.at)}
              {commit.deploys.length > 1 ? `, ${commit.deploys.length}x` : ""}
            </span>
          </Chip>
          {deploy.workshopId && <LinkButton href={workshopUrl(deploy.workshopId)}>Workshop</LinkButton>}
        </div>
      )}
    </div>
  );
}

function CommitSkeleton({ rows }: { rows: number }) {
  return (
    <div className="flex flex-col" aria-hidden>
      {Array.from({ length: rows }, (_, index) => (
        <div key={index} className="flex gap-3 py-2">
          <div className="flex w-6 justify-center pt-1">
            <Skeleton className="size-2.5 rounded-full" />
          </div>
          <div className="flex flex-1 flex-col gap-2">
            <Skeleton className={cn("h-3.5 rounded-sm", index % 3 === 0 ? "w-1/2" : index % 3 === 1 ? "w-2/3" : "w-2/5")} />
            <Skeleton className="h-3 w-48 rounded-sm" />
          </div>
        </div>
      ))}
    </div>
  );
}

function CommitModal({ path, commit, onClose }: { path: string; commit: GitCommit | null; onClose: () => void }) {
  const detail = useQuery({
    queryKey: ["git.show", path, commit?.hash],
    queryFn: () => call("git.show", { path, hash: commit!.hash }),
    enabled: commit !== null,
    staleTime: Infinity,
  });
  const files = detail.data?.files ?? [];
  const additions = files.reduce((sum, file) => sum + (file.additions ?? 0), 0);
  const deletions = files.reduce((sum, file) => sum + (file.deletions ?? 0), 0);

  return (
    <Modal.Backdrop
      isOpen={commit !== null}
      onOpenChange={(isOpen) => {
        if (!isOpen) onClose();
      }}
    >
      <Modal.Container size="lg" scroll="inside">
        <Modal.Dialog className="sm:max-w-2xl">
          <Modal.CloseTrigger />
          {commit && (
            <>
              <Modal.Header className="flex-col items-start gap-1.5">
                <Modal.Heading className="pr-8 wrap-anywhere">{commit.subject || "(no message)"}</Modal.Heading>
                <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
                  <span>{commit.authorName}</span>
                  <span>{formatDateTime(commit.date)}</span>
                  {commit.version && (
                    <Chip size="sm" variant="soft" color="accent">
                      v{commit.version}
                    </Chip>
                  )}
                </div>
              </Modal.Header>
              <Modal.Body className="flex flex-col gap-4">
                {commit.body && <p className="text-[13px] whitespace-pre-wrap wrap-anywhere">{commit.body}</p>}
                <CopyBlock text={commit.hash} />

                {commit.deploys.length > 0 && (
                  <div className="flex flex-col gap-1.5">
                    <h4 className="text-xs font-semibold text-muted">Deploys</h4>
                    {commit.deploys.map((deploy) => (
                      <div key={deploy.tag} className="flex flex-wrap items-center gap-2 rounded-sm bg-success-soft px-2.5 py-1.5 text-xs text-success-soft-foreground">
                        <CloudUpload className="size-3.5" />
                        <span className="font-mono">{deploy.tag}</span>
                        <span>{formatDateTime(deploy.at)}</span>
                        {deploy.note && <span className="opacity-80 wrap-anywhere">{deploy.note}</span>}
                        {deploy.workshopId && (
                          <LinkButton href={workshopUrl(deploy.workshopId)} className="ml-auto h-6 text-inherit">
                            {deploy.workshopId}
                          </LinkButton>
                        )}
                      </div>
                    ))}
                  </div>
                )}

                <div className="flex flex-col gap-1.5">
                  <div className="flex items-center gap-3">
                    <h4 className="text-xs font-semibold text-muted">Files</h4>
                    {detail.data && (
                      <span className="text-xs text-muted tabular-nums">
                        {plural(files.length, "file")}
                        {additions > 0 && <span className="ml-2 text-success">+{additions}</span>}
                        {deletions > 0 && <span className="ml-1.5 text-danger">-{deletions}</span>}
                      </span>
                    )}
                  </div>
                  {detail.isPending ? (
                    <ListSkeleton rows={4} />
                  ) : detail.isError ? (
                    <ErrorNotice title="Could not load the files" error={detail.error} onRetry={() => void detail.refetch()} />
                  ) : files.length === 0 ? (
                    <p className="text-xs text-muted">No file changes (a merge or an empty commit).</p>
                  ) : (
                    <ul className="flex flex-col">
                      {files.map((file) => {
                        const tone = gitStateTone(file.status);
                        return (
                          <li key={file.path} className="flex items-center gap-3 rounded-sm px-2 py-1.5 text-[13px] hover:bg-default/60">
                            <span className={cn("w-3 shrink-0 text-center font-mono text-xs font-semibold", TONE_TEXT[tone])} title={gitStateLabel(file.status)}>
                              {gitStateLetter(file.status)}
                            </span>
                            <FilePath path={file.path} className="flex-1" />
                            {file.binary ? (
                              <span className="shrink-0 rounded-sm bg-default px-1.5 text-[11px] text-muted">binary</span>
                            ) : (
                              <span className="shrink-0 text-xs tabular-nums">
                                <span className="text-success">+{file.additions ?? 0}</span>
                                <span className="ml-1.5 text-danger">-{file.deletions ?? 0}</span>
                              </span>
                            )}
                          </li>
                        );
                      })}
                    </ul>
                  )}
                </div>
              </Modal.Body>
              <Modal.Footer>
                <Button slot="close" variant="secondary">
                  Close
                </Button>
              </Modal.Footer>
            </>
          )}
        </Modal.Dialog>
      </Modal.Container>
    </Modal.Backdrop>
  );
}
