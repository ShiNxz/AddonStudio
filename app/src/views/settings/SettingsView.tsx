// App settings: CS2 path override, parallel compiles, compiler args, pack size cap (GB), theme.
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Button, Input, NumberField, Skeleton, TextField } from "@heroui/react";
import { FolderOpen, Monitor, Moon, RotateCcw, Sun } from "lucide-react";
import { call, currentTransport } from "../../rpc/client";
import type { AppSettings, Theme } from "../../rpc/types";
import { Segmented } from "../../components/Segmented";
import { InfoBar } from "../../components/InfoBar";
import { notifySuccess } from "../../components/notify";
import { keys, useAppInfo, useDoctor, useSettings } from "../../app/queries";
import { canPickFolders, pickFolder } from "../../app/platform";
import { DEFAULT_COMPILER_ARGS, diffSettings, toDraft, type Draft, type PatchWithCap, type SettingsWithCap } from "./draft";

const THEMES = [
  { id: "dark", label: "Dark", icon: Moon },
  { id: "light", label: "Light", icon: Sun },
  { id: "system", label: "System", icon: Monitor },
] as const;

export function SettingsView() {
  const queryClient = useQueryClient();
  const settings = useSettings();
  const doctor = useDoctor();
  const app = useAppInfo();

  const saved = useMemo(() => (settings.data ? toDraft(settings.data as SettingsWithCap) : null), [settings.data]);
  const [draft, setDraft] = useState<Draft | null>(null);
  useEffect(() => {
    if (saved && draft === null) setDraft(saved);
  }, [saved, draft]);

  const patch = saved && draft ? diffSettings(saved, draft) : {};
  const dirty = Object.keys(patch).length > 0;
  const argsMissingInput = draft !== null && !draft.compilerArgs.includes("{input}");

  const save = useMutation({
    mutationFn: (changes: PatchWithCap) => call("settings.set", changes),
    onSuccess: (next) => {
      queryClient.setQueryData(keys.settings, next);
      setDraft(toDraft(next as SettingsWithCap));
      if ("cs2PathOverride" in patch) void queryClient.invalidateQueries({ queryKey: keys.doctor });
      notifySuccess("Settings saved");
    },
  });

  const theme = useMutation({
    mutationFn: (value: Theme) => call("settings.set", { theme: value }),
    onMutate: (value) => {
      const previous = queryClient.getQueryData<AppSettings>(keys.settings);
      if (previous) queryClient.setQueryData(keys.settings, { ...previous, theme: value });
      return { previous };
    },
    onError: (_error, _value, context) => {
      if (context?.previous) queryClient.setQueryData(keys.settings, context.previous);
    },
    onSuccess: (next) => queryClient.setQueryData(keys.settings, next),
  });

  const update = (changes: Partial<Draft>) => setDraft((current) => (current ? { ...current, ...changes } : current));

  const browse = async () => {
    const picked = await pickFolder("Counter-Strike Global Offensive folder", draft?.cs2PathOverride || doctor.data?.cs2Path);
    if (picked) update({ cs2PathOverride: picked });
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex-1 overflow-y-auto">
        <div className="mx-auto flex max-w-3xl flex-col gap-6 px-8 py-8">
          <div className="flex flex-col gap-1">
            <h1 className="text-title font-semibold">Settings</h1>
            <p className="text-body text-fg-secondary">Saved for this PC, used by every project.</p>
          </div>

          {settings.isPending || !draft ? (
            <SettingsSkeleton />
          ) : (
            <>
              <Group title="Appearance">
                <Row label="Theme" description="Dark is the default. System follows Windows.">
                  <Segmented
                    aria-label="Theme"
                    options={THEMES}
                    value={settings.data?.theme ?? "dark"}
                    onChange={(value) => theme.mutate(value)}
                  />
                </Row>
              </Group>

              <Group title="Counter-Strike 2">
                <Row
                  label="CS2 folder"
                  description={
                    doctor.data?.cs2Path && !draft.cs2PathOverride
                      ? `Found through Steam: ${doctor.data.cs2Path}`
                      : "Leave empty to use the install Steam reports."
                  }
                  wide
                >
                  <div className="flex gap-2">
                    <TextField aria-label="CS2 folder" className="min-w-0 flex-1" value={draft.cs2PathOverride} onChange={(value) => update({ cs2PathOverride: value })}>
                      <Input placeholder={doctor.data?.cs2Path ?? "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Counter-Strike Global Offensive"} className="w-full font-mono text-[13px]" />
                    </TextField>
                    {canPickFolders() && (
                      <Button variant="secondary" onPress={() => void browse()}>
                        <FolderOpen />
                        Browse
                      </Button>
                    )}
                    {draft.cs2PathOverride && (
                      <Button variant="ghost" onPress={() => update({ cs2PathOverride: "" })}>
                        Clear
                      </Button>
                    )}
                  </div>
                </Row>
              </Group>

              <Group title="Compiling">
                <Row label="Parallel compiles" description="How many resourcecompiler.exe processes run at once.">
                  <NumberField
                    aria-label="Parallel compiles"
                    minValue={1}
                    maxValue={64}
                    value={draft.compileParallelism}
                    onChange={(value) => update({ compileParallelism: Number.isFinite(value) ? Math.round(value) : 1 })}
                    className="w-36"
                  >
                    <NumberField.Group>
                      <NumberField.DecrementButton />
                      <NumberField.Input className="w-full text-center" />
                      <NumberField.IncrementButton />
                    </NumberField.Group>
                  </NumberField>
                </Row>
                <Row label="Compiler arguments" description="{input} is replaced with the file to compile." wide>
                  <div className="flex gap-2">
                    <TextField aria-label="Compiler arguments" className="min-w-0 flex-1" value={draft.compilerArgs} onChange={(value) => update({ compilerArgs: value })} isInvalid={argsMissingInput}>
                      <Input className="w-full font-mono text-[13px]" />
                    </TextField>
                    {draft.compilerArgs !== DEFAULT_COMPILER_ARGS && (
                      <Button variant="ghost" onPress={() => update({ compilerArgs: DEFAULT_COMPILER_ARGS })}>
                        <RotateCcw />
                        Default
                      </Button>
                    )}
                  </div>
                  {argsMissingInput && <p className="mt-1.5 text-caption text-danger">Without {"{input}"} the compiler does not know which file to build.</p>}
                </Row>
              </Group>

              <Group title="Publishing">
                <Row label="Pack size cap" description="The biggest upload Addon Studio packs. Valve's own tool stops at 3 GB.">
                  <NumberField
                    aria-label="Pack size cap in GB"
                    minValue={0.5}
                    maxValue={64}
                    step={0.5}
                    value={draft.packCapGb}
                    formatOptions={{ maximumFractionDigits: 1 }}
                    onChange={(value) => update({ packCapGb: Number.isFinite(value) ? value : 3 })}
                    className="w-36"
                  >
                    <NumberField.Group>
                      <NumberField.DecrementButton />
                      <NumberField.Input className="w-full text-center" />
                      <NumberField.IncrementButton />
                    </NumberField.Group>
                  </NumberField>
                  <span className="ml-2 text-body text-muted">GB</span>
                </Row>
                {draft.packCapGb > 3 && (
                  <InfoBar severity="warning" title="Above 3 GB.">
                    Valve's tool caps uploads at 3 GB, bigger uploads are untested.
                  </InfoBar>
                )}
              </Group>

              <Group title="About">
                <Row label="Version" description={currentTransport() === "mock" ? "Demo data in the browser, no core running." : "Addon Studio core sidecar."}>
                  {app.data ? <span className="text-body text-fg-secondary">{app.data.version}</span> : <Skeleton className="h-4 w-16" />}
                </Row>
              </Group>
            </>
          )}
        </div>
      </div>

      {draft && (
        <footer className="flex shrink-0 items-center justify-end gap-2 bg-surface px-8 py-3">
          <span className="mr-auto text-caption text-muted">{dirty ? "Unsaved changes" : "All changes saved"}</span>
          <Button variant="secondary" isDisabled={!dirty || save.isPending} onPress={() => saved && setDraft(saved)}>
            Revert
          </Button>
          <Button variant="primary" isDisabled={!dirty || argsMissingInput} isPending={save.isPending} onPress={() => save.mutate(patch)}>
            Save
          </Button>
        </footer>
      )}
    </div>
  );
}

