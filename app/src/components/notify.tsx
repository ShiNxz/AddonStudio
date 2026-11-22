// Toasts. Errors show the RPC error code next to the message and offer a copy action.
import { toast } from "@heroui/react";
import { isRpcError } from "../rpc/client";
import { copyText } from "./clipboard";

export interface ErrorInfo {
  code: string | null;
  message: string;
}

/** The code and message of anything thrown (RpcError from the core, or a plain Error). */
export function describeError(error: unknown): ErrorInfo {
  if (isRpcError(error)) return { code: error.code, message: error.message };
  if (error instanceof Error) return { code: null, message: error.message };
  return { code: null, message: String(error) };
}

/** True for errors the user caused on purpose (a cancelled build), which never get a toast. */
export function isCancelled(error: unknown): boolean {
  return isRpcError(error, "cancelled");
}

/** Shows an error toast with the code and message. Cancelled requests are ignored. */
export function notifyError(error: unknown, title?: string): void {
  if (isCancelled(error)) return;
  const { code, message } = describeError(error);
  const id = toast.danger(title ?? message, {
    description: (
      <span className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 selectable">
        {code && <code className="rounded-sm bg-danger-soft px-1 text-caption text-danger-soft-foreground">{code}</code>}
        {title && <span className="text-fg-secondary">{message}</span>}
      </span>
    ),
    timeout: 8000,
    actionProps: {
      children: "Copy",
      variant: "tertiary",
      size: "sm",
      onPress: () => {
        void copyText(code ? `${code}: ${message}` : message);
        toast.close(id);
      },
    },
  });
}

export function notifySuccess(title: string, description?: string): void {
  toast.success(title, { description });
}

export function notifyInfo(title: string, description?: string): void {
  toast.info(title, { description });
}

export function notifyWarning(title: string, description?: string): void {
  toast.warning(title, { description, timeout: 7000 });
}
