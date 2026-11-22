// Package: the pack mode (everything, or Valve's whitelist only) with what the whitelist drops and what the default
// mode leaves out; every file that goes into the VPK, with size, origin and change vs the last publish; the size bar
// against the pack cap, a breakdown by type, removed files, and Export (pack.export).
import { useDeferredValue, useMemo, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { Button, SearchField, Skeleton, cn } from "@heroui/react";
import { AnimatePresence, motion } from "motion/react";
import { ArrowDown, ArrowUp, ChevronDown, Download, FolderOpen, Package, Settings2 } from "lucide-react";
import { call, isTauri } from "../../rpc/client";
import type { FileChange, PackContents, PackExportResult, PackFile, PackOrigin, PackSkipReason, PackSkippedFile, PackWhitelistFile } from "../../rpc/types";
import type { ProjectTab, ProjectViewProps } from "./types";
import { filterPackFiles, folderSummary, formatBytes, formatDateTime, plural, relativeTime, workshopUrl, type PackSortKey } from "./logic";
import { CopyBlock, ErrorNotice, FilePath, LinkButton, ListSkeleton, Notice, Panel, Segmented, ViewPage, openFolder } from "./ui";

const VALVE_CAP = 3 * 1024 ** 3;
const PAGE = 300;

/** Fills for the type breakdown, in order of size. */
const TYPE_COLORS = ["bg-accent", "bg-success", "bg-warning", "bg-danger", "bg-accent/55", "bg-success/55", "bg-warning/55", "bg-default-foreground/40"];

export function PackageView({ project, goTo }: ProjectViewProps) {
  const contents = useQuery({ queryKey: ["pack.contents", project.path], queryFn: () => call("pack.contents", { path: project.path }) });

  if (contents.isPending) {
    return (
      <ViewPage>
        <div className="grid gap-4 lg:grid-cols-2" aria-hidden>
          <Skeleton className="h-36 rounded-lg" />
          <Skeleton className="h-36 rounded-lg" />
        </div>
        <div className="rounded-lg bg-surface p-2">
          <ListSkeleton rows={8} leading={false} />
        </div>
      </ViewPage>
    );
  }

  if (contents.isError) {
    return (
      <ViewPage>
        <ErrorNotice title="Could not read the package" error={contents.error} onRetry={() => void contents.refetch()} />
      </ViewPage>
    );
  }

  return <PackageBody path={project.path} data={contents.data} refreshing={contents.isFetching} onRefresh={() => void contents.refetch()} goTo={goTo} />;
}

function PackageBody({
  path,
  data,
  refreshing,
  onRefresh,
  goTo,
}: {
  path: string;
  data: PackContents;
  refreshing: boolean;
  onRefresh: () => void;
  goTo: (tab: ProjectTab) => void;
}) {
  return (
    <ViewPage>
      <ModeCard data={data} onSettings={() => goTo("settings")} />
      <div className="grid gap-4 lg:grid-cols-2">
        <SizeCard data={data} />
        <TypeCard data={data} />
      </div>
      <ExportCard path={path} data={data} />
      <FileTable data={data} refreshing={refreshing} onRefresh={onRefresh} />
      {data.removed.length > 0 && (
        <Panel title={`Removed since the last publish (${data.removed.length})`} description="These were on the Workshop and are gone from the build now.">
          <ul className="flex flex-col">
            {data.removed.map((file) => (
              <li key={file} className="flex items-center gap-3 rounded-sm px-2 py-1 text-[13px]">
                <span className="w-3 shrink-0 text-center font-mono text-xs font-semibold text-danger">D</span>
                <FilePath path={file} className="text-muted line-through decoration-muted" />
              </li>
            ))}
          </ul>
        </Panel>
      )}
    </ViewPage>
  );
}

/* ---------- pack mode ---------- */

const SKIP_LABEL: Record<PackSkipReason, string> = {
  junk: "junk",
  sourceOnly: "source file",
  excluded: "exclude rule",
  blocked: "blocked type",
};

const SKIP_PLURAL: Record<PackSkipReason, string> = {
  junk: "junk",
  sourceOnly: "source files",
  excluded: "exclude rules",
  blocked: "blocked types",
};

const SKIP_TITLE: Record<PackSkipReason, string> = {
  junk: "Temporary or OS file (Thumbs.db, .tmp, .log...)",
  sourceOnly: "The compiler reads it, CS2 loads the compiled file",
  excluded: "A pack exclude rule in Settings",
  blocked: "Valve's tool never packs executables and archives",
};

function ModeCard({ data, onSettings }: { data: PackContents; onSettings: () => void }) {
  const dropped = data.whitelistDropped;
  const droppedSize = dropped.reduce((sum, file) => sum + file.size, 0);
  const whitelist = data.valveWhitelist;

  return (
    <Panel
      title={whitelist ? "Packing: Valve whitelist only" : "Packing: everything"}
      description={
        whitelist
          ? "Only the folders in CS2's gameinfo.gi (AddonConfig > VpkDirectories) go in, like Valve's workshop manager."
          : "Every file in the build folder goes in: compiled files and prebuilt/. Junk and source files stay out."
      }
      actions={
        <Button size="sm" variant="tertiary" onPress={onSettings}>
          <Settings2 className="size-3.5" />
          Change in Settings
        </Button>
      }
    >
      <div className="flex flex-col gap-3">
        {dropped.length === 0 ? (
          <p className="text-xs text-muted">Nothing in the build folder is outside Valve's whitelist.</p>
        ) : whitelist ? (
          <Notice tone="warning" title={`Valve's whitelist leaves out ${plural(dropped.length, "file")} (${formatBytes(droppedSize)})`}>
            They are in the build folder but not in the VPK. Turn off "Valve whitelist only" in Settings, or add include rules for them.
          </Notice>
        ) : (
          <Notice tone="info" title={`${plural(dropped.length, "file")} (${formatBytes(droppedSize)}) outside Valve's whitelist, packed anyway`}>
            Valve's workshop manager would have left these out.
          </Notice>
        )}
        {dropped.length > 0 && <DroppedList files={dropped} />}
        {!whitelist && data.skipped.length > 0 && <SkippedList files={data.skipped} />}
      </div>
    </Panel>
  );
}

function Disclosure({ label, children }: { label: ReactNode; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div>
      <button
        type="button"
        onClick={() => setOpen((value) => !value)}
        aria-expanded={open}
        className="flex cursor-pointer items-center gap-1.5 rounded-sm text-xs text-muted transition-colors duration-200 hover:text-foreground"
      >
        <ChevronDown className={cn("size-3.5 transition-transform duration-200", !open && "-rotate-90")} />
        {label}
      </button>
      <AnimatePresence initial={false}>
        {open && (
          <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }} transition={{ duration: 0.18 }} className="overflow-hidden">
            <div className="pt-2">{children}</div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

function DroppedList({ files }: { files: PackWhitelistFile[] }) {
  const [limit, setLimit] = useState(PAGE);
  const folders = useMemo(() => folderSummary(files), [files]);
  return (
    <div className="flex flex-col gap-2">
      <ul className="flex flex-wrap gap-1.5" aria-label="Folders outside the whitelist">
        {folders.slice(0, 8).map((group) => (
          <li key={group.folder} className="rounded-sm bg-default/60 px-2 py-0.5 font-mono text-xs">
            {group.folder || "top level"} <span className="text-muted tabular-nums">{group.count}</span>
          </li>
        ))}
        {folders.length > 8 && <li className="px-1 py-0.5 text-xs text-muted">+{folders.length - 8} more folders</li>}
      </ul>
      <Disclosure label={`Files (${files.length.toLocaleString("en-US")})`}>
        <ul className="flex flex-col">
          {files.slice(0, limit).map((file) => (
            <li key={file.path} className="flex items-center gap-3 rounded-sm px-2 py-1 text-[13px]">
              <FilePath path={file.path} className="flex-1" />
              <span className="w-20 shrink-0 text-right text-xs tabular-nums">{formatBytes(file.size)}</span>
            </li>
          ))}
        </ul>
        {files.length > limit && (
          <Button size="sm" variant="tertiary" className="mt-1" onPress={() => setLimit((value) => value + PAGE * 2)}>
            Show {Math.min(PAGE * 2, files.length - limit)} more
          </Button>
        )}
      </Disclosure>
    </div>
  );
}

function SkippedList({ files }: { files: PackSkippedFile[] }) {
  const [limit, setLimit] = useState(PAGE);
  const size = files.reduce((sum, file) => sum + file.size, 0);
  const reasons = [...new Set(files.map((file) => file.reason))].map((reason) => SKIP_PLURAL[reason]).join(", ");
  return (
    <Disclosure label={`${plural(files.length, "file")} left out (${formatBytes(size)}): ${reasons}`}>
      <p className="px-2 pb-1 text-xs text-muted">An include rule in Settings packs a junk or source file anyway.</p>
      <ul className="flex flex-col">
        {files.slice(0, limit).map((file) => (
          <li key={file.path} className="flex items-center gap-3 rounded-sm px-2 py-1 text-[13px]">
            <FilePath path={file.path} className="flex-1 text-muted" />
            <span className="hidden w-24 shrink-0 text-xs text-muted sm:block" title={SKIP_TITLE[file.reason]}>
              {SKIP_LABEL[file.reason]}
            </span>
            <span className="w-20 shrink-0 text-right text-xs tabular-nums">{formatBytes(file.size)}</span>
          </li>
        ))}
      </ul>
      {files.length > limit && (
        <Button size="sm" variant="tertiary" className="mt-1" onPress={() => setLimit((value) => value + PAGE * 2)}>
          Show {Math.min(PAGE * 2, files.length - limit)} more
        </Button>
      )}
    </Disclosure>
  );
}

/* ---------- size ---------- */

function SizeCard({ data }: { data: PackContents }) {
  const ratio = data.limitBytes > 0 ? data.totalSize / data.limitBytes : 0;
  const tone = ratio > 1 ? "danger" : ratio >= 0.85 ? "warning" : "accent";

  return (
    <Panel title="Size" description={`${plural(data.fileCount, "file")} in the VPK`}>
      <div className="flex items-baseline gap-2">
        <span className="text-[28px] leading-9 font-semibold tabular-nums">{formatBytes(data.totalSize)}</span>
        <span className="text-sm text-muted">of {formatBytes(data.limitBytes)}</span>
        <span className={cn("ml-auto text-sm tabular-nums", tone === "danger" ? "text-danger" : tone === "warning" ? "text-warning" : "text-muted")}>
          {Math.round(ratio * 100)}%
        </span>
      </div>
      <div className="mt-2 h-2 overflow-hidden rounded-full bg-default" role="meter" aria-valuemin={0} aria-valuemax={data.limitBytes} aria-valuenow={data.totalSize} aria-label="Pack size">
        <motion.div
          className={cn("h-full rounded-full", tone === "danger" ? "bg-danger" : tone === "warning" ? "bg-warning" : "bg-accent")}
          initial={{ width: 0 }}
          animate={{ width: `${Math.min(100, ratio * 100)}%` }}
          transition={{ duration: 0.4, ease: "easeOut" }}
        />
      </div>
      <div className="mt-3 flex flex-col gap-2">
        {ratio > 1 && (
          <Notice tone="danger" title="Too big to publish">
            Lower the size, or raise the cap in app settings.
          </Notice>
        )}
        {ratio <= 1 && ratio >= 0.85 && <Notice tone="warning" title="Close to the size cap" />}
        {data.limitBytes > VALVE_CAP && (
          <Notice tone="warning" title="The cap is above 3 GB">
            Valve's tool caps uploads at 3 GB, bigger uploads are untested.
          </Notice>
        )}
        <LastPublishLine data={data} />
      </div>
    </Panel>
  );
}

function LastPublishLine({ data }: { data: PackContents }) {
  if (!data.lastPublish) return <p className="text-xs text-muted">Not published yet, so every file is new.</p>;
  const { added, changed, removed } = data.changes;
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
      <span title={formatDateTime(data.lastPublish.at)}>Last publish {relativeTime(data.lastPublish.at)}</span>
      {data.lastPublish.commit && <span className="font-mono">{data.lastPublish.commit.slice(0, 7)}</span>}
      <span className="text-success tabular-nums">+{added}</span>
      <span className="text-warning tabular-nums">~{changed}</span>
      <span className="text-danger tabular-nums">-{removed}</span>
      <LinkButton href={workshopUrl(data.lastPublish.workshopId)} className="ml-auto h-6">
        Workshop
      </LinkButton>
    </div>
  );
}

/* ---------- types ---------- */

function TypeCard({ data }: { data: PackContents }) {
  const types = [...data.byType].sort((a, b) => b.size - a.size);
  const shown = types.slice(0, TYPE_COLORS.length - 1);
  const rest = types.slice(TYPE_COLORS.length - 1);
  if (rest.length > 0) {
    shown.push({ label: "Other", count: rest.reduce((sum, type) => sum + type.count, 0), size: rest.reduce((sum, type) => sum + type.size, 0) });
  }
  const total = Math.max(1, data.totalSize);

  return (
    <Panel title="By type">
      {shown.length === 0 ? (
        <p className="text-xs text-muted">The package is empty. Build first.</p>
      ) : (
        <>
          <div className="flex h-2 gap-0.5 overflow-hidden rounded-full bg-default" aria-hidden>
            {shown.map((type, index) => (
              <motion.div
                key={type.label}
                className={cn("h-full", TYPE_COLORS[index])}
                initial={{ width: 0 }}
                animate={{ width: `${(type.size / total) * 100}%` }}
                transition={{ duration: 0.4, ease: "easeOut" }}
              />
            ))}
          </div>
          <ul className="mt-3 grid gap-x-6 gap-y-1.5 sm:grid-cols-2">
            {shown.map((type, index) => (
              <li key={type.label} className="flex items-center gap-2 text-[13px]">
                <span className={cn("size-2.5 shrink-0 rounded-sm", TYPE_COLORS[index])} />
                <span className="min-w-0 flex-1 truncate">{type.label}</span>
                <span className="text-xs text-muted tabular-nums">{type.count}</span>
                <span className="w-16 text-right text-xs tabular-nums">{formatBytes(type.size)}</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </Panel>
  );
}

/* ---------- export ---------- */

function ExportCard({ path, data }: { path: string; data: PackContents }) {
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [result, setResult] = useState<PackExportResult | null>(null);

  const exportPack = async () => {
    setPending(true);
    setError(null);
    try {
      setResult(await call("pack.export", { path, outDir: null }));
    } catch (failure) {
      setError(failure);
    } finally {
      setPending(false);
    }
  };

  return (
    <Panel
      title="Export"
      description="Writes the VPK chunks and publish_data.txt to a folder, the same files the Workshop gets."
      actions={
        <Button variant="secondary" size="sm" isPending={pending} isDisabled={data.fileCount === 0} onPress={() => void exportPack()}>
          <Download className="size-3.5" />
          Export VPK
        </Button>
      }
    >
      {error !== null && <ErrorNotice title="Export failed" error={error} />}
      {result && (
        <motion.div initial={{ opacity: 0, y: 4 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.18 }} className="flex flex-col gap-2">
          <div className="flex items-center gap-2">
            <CopyBlock text={result.dir} className="flex-1" />
            {isTauri() && (
              <Button size="sm" variant="tertiary" onPress={() => void openFolder(result.dir)}>
                <FolderOpen className="size-3.5" />
                Open
              </Button>
            )}
          </div>
          <p className="text-xs text-muted">
            {plural(result.files.length, "file")}, {formatBytes(result.totalSize)}
          </p>
          <ul className="flex flex-col gap-0.5 font-mono text-xs text-muted">
            {result.files.map((file) => (
              <li key={file} className="wrap-anywhere">
                {file.split(/[\\/]/).pop()}
              </li>
            ))}
          </ul>
        </motion.div>
      )}
    </Panel>
  );
}

/* ---------- files ---------- */

const CHANGE_STYLE: Record<FileChange, { letter: string; className: string; title: string }> = {
  added: { letter: "A", className: "text-success", title: "New since the last publish" },
  changed: { letter: "M", className: "text-warning", title: "Changed since the last publish" },
  same: { letter: "", className: "text-muted", title: "Same as the last publish" },
};

const ORIGIN_LABEL: Record<PackOrigin, string> = { compiled: "compiled", prebuilt: "prebuilt", other: "other" };

function FileTable({ data, refreshing, onRefresh }: { data: PackContents; refreshing: boolean; onRefresh: () => void }) {
  const [search, setSearch] = useState("");
  const [origin, setOrigin] = useState<PackOrigin | "all">("all");
  const [change, setChange] = useState<FileChange | "all">("all");
  const [sort, setSort] = useState<PackSortKey>("size");
  const [descending, setDescending] = useState(true);
  const [limit, setLimit] = useState(PAGE);
  const deferredSearch = useDeferredValue(search);

  const files = useMemo(
    () => filterPackFiles(data.files, { search: deferredSearch, origin, change, sort, descending }),
    [data.files, deferredSearch, origin, change, sort, descending],
  );
  const shownSize = files.reduce((sum, file) => sum + file.size, 0);
  const originCounts = useMemo(() => countBy(data.files, (file) => file.origin), [data.files]);
  const changeCounts = useMemo(() => countBy(data.files, (file) => file.change ?? "same"), [data.files]);

  const setSortKey = (key: PackSortKey) => {
    if (sort === key) setDescending((value) => !value);
    else {
      setSort(key);
      setDescending(key === "size");
    }
  };

  return (
    <section className="rounded-lg bg-surface p-2">
      <div className="flex flex-wrap items-center gap-2 p-2">
        <SearchField value={search} onChange={(value) => { setSearch(value); setLimit(PAGE); }} aria-label="Search files" className="min-w-48 flex-1">
          <SearchField.Group>
            <SearchField.SearchIcon />
            <SearchField.Input placeholder="Search paths" />
            <SearchField.ClearButton />
          </SearchField.Group>
        </SearchField>
        <Segmented
          label="Origin"
          value={origin}
          onChange={(value) => {
            setOrigin(value);
            setLimit(PAGE);
          }}
          options={[
            { id: "all", label: "All" },
            { id: "compiled", label: "Compiled", count: originCounts.get("compiled") ?? 0 },
            { id: "prebuilt", label: "Prebuilt", count: originCounts.get("prebuilt") ?? 0 },
            { id: "other", label: "Other", count: originCounts.get("other") ?? 0 },
          ]}
        />
        {data.lastPublish && (
          <Segmented
            label="Change"
            value={change}
            onChange={(value) => {
              setChange(value);
              setLimit(PAGE);
            }}
            options={[
              { id: "all", label: "All" },
              { id: "added", label: "New", count: changeCounts.get("added") ?? 0 },
              { id: "changed", label: "Changed", count: changeCounts.get("changed") ?? 0 },
              { id: "same", label: "Same", count: changeCounts.get("same") ?? 0 },
            ]}
          />
        )}
      </div>

      <div className="flex items-center gap-3 px-4 pt-1 pb-1.5 text-xs text-muted">
        <span className="w-3 shrink-0" />
        <SortHeader label="Path" active={sort === "path"} descending={descending} onPress={() => setSortKey("path")} className="flex-1" />
        <span className="hidden w-20 shrink-0 sm:block">Origin</span>
        <SortHeader label="Size" active={sort === "size"} descending={descending} onPress={() => setSortKey("size")} className="w-20 justify-end" />
      </div>

      {files.length === 0 ? (
        <div className="flex flex-col items-center gap-2 py-10 text-center">
          <Package className="size-6 text-muted" />
          <p className="text-sm">{data.files.length === 0 ? "Nothing to pack yet" : "No files match"}</p>
          <p className="text-xs text-muted">{data.files.length === 0 ? "Build the project first." : "Try another search or filter."}</p>
        </div>
      ) : (
        <ul className={cn("flex flex-col transition-opacity duration-200", refreshing && "opacity-70")}>
          {files.slice(0, limit).map((file) => (
            <FileRow key={file.path} file={file} />
          ))}
        </ul>
      )}

      <div className="flex flex-wrap items-center gap-3 px-3 pt-2 pb-1 text-xs text-muted">
        <span>
          {files.length === data.files.length ? plural(files.length, "file") : `${files.length} of ${data.files.length} files`}, {formatBytes(shownSize)}
        </span>
        {files.length > limit && (
          <Button size="sm" variant="tertiary" onPress={() => setLimit((value) => value + PAGE * 2)}>
            Show {Math.min(PAGE * 2, files.length - limit)} more
          </Button>
        )}
        <Button size="sm" variant="ghost" className="ml-auto h-7" onPress={onRefresh} isPending={refreshing}>
          Refresh
        </Button>
      </div>
    </section>
  );
}

function SortHeader({ label, active, descending, onPress, className }: { label: string; active: boolean; descending: boolean; onPress: () => void; className?: string }) {
  const Icon = descending ? ArrowDown : ArrowUp;
  return (
    <button
      type="button"
      onClick={onPress}
      className={cn("flex cursor-pointer items-center gap-1 transition-colors duration-200 hover:text-foreground", active && "text-foreground", className)}
      aria-label={`Sort by ${label.toLowerCase()}`}
    >
      {label}
      {active && <Icon className="size-3" />}
    </button>
  );
}

function FileRow({ file }: { file: PackFile }) {
  const style = file.change ? CHANGE_STYLE[file.change] : null;
  return (
    <li className="flex items-center gap-3 rounded-sm px-2 py-1 text-[13px] transition-colors duration-200 hover:bg-default/60">
      <span className={cn("w-3 shrink-0 text-center font-mono text-xs font-semibold", style?.className)} title={style?.title}>
        {style?.letter}
      </span>
      <FilePath path={file.path} className="flex-1" />
      <span className="hidden w-20 shrink-0 text-xs text-muted sm:block">{ORIGIN_LABEL[file.origin]}</span>
      <span className="w-20 shrink-0 text-right text-xs tabular-nums">{formatBytes(file.size)}</span>
    </li>
  );
}

function countBy<T, K>(items: T[], key: (item: T) => K): Map<K, number> {
  const map = new Map<K, number>();
  for (const item of items) map.set(key(item), (map.get(key(item)) ?? 0) + 1);
  return map;
}