function Group({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-1">
      <h2 className="pb-1 text-body font-semibold">{title}</h2>
      {children}
    </section>
  );
}

function Row({ label, description, children, wide = false }: { label: string; description?: string; children: ReactNode; wide?: boolean }) {
  return (
    <div className={`flex gap-4 rounded-lg bg-surface px-4 py-3 shadow-card ${wide ? "flex-col" : "items-center justify-between"}`}>
      <div className="flex min-w-0 flex-col">
        <span className="text-body">{label}</span>
        {description && <span className="text-caption break-words text-muted">{description}</span>}
      </div>
      <div className={wide ? "" : "flex shrink-0 items-center"}>{children}</div>
    </div>
  );
}

function SettingsSkeleton() {
  return (
    <div className="flex flex-col gap-6" aria-hidden>
      {[1, 1, 2, 1].map((rows, group) => (
        <div key={group} className="flex flex-col gap-1">
          <Skeleton className="mb-1 h-4 w-28" />
          {Array.from({ length: rows }, (_, index) => (
            <div key={index} className="flex items-center justify-between rounded-lg bg-surface px-4 py-3 shadow-card">
              <div className="flex flex-col gap-1.5">
                <Skeleton className="h-3.5 w-36" />
                <Skeleton className="h-3 w-64" />
              </div>
              <Skeleton className="h-8 w-36" />
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}
