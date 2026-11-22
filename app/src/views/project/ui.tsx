// Small building blocks shared by the project views: fill-only panels, notices, copyable paths, log and confirm
// dialogs, skeleton rows and a segmented control with a sliding selection.
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { AlertDialog, Button, Modal, Skeleton, cn } from "@heroui/react";
import { motion } from "motion/react";
import { Check, CircleCheck, CircleExclamationPoint, Copy, ExternalLink, Info, RotateCcw, TriangleExclamationPoint } from "lucide-react";
import { isRpcError, isTauri } from "../../rpc/client";
import { classifyLogLine, splitPath } from "./logic";

/* ---------- layout ---------- */

/** The scroll content of one tab. */
export function ViewPage({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("mx-auto flex w-full max-w-6xl flex-col gap-4 px-6 pt-4 pb-8", className)}>{children}</div>;
}

interface PanelProps {
  title?: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
  className?: string;
}

/** A card: fill only, no outline. */
export function Panel({ title, description, actions, children, className }: PanelProps) {
  const hasHeader = title !== undefined || actions !== undefined;
  return (
    <section className={cn("rounded-lg bg-surface p-4 shadow-[0_1px_2px_rgba(0,0,0,0.06)]", className)}>
      {hasHeader && (
        <header className={cn("flex flex-wrap items-start justify-between gap-3", children !== undefined && "mb-3")}>
          <div className="min-w-0">
            {title !== undefined && <h3 className="text-sm font-semibold">{title}</h3>}
            {description !== undefined && <p className="mt-0.5 text-xs text-muted">{description}</p>}
          </div>
          {actions !== undefined && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}

export type NoticeTone = "info" | "warning" | "danger" | "success";

const NOTICE_TONE: Record<NoticeTone, string> = {
  info: "bg-accent-soft text-accent-soft-foreground",
  warning: "bg-warning-soft text-warning-soft-foreground",
  danger: "bg-danger-soft text-danger-soft-foreground",
  success: "bg-success-soft text-success-soft-foreground",
};

const NOTICE_ICON: Record<NoticeTone, typeof Info> = {
  info: Info,
  warning: TriangleExclamationPoint,
  danger: CircleExclamationPoint,
  success: CircleCheck,
};

export function Notice({
  tone = "info",
  title,
  children,
  action,
  className,
}: {
  tone?: NoticeTone;
  title: ReactNode;
  children?: ReactNode;
  action?: ReactNode;
  className?: string;
}) {
  const Icon = NOTICE_ICON[tone];
  return (
    <div role={tone === "danger" ? "alert" : "status"} className={cn("flex items-start gap-3 rounded-lg px-3.5 py-3", NOTICE_TONE[tone], className)}>
      <Icon className="mt-0.5 size-4 shrink-0" aria-hidden />
      <div className="min-w-0 flex-1 text-sm wrap-anywhere">
        <div className="font-medium">{title}</div>
        {children !== undefined && <div className="mt-0.5 text-[13px] opacity-90">{children}</div>}
      </div>
      {action !== undefined && <div className="shrink-0">{action}</div>}
    </div>
  );
}

/** The message of an RPC (or any) error. */
export function errorText(error: unknown): string {
  if (error instanceof Error) return error.message;
  return String(error);
}

export function ErrorNotice({ error, title, onRetry }: { error: unknown; title?: string; onRetry?: () => void }) {
  const code = isRpcError(error) ? error.code : null;
  return (
    <Notice
      tone="danger"
      title={title ?? "Something went wrong"}
      action={
        onRetry ? (
          <Button size="sm" variant="ghost" onPress={onRetry} className="text-inherit">
            <RotateCcw className="size-3.5" />
            Retry
          </Button>
        ) : undefined
      }
    >
      {errorText(error)}
      {code !== null && code !== "internal" && <span className="ml-1.5 font-mono text-xs opacity-75">({code})</span>}
    </Notice>
  );
}

/* ---------- text bits ---------- */

/** dir muted, file name normal; wraps after a slash first, inside a name only when it has to. */
export function FilePath({ path, className }: { path: string; className?: string }) {
  const { dir, name } = splitPath(path);
  const parts = dir.split(/(?<=[\\/])/).filter(Boolean);
  return (
    <span className={cn("min-w-0 wrap-break-word", className)}>
      {parts.length > 0 && (
        <span className="text-muted">
          {parts.map((part, index) => (
            <span key={index}>
              {part}
              <wbr />
            </span>
          ))}
        </span>
      )}
      <span>{name}</span>
    </span>
  );
}

async function copyText(text: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const area = document.createElement("textarea");
    area.value = text;
    area.style.position = "fixed";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();
    document.execCommand("copy");
    area.remove();
  }
}

export function CopyButton({ text, label = "Copy", className }: { text: string; label?: string; className?: string }) {
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!copied) return;
    const timer = setTimeout(() => setCopied(false), 1500);
    return () => clearTimeout(timer);
  }, [copied]);

  return (
    <Button
      isIconOnly
      size="sm"
      variant="ghost"
      aria-label={copied ? "Copied" : label}
      className={cn("size-7 min-w-7 shrink-0", className)}
      onPress={() => void copyText(text).then(() => setCopied(true))}
    >
      {copied ? <Check className="size-3.5 text-success" /> : <Copy className="size-3.5" />}
    </Button>
  );
}

