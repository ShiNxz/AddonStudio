// Clone an addon repo: URL and target folder, live git progress, cancel, log in a modal.
import { useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Button, Description, FieldError, Input, Label, Modal, ProgressBar, TextField } from "@heroui/react";
import { ScrollText } from "lucide-react";
import { call } from "../../rpc/client";
import { joinPath } from "../../app/platform";
import { LogModal } from "../../components/LogModal";
import { FolderField, useParentFolder } from "./FolderField";
import { isValidFolderName, repoNameFromUrl } from "./naming";
import type { AddFlowProps } from "./AddProjectModal";

interface Progress {
  phase: string;
  percent: number | null;
  line: string;
}

const MAX_LOG_LINES = 2000;

export function CloneForm({ onDone, onCancel, setBusy }: AddFlowProps) {
  const [url, setUrl] = useState("");
  const [folder, setFolder] = useState("");
  const [folderEdited, setFolderEdited] = useState(false);
  const [parent, setParent] = useParentFolder();
  const [progress, setProgress] = useState<Progress | null>(null);
  const [log, setLog] = useState<string[]>([]);
  const [logOpen, setLogOpen] = useState(false);
  const abort = useRef<AbortController | null>(null);

  const name = folderEdited ? folder : repoNameFromUrl(url);
  const path = parent.trim() && name ? joinPath(parent.trim(), name) : "";
  const urlLooksOk = /^(https?:\/\/|git@|ssh:\/\/|file:\/\/|[a-zA-Z]:\\|\\\\)/.test(url.trim());
  const nameInvalid = name.length > 0 && !isValidFolderName(name);
  const canClone = urlLooksOk && name.length > 0 && !nameInvalid && parent.trim().length > 0;

  const clone = useMutation({
    mutationFn: async () => {
      const controller = new AbortController();
      abort.current = controller;
      setProgress({ phase: "Starting", percent: null, line: "" });
      setLog([]);
      return call(
        "projects.clone",
        { url: url.trim(), path },
        {
          signal: controller.signal,
          onEvent: (message) => {
            if (message.event !== "git.progress") return;
            const { phase, percent, line } = message.data;
            setProgress({ phase, percent, line });
            if (line) setLog((current) => (current.length >= MAX_LOG_LINES ? [...current.slice(-MAX_LOG_LINES + 1), line] : [...current, line]));
          },
        },
      );
    },
    onMutate: () => setBusy(true),
    onSettled: () => {
      setBusy(false);
      abort.current = null;
    },
    onSuccess: (project) => {
      setParent(parent.trim());
      onDone(project);
    },
    onError: () => setProgress(null),
  });

  const running = clone.isPending;

  return (
    <form
      className="contents"
      onSubmit={(event) => {
        event.preventDefault();
        if (canClone && !running) clone.mutate();
      }}
    >
      <Modal.Body className="mt-5 flex flex-col gap-4">
        <TextField value={url} onChange={setUrl} autoFocus isDisabled={running}>
          <Label>Repository URL</Label>
          <Input placeholder="https://github.com/you/next-hud.git" className="w-full font-mono text-[13px]" />
          <Description>HTTPS or SSH. Git LFS files are pulled after the clone.</Description>
        </TextField>

        <TextField
          value={name}
          onChange={(value) => {
            setFolderEdited(true);
            setFolder(value);
          }}
          isInvalid={nameInvalid}
          isDisabled={running}
        >
          <Label>Folder name</Label>
          <Input placeholder="next-hud" className="w-full font-mono text-[13px]" />
          {nameInvalid && <FieldError>Windows does not allow this folder name.</FieldError>}
        </TextField>

        <FolderField
          label="Put it in"
          value={parent}
          onChange={setParent}
          isDisabled={running}
          pickerTitle="Where the cloned project goes"
          description={path ? `Clones into ${path}` : undefined}
        />

        {progress && (
          <div className="flex flex-col gap-2 rounded-lg bg-surface-secondary px-4 py-3">
            <div className="flex items-center justify-between gap-3 text-caption">
              <span className="font-semibold text-foreground">{progress.phase}</span>
              <span className="text-muted">{progress.percent !== null ? `${Math.round(progress.percent)}%` : ""}</span>
            </div>
            <ProgressBar
              aria-label="Clone progress"
              size="sm"
              value={progress.percent ?? 0}
              isIndeterminate={progress.percent === null}
            >
              <ProgressBar.Track>
                <ProgressBar.Fill />
              </ProgressBar.Track>
            </ProgressBar>
            <div className="flex items-center justify-between gap-3">
              <span className="min-w-0 flex-1 truncate font-mono text-caption text-muted">{progress.line || " "}</span>
              <Button size="sm" variant="ghost" onPress={() => setLogOpen(true)} isDisabled={log.length === 0}>
                <ScrollText />
                Show log
              </Button>
            </div>
          </div>
        )}
      </Modal.Body>
      <Modal.Footer className="mt-6">
        {running ? (
          <Button variant="secondary" onPress={() => abort.current?.abort()}>
            Cancel clone
          </Button>
        ) : (
          <Button variant="secondary" onPress={onCancel}>
            Cancel
          </Button>
        )}
        <Button type="submit" variant="primary" isDisabled={!canClone} isPending={running}>
          Clone
        </Button>
      </Modal.Footer>
      <LogModal isOpen={logOpen} onOpenChange={setLogOpen} title="Clone log" subtitle={url} log={log.join("\n")} follow={running} />
    </form>
  );
}
