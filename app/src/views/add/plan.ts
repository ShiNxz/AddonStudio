import type { ImportAction, ImportItem } from "../../rpc/types";

/** Count and bytes per action, for the summary chips. */
export function summarizePlan(items: readonly ImportItem[]): Record<ImportAction, { count: number; bytes: number }> {
  const summary: Record<ImportAction, { count: number; bytes: number }> = {
    copySource: { count: 0, bytes: 0 },
    decompile: { count: 0, bytes: 0 },
    prebuilt: { count: 0, bytes: 0 },
    skip: { count: 0, bytes: 0 },
  };
  for (const item of items) {
    summary[item.action].count += 1;
    summary[item.action].bytes += item.size;
  }
  return summary;
}
