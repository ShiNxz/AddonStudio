// The settings form state and what settings.set gets from it.
import type { AppSettings, AppSettingsPatch } from "../../rpc/types";
import { GIB } from "../../components/format";

export const DEFAULT_COMPILER_ARGS = '-nop4 -i "{input}"';
export const DEFAULT_MAX_PACK_BYTES = 3 * GIB;

/** AppSettings with maxPackBytes read defensively, in case the core is older. */
export type SettingsWithCap = Omit<AppSettings, "maxPackBytes"> & { maxPackBytes?: number };
export type PatchWithCap = AppSettingsPatch;

export interface Draft {
  cs2PathOverride: string;
  compileParallelism: number;
  compilerArgs: string;
  packCapGb: number;
}

/** Bytes to GB for the cap field, rounded to 0.1 GB. */
export function bytesToGb(bytes: number): number {
  return Math.round((bytes / GIB) * 10) / 10;
}

/** GB from the cap field to whole bytes. */
export function gbToBytes(gb: number): number {
  return Math.round(gb * GIB);
}

export function toDraft(settings: SettingsWithCap): Draft {
  return {
    cs2PathOverride: settings.cs2PathOverride ?? "",
    compileParallelism: settings.compileParallelism,
    compilerArgs: settings.compilerArgs,
    packCapGb: bytesToGb(settings.maxPackBytes ?? DEFAULT_MAX_PACK_BYTES),
  };
}

/** Only the fields that changed, as settings.set expects. */
export function diffSettings(saved: Draft, draft: Draft): PatchWithCap {
  const patch: PatchWithCap = {};
  if (draft.cs2PathOverride.trim() !== saved.cs2PathOverride.trim()) patch.cs2PathOverride = draft.cs2PathOverride.trim() || null;
  if (draft.compileParallelism !== saved.compileParallelism) patch.compileParallelism = draft.compileParallelism;
  if (draft.compilerArgs !== saved.compilerArgs) patch.compilerArgs = draft.compilerArgs;
  if (draft.packCapGb !== saved.packCapGb) patch.maxPackBytes = gbToBytes(draft.packCapGb);
  return patch;
}