/** A path or command: monospace, wraps, with a copy button. */
export function CopyBlock({ text, className }: { text: string; className?: string }) {
  return (
    <div className={cn("flex items-start gap-1 rounded-sm bg-default/60 py-0.5 pr-0.5 pl-2.5", className)}>
      <code className="min-w-0 flex-1 py-1 font-mono text-xs leading-5 wrap-anywhere whitespace-pre-wrap">{text}</code>
      <CopyButton text={text} />
    </div>
  );
}

/* ---------- links ---------- */

export async function openExternal(url: string): Promise<void> {
  if (isTauri()) {
    const { openUrl } = await import("@tauri-apps/plugin-opener");
    await openUrl(url);
  } else {
    window.open(url, "_blank", "noopener,noreferrer");
  }
}

/** Opens a folder in Explorer (desktop only). */
export async function openFolder(path: string): Promise<void> {
  if (!isTauri()) return;
  const { openPath } = await import("@tauri-apps/plugin-opener");
  await openPath(path);
}

export function LinkButton({ href, children, className }: { href: string; children: ReactNode; className?: string }) {
  return (
    <Button size="sm" variant="ghost" className={cn("h-7 gap-1.5 px-2 text-accent", className)} onPress={() => void openExternal(href)}>
      {children}
      <ExternalLink className="size-3.5" />
    </Button>
  );
}

/* ---------- segmented control ---------- */

export interface SegmentOption<T extends string> {
  id: T;
  label: string;
  count?: number;
}

/** A small filter switcher; the selected background slides between items. */
export function Segmented<T extends string>({
  value,
  onChange,
  options,
  label,
}: {
  value: T;
  onChange: (value: T) => void;
  options: SegmentOption<T>[];
  label: string;
}) {
  const groupId = useId();
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex shrink-0 rounded-sm bg-default/60 p-0.5">
      {options.map((option) => {
        const active = option.id === value;
        return (
          <button
            key={option.id}
            type="button"
            role="radio"
            aria-checked={active}
            onClick={() => onChange(option.id)}
            className={cn(
              "relative cursor-pointer rounded-[3px] px-2.5 py-1 text-xs transition-colors duration-200",
              active ? "text-foreground" : "text-muted hover:text-foreground",
            )}
          >
            {active && (
              <motion.span
                layoutId={`segment-${groupId}`}
                className="absolute inset-0 rounded-[3px] bg-surface shadow-[0_1px_2px_rgba(0,0,0,0.12)]"
                transition={{ type: "spring", bounce: 0, duration: 0.25 }}
              />
            )}
            <span className="relative">
              {option.label}
              {option.count !== undefined && <span className="ml-1.5 text-muted tabular-nums">{option.count}</span>}
            </span>
          </button>
        );
      })}
    </div>
  );
}

/* ---------- skeletons ---------- */

/** Rows shaped like a file list: optional checkbox, a letter, a path and a size. */
export function ListSkeleton({ rows = 5, leading = true }: { rows?: number; leading?: boolean }) {
  const widths = ["w-3/5", "w-2/5", "w-1/2", "w-2/3", "w-1/3", "w-3/4"];
  return (
    <div className="flex flex-col gap-1" aria-hidden>
      {Array.from({ length: rows }, (_, index) => (
        <div key={index} className="flex items-center gap-3 px-2 py-2">
          {leading && <Skeleton className="size-4 rounded-sm" />}
          <Skeleton className={cn("h-3.5 rounded-sm", widths[index % widths.length])} />
          <Skeleton className="ml-auto h-3 w-12 rounded-sm" />
        </div>
      ))}
    </div>
  );
}

export function PanelSkeleton({ lines = 3, className }: { lines?: number; className?: string }) {
  return (
    <div className={cn("rounded-lg bg-surface p-4", className)} aria-hidden>
      <Skeleton className="mb-4 h-4 w-40 rounded-sm" />
      <div className="flex flex-col gap-2.5">
        {Array.from({ length: lines }, (_, index) => (
          <Skeleton key={index} className={cn("h-3 rounded-sm", index % 2 === 0 ? "w-4/5" : "w-3/5")} />
        ))}
      </div>
    </div>
  );
}

/* ---------- dialogs ---------- */

