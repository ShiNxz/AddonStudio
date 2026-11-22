import { CopyButton } from "./CopyButton";

interface CodeTextProps {
  /** The command or path. It wraps, never scrolls sideways. */
  value: string;
  /** Tooltip of the copy button. */
  copyLabel?: string;
  className?: string;
}

/** A command or path on a quiet fill with a copy button. */
export function CodeText({ value, copyLabel = "Copy", className }: CodeTextProps) {
  return (
    <div className={`flex items-start gap-1 rounded-sm bg-surface-secondary py-1 pr-1 pl-2.5 ${className ?? ""}`}>
      <code className="min-w-0 flex-1 py-[3px] text-[13px] leading-5 text-fg-secondary">{value}</code>
      <CopyButton value={value} label={copyLabel} />
    </div>
  );
}
