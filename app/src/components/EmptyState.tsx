import type { ReactNode } from "react";
import type { LucideIcon } from "lucide-react";

interface EmptyStateProps {
  icon: LucideIcon;
  title: string;
  children?: ReactNode;
  actions?: ReactNode;
  className?: string;
}

/** A centered icon, title, short text and actions for empty or missing content. */
export function EmptyState({ icon: Icon, title, children, actions, className }: EmptyStateProps) {
  return (
    <div className={`flex flex-col items-center justify-center gap-3 px-6 py-12 text-center ${className ?? ""}`}>
      <span className="flex size-12 items-center justify-center rounded-lg bg-default text-fg-secondary">
        <Icon className="size-6" strokeWidth={1.6} />
      </span>
      <div className="flex max-w-md flex-col gap-1">
        <h2 className="text-body-lg font-semibold">{title}</h2>
        {children && <div className="text-body text-muted">{children}</div>}
      </div>
      {actions && <div className="mt-1 flex flex-wrap items-center justify-center gap-2">{actions}</div>}
    </div>
  );
}
