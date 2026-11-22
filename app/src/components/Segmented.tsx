import { useId } from "react";
import { motion } from "motion/react";
import type { LucideIcon } from "lucide-react";

export interface SegmentedOption<T extends string> {
  id: T;
  label: string;
  icon?: LucideIcon;
}

interface SegmentedProps<T extends string> {
  options: readonly SegmentedOption<T>[];
  value: T;
  onChange: (value: T) => void;
  "aria-label": string;
  className?: string;
}

/** A segmented control whose selected background slides between options. */
export function Segmented<T extends string>({ options, value, onChange, className, ...rest }: SegmentedProps<T>) {
  const layoutId = useId();

  return (
    <div role="radiogroup" aria-label={rest["aria-label"]} className={`inline-flex gap-0.5 rounded-sm bg-default p-0.5 ${className ?? ""}`}>
      {options.map((option) => {
        const selected = option.id === value;
        const Icon = option.icon;
        return (
          <button
            key={option.id}
            type="button"
            role="radio"
            aria-checked={selected}
            onClick={() => onChange(option.id)}
            className={`relative flex h-7 items-center gap-1.5 rounded-[3px] px-3 text-[13px] transition-colors duration-200 outline-none focus-visible:ring-2 focus-visible:ring-focus ${
              selected ? "text-foreground" : "text-muted hover:text-foreground"
            }`}
          >
            {selected && (
              <motion.span
                layoutId={`segmented-${layoutId}`}
                className="absolute inset-0 rounded-[3px] bg-segment shadow-card"
                transition={{ type: "spring", bounce: 0.15, duration: 0.3 }}
              />
            )}
            {Icon && <Icon className="relative size-3.5" />}
            <span className="relative">{option.label}</span>
          </button>
        );
      })}
    </div>
  );
}