export function ConfirmDialog({
  isOpen,
  onOpenChange,
  title,
  children,
  confirmLabel,
  tone = "danger",
  onConfirm,
}: {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  children: ReactNode;
  confirmLabel: string;
  tone?: "danger" | "warning" | "accent";
  onConfirm: () => void | Promise<void>;
}) {
  const [pending, setPending] = useState(false);

  const confirm = async () => {
    setPending(true);
    try {
      await onConfirm();
      onOpenChange(false);
    } finally {
      setPending(false);
    }
  };

  return (
    <AlertDialog.Backdrop isOpen={isOpen} onOpenChange={onOpenChange} isDismissable={!pending}>
      <AlertDialog.Container size="sm">
        <AlertDialog.Dialog>
          <AlertDialog.Header>
            <AlertDialog.Icon status={tone} />
            <AlertDialog.Heading>{title}</AlertDialog.Heading>
          </AlertDialog.Header>
          <AlertDialog.Body>
            <div className="text-sm text-muted wrap-anywhere">{children}</div>
          </AlertDialog.Body>
          <AlertDialog.Footer>
            <Button slot="close" variant="tertiary" isDisabled={pending}>
              Cancel
            </Button>
            <Button variant={tone === "danger" ? "danger" : "primary"} isPending={pending} onPress={() => void confirm()}>
              {confirmLabel}
            </Button>
          </AlertDialog.Footer>
        </AlertDialog.Dialog>
      </AlertDialog.Container>
    </AlertDialog.Backdrop>
  );
}

/** A compiler or git log; error lines get a red fill, warnings amber. Follows the end while live. */
export function LogModal({
  isOpen,
  onOpenChange,
  title,
  subtitle,
  text,
  errors = [],
  live = false,
}: {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  subtitle?: ReactNode;
  text: string;
  errors?: string[];
  live?: boolean;
}) {
  const scroller = useRef<HTMLPreElement | null>(null);
  const lines = text === "" ? [] : text.replace(/\r\n/g, "\n").replace(/\n$/, "").split("\n");
  const errorCount = lines.filter((line) => classifyLogLine(line) === "error").length;

  useEffect(() => {
    if (!live || !scroller.current) return;
    scroller.current.scrollTop = scroller.current.scrollHeight;
  }, [live, lines.length]);

  return (
    <Modal.Backdrop isOpen={isOpen} onOpenChange={onOpenChange}>
      <Modal.Container size="lg" scroll="inside">
        <Modal.Dialog className="sm:max-w-3xl">
          <Modal.CloseTrigger />
          <Modal.Header className="flex-col items-start gap-1">
            <Modal.Heading>{title}</Modal.Heading>
            {subtitle !== undefined && <div className="text-xs text-muted wrap-anywhere">{subtitle}</div>}
          </Modal.Header>
          <Modal.Body className="flex flex-col gap-3">
            {errors.length > 0 && (
              <div className="rounded-sm bg-danger-soft px-3 py-2 text-danger-soft-foreground">
                <p className="mb-1 text-xs font-medium">{errors.length === 1 ? "1 error" : `${errors.length} errors`}</p>
                <ul className="flex flex-col gap-1 font-mono text-xs wrap-anywhere">
                  {errors.map((error, index) => (
                    <li key={index}>{error}</li>
                  ))}
                </ul>
              </div>
            )}
            <pre ref={scroller} className="max-h-[55vh] overflow-y-auto rounded-sm bg-default/60 px-3 py-2 font-mono text-xs leading-5 wrap-anywhere whitespace-pre-wrap">
              {lines.length === 0 ? (
                <span className="text-muted">{live ? "Waiting for output..." : "No output."}</span>
              ) : (
                lines.map((line, index) => {
                  const tone = classifyLogLine(line);
                  return (
                    <div
                      key={index}
                      className={cn(
                        "-mx-3 px-3",
                        tone === "error" && "bg-danger-soft text-danger-soft-foreground",
                        tone === "warning" && "text-warning",
                      )}
                    >
                      {line === "" ? " " : line}
                    </div>
                  );
                })
              )}
            </pre>
          </Modal.Body>
          <Modal.Footer className="items-center justify-between">
            <span className="text-xs text-muted">
              {lines.length} lines{errorCount > 0 ? `, ${errorCount} with errors` : ""}
            </span>
            <div className="flex items-center gap-2">
              <CopyLogButton text={text} />
              <Button slot="close" variant="secondary">
                Close
              </Button>
            </div>
          </Modal.Footer>
        </Modal.Dialog>
      </Modal.Container>
    </Modal.Backdrop>
  );
}

function CopyLogButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return;
    const timer = setTimeout(() => setCopied(false), 1500);
    return () => clearTimeout(timer);
  }, [copied]);
  return (
    <Button variant="tertiary" onPress={() => void copyText(text).then(() => setCopied(true))}>
      {copied ? <Check className="size-4" /> : <Copy className="size-4" />}
      {copied ? "Copied" : "Copy log"}
    </Button>
  );
}

/* ---------- motion ---------- */

/** Enter-only list item fade, quick; a small stagger for the first screen of rows. */
export function listItemMotion(index: number) {
  return {
    initial: { opacity: 0, y: 4 },
    animate: { opacity: 1, y: 0 },
    transition: { duration: 0.18, delay: Math.min(index, 12) * 0.015 },
  };
}
