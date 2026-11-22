// A global confirm dialog: `if (await confirm({ title: "Discard 3 files?", tone: "danger" })) ...`.
// <ConfirmHost /> is rendered once at the root (main.tsx).
import { useRef, useSyncExternalStore, type ReactNode } from "react";
import { AlertDialog, Button } from "@heroui/react";

export interface ConfirmOptions {
  title: string;
  body?: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  /** danger = red confirm button (deletes, discards); warning = renames or replaces something. */
  tone?: "default" | "warning" | "danger";
}

interface ConfirmRequest extends ConfirmOptions {
  id: number;
  resolve: (ok: boolean) => void;
}

let queue: ConfirmRequest[] = [];
let nextId = 0;
const listeners = new Set<() => void>();

function emit() {
  for (const listener of [...listeners]) listener();
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function current(): ConfirmRequest | null {
  return queue[0] ?? null;
}

/** Asks the user; resolves true on confirm, false on cancel or Escape. Requests queue up one at a time. */
export function confirm(options: ConfirmOptions): Promise<boolean> {
  return new Promise((resolve) => {
    queue = [...queue, { ...options, id: ++nextId, resolve }];
    emit();
  });
}

function settle(id: number, ok: boolean) {
  const request = queue.find((entry) => entry.id === id);
  if (!request) return;
  queue = queue.filter((entry) => entry.id !== id);
  request.resolve(ok);
  emit();
}

export function ConfirmHost() {
  const request = useSyncExternalStore(subscribe, current, current);
  // keep the last request on screen while the dialog animates out
  const last = useRef<ConfirmRequest | null>(null);
  if (request) last.current = request;
  const shown = request ?? last.current;
  const status = shown?.tone === "danger" ? "danger" : shown?.tone === "warning" ? "warning" : "accent";

  return (
    <AlertDialog.Backdrop
      isOpen={request !== null}
      isDismissable
      isKeyboardDismissDisabled={false}
      onOpenChange={(open) => {
        if (!open && request) settle(request.id, false);
      }}
    >
      <AlertDialog.Container size="sm">
        <AlertDialog.Dialog className="sm:max-w-[440px]">
          {shown && (
            <>
              <AlertDialog.Header>
                <AlertDialog.Icon status={status} />
                <AlertDialog.Heading>{shown.title}</AlertDialog.Heading>
              </AlertDialog.Header>
              {shown.body && <AlertDialog.Body className="mt-2 text-body text-fg-secondary">{shown.body}</AlertDialog.Body>}
              <AlertDialog.Footer className="mt-6">
                <Button variant="secondary" onPress={() => settle(shown.id, false)}>
                  {shown.cancelLabel ?? "Cancel"}
                </Button>
                <Button autoFocus variant={shown.tone === "danger" ? "danger" : "primary"} onPress={() => settle(shown.id, true)}>
                  {shown.confirmLabel ?? "OK"}
                </Button>
              </AlertDialog.Footer>
            </>
          )}
        </AlertDialog.Dialog>
      </AlertDialog.Container>
    </AlertDialog.Backdrop>
  );
}
