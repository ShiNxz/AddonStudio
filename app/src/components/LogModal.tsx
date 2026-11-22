import { useEffect, useRef } from "react";
import { Button, Modal } from "@heroui/react";
import { ScrollText } from "lucide-react";
import { CopyButton } from "./CopyButton";

interface LogModalProps {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** Shown under the title, e.g. the file or the command. */
  subtitle?: string;
  log: string;
  /** Keep the view pinned to the end while the log grows. */
  follow?: boolean;
}

/** Logs always open in a modal: wrapped monospace text with a copy button. */
export function LogModal({ isOpen, onOpenChange, title, subtitle, log, follow = false }: LogModalProps) {
  const endRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (isOpen && follow) endRef.current?.scrollIntoView({ block: "end" });
  }, [isOpen, follow, log]);

  return (
    <Modal.Backdrop isOpen={isOpen} onOpenChange={onOpenChange}>
      <Modal.Container size="lg" scroll="inside">
        <Modal.Dialog className="max-h-[80vh] sm:max-w-[760px]">
          <Modal.CloseTrigger />
          <Modal.Header>
            <Modal.Icon className="bg-default text-foreground">
              <ScrollText className="size-[18px]" />
            </Modal.Icon>
            <div className="min-w-0">
              <Modal.Heading>{title}</Modal.Heading>
              {subtitle && <p className="text-caption break-all text-muted">{subtitle}</p>}
            </div>
          </Modal.Header>
          <Modal.Body className="mt-4">
            <div className="relative rounded-sm bg-surface-secondary">
              <div className="absolute top-1 right-1">
                <CopyButton value={log} label="Copy log" />
              </div>
              <pre className="min-h-24 p-3 pr-10 text-[12.5px] leading-[18px] text-fg-secondary">{log.length > 0 ? log : "No output."}</pre>
              <div ref={endRef} />
            </div>
          </Modal.Body>
          <Modal.Footer className="mt-5">
            <Button slot="close" variant="secondary">
              Close
            </Button>
          </Modal.Footer>
        </Modal.Dialog>
      </Modal.Container>
    </Modal.Backdrop>
  );
}
