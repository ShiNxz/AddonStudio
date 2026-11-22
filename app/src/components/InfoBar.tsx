import type { ReactNode } from "react";
import { Button } from "@heroui/react";
import { CircleAlert, CircleCheck, CircleX, Info, X } from "lucide-react";

export type InfoBarSeverity = "info" | "success" | "warning" | "error";

const STYLE: Record<InfoBarSeverity, { fill: string; icon: string; Icon: typeof Info }> = {
  info: { fill: "bg-accent-soft", icon: "text-accent", Icon: Info },
  success: { fill: "bg-success-soft", icon: "text-success", Icon: CircleCheck },
  warning: { fill: "bg-warning-soft", icon: "text-warning", Icon: CircleAlert },
  error: { fill: "bg-danger-soft", icon: "text-danger", Icon: CircleX },
};

interface InfoBarProps {
  severity?: InfoBarSeverity;
  title: ReactNode;
  children?: ReactNode;
  /** Buttons on the right. */
  actions?: ReactNode;
  onClose?: () => void;
  className?: string;
}

/** A Windows 11 style InfoBar: a tinted fill, an icon, a bold title and a message. No outline. */
export function InfoBar({ severity = "info", title, children, actions, onClose, className }: InfoBarProps) {
  const style = STYLE[severity];
  const Icon = style.Icon;
  return (
    <div role={severity === "error" || severity === "warning" ? "alert" : "status"} className={`flex items-start gap-3 rounded-sm px-3.5 py-2.5 ${style.fill} ${className ?? ""}`}>
      <Icon className={`mt-0.5 size-4 shrink-0 ${style.icon}`} />
      <div className="min-w-0 flex-1 text-body">
        <span className="mr-2 font-semibold">{title}</span>
        {children && <div className="inline text-fg-secondary">{children}</div>}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
      {onClose && (
        <Button isIconOnly size="sm" variant="ghost" aria-label="Close" onPress={onClose} className="-my-0.5 shrink-0">
          <X />
        </Button>
      )}
    </div>
  );
}
