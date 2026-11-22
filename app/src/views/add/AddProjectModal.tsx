// Add project: create a new addon repo, clone one, or import an existing CS2 addon.
import { useRef, useState } from "react";
import { Modal } from "@heroui/react";
import { FolderPlus, GitFork, Import } from "lucide-react";
import type { ProjectInfo } from "../../rpc/types";
import { Segmented } from "../../components/Segmented";
import { CreateForm } from "./CreateForm";
import { CloneForm } from "./CloneForm";
import { ImportFlow } from "./ImportFlow";

export type AddMode = "create" | "clone" | "import";

const MODES = [
  { id: "create", label: "Create new", icon: FolderPlus },
  { id: "clone", label: "Clone URL", icon: GitFork },
  { id: "import", label: "Import CS2 addon", icon: Import },
] as const;

interface AddProjectModalProps {
  /** null = closed. */
  mode: AddMode | null;
  onModeChange: (mode: AddMode) => void;
  onClose: () => void;
  /** Called with the new project; the shell opens it. */
  onOpened: (project: ProjectInfo) => void;
}

/** What the flows tell the modal: busy blocks closing and switching while a clone or import runs. */
export interface AddFlowProps {
  onDone: (project: ProjectInfo) => void;
  onCancel: () => void;
  setBusy: (busy: boolean) => void;
}

export function AddProjectModal({ mode, onModeChange, onClose, onOpened }: AddProjectModalProps) {
  const [busy, setBusy] = useState(false);
  // keep showing the last mode while the dialog animates out
  const last = useRef<AddMode>("create");
  if (mode) last.current = mode;
  const shown = mode ?? last.current;
  // a fresh form each time the modal opens
  const [session, setSession] = useState(0);

  const close = () => {
    if (busy) return;
    onClose();
    setSession((value) => value + 1);
  };

  const flow: AddFlowProps = {
    onDone: (project) => {
      setBusy(false);
      onOpened(project);
      onClose();
      setSession((value) => value + 1);
    },
    onCancel: close,
    setBusy,
  };

  return (
    <Modal.Backdrop
      isOpen={mode !== null}
      isDismissable={!busy}
      isKeyboardDismissDisabled={busy}
      onOpenChange={(open) => {
        if (!open) close();
      }}
    >
      <Modal.Container size="lg" scroll="inside">
        <Modal.Dialog className={`max-h-[86vh] transition-[max-width] duration-200 ${shown === "import" ? "sm:max-w-[920px]" : "sm:max-w-[600px]"}`}>
          {!busy && <Modal.CloseTrigger />}
          <Modal.Header className="flex-col items-start gap-4">
            <Modal.Heading>Add a project</Modal.Heading>
            <div className={busy ? "pointer-events-none opacity-60" : ""}>
              <Segmented aria-label="How to add the project" options={MODES} value={shown} onChange={(next) => !busy && onModeChange(next)} />
            </div>
          </Modal.Header>
          {shown === "create" && <CreateForm key={`create-${session}`} {...flow} />}
          {shown === "clone" && <CloneForm key={`clone-${session}`} {...flow} />}
          {shown === "import" && <ImportFlow key={`import-${session}`} {...flow} />}
        </Modal.Dialog>
      </Modal.Container>
    </Modal.Backdrop>
  );
}
