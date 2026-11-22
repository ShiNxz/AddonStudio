import type { ReactNode } from "react";

interface PanelProps {
  title?: ReactNode;
  description?: ReactNode;
  /** Right side of the header (buttons, chips). */
  actions?: ReactNode;
  children?: ReactNode;
  className?: string;
  /** Padding of the body; "none" for edge-to-edge lists. */
  padding?: "normal" | "none";
}

/** A card: fill and a soft shadow, never an outline. 8px corners like Windows 11 cards. */
export function Panel({ title, description, actions, children, className, padding = "normal" }: PanelProps) {
  const hasHeader = title !== undefined || actions !== undefined;
  return (
    <section className={`rounded-lg bg-surface shadow-card ${className ?? ""}`}>
      {hasHeader && (
        <header className="flex items-start justify-between gap-3 px-4 pt-3.5 pb-2">
          <div className="min-w-0">
            {title && <h3 className="text-body font-semibold">{title}</h3>}
            {description && <p className="text-caption text-muted">{description}</p>}
          </div>
          {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
        </header>
      )}
      <div className={padding === "none" ? "" : hasHeader ? "px-4 pb-4" : "p-4"}>{children}</div>
    </section>
  );
}
