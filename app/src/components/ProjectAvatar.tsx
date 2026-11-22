import type { CSSProperties } from "react";

/** A stable hue (0-359) for a name, so each project keeps its color. */
export function hueOf(name: string): number {
  let hash = 0;
  for (let i = 0; i < name.length; i += 1) hash = (hash * 31 + name.charCodeAt(i)) | 0;
  return Math.abs(hash) % 360;
}

interface ProjectAvatarProps {
  title: string;
  /** Seeds the color; the addon name keeps it stable when the title changes. */
  seed: string;
  size?: "sm" | "md" | "lg";
  className?: string;
}

const SIZE = {
  sm: "size-7 text-[13px] rounded-[4px]",
  md: "size-9 text-[15px] rounded-[6px]",
  lg: "size-11 text-[18px] rounded-lg",
} as const;

/** The first letter of the title on a tinted tile. */
export function ProjectAvatar({ title, seed, size = "sm", className }: ProjectAvatarProps) {
  const letter = (title.trim()[0] ?? seed.trim()[0] ?? "?").toUpperCase();
  return (
    <span
      aria-hidden
      style={{ "--hue": hueOf(seed || title) } as CSSProperties}
      className={`avatar-tile flex shrink-0 items-center justify-center font-semibold ${SIZE[size]} ${className ?? ""}`}
    >
      {letter}
    </span>
  );
}
