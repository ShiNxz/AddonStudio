// Settings: the workshop.json form with inline validation, the core's configErrors, and Save
// (projects.saveConfig, then refreshProject). Unknown keys in workshop.json ride along untouched.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { Button, Description, FieldError, Input, Label, ListBox, Select, Switch, TextArea, TextField, cn } from "@heroui/react";
import { AnimatePresence, motion } from "motion/react";
import { Check, Plus, RotateCcw, Save, X } from "lucide-react";
import { call } from "../../rpc/client";
import type { Visibility, WorkshopConfig } from "../../rpc/types";
import type { ProjectViewProps } from "./types";
import { autoPublishVersion, validateConfig, type ConfigErrors } from "./logic";
import { ErrorNotice, Notice, Panel, ViewPage } from "./ui";

const VISIBILITIES: { id: Visibility; label: string; description: string }[] = [
  { id: "public", label: "Public", description: "Anyone can find and subscribe" },
  { id: "unlisted", label: "Unlisted", description: "Only people with the link" },
  { id: "friendsOnly", label: "Friends only", description: "Your Steam friends" },
  { id: "private", label: "Private", description: "Only you" },
];

const stable = (config: WorkshopConfig) => JSON.stringify(config);

export function ProjectSettingsView({ project, refreshProject }: ProjectViewProps) {
  const [base, setBase] = useState<WorkshopConfig>(() => structuredClone(project.config));
  const [draft, setDraft] = useState<WorkshopConfig>(() => structuredClone(project.config));
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const [testSubject, setTestSubject] = useState("v1.5.0 New scoreboard");

  const dirty = stable(draft) !== stable(base);
  const projectConfig = stable(project.config);
  const latest = useRef({ draft, base });
  latest.current = { draft, base };

  // the project changed underneath (saved here, or a publish wrote the workshop id): take it unless there are edits
  useEffect(() => {
    const next = JSON.parse(projectConfig) as WorkshopConfig;
    const clean = stable(latest.current.draft) === stable(latest.current.base);
    setBase(next);
    if (clean) setDraft(structuredClone(next));
  }, [projectConfig]);

  useEffect(() => {
    if (!saved) return;
    const timer = setTimeout(() => setSaved(false), 2000);
    return () => clearTimeout(timer);
  }, [saved]);

  const errors: ConfigErrors = useMemo(() => validateConfig(draft), [draft]);
  const invalid = Object.keys(errors).length > 0;
  const set = <K extends keyof WorkshopConfig>(key: K, value: WorkshopConfig[K]) => setDraft((current) => ({ ...current, [key]: value }));
  const setAuto = <K extends keyof WorkshopConfig["autoPublish"]>(key: K, value: WorkshopConfig["autoPublish"][K]) =>
    setDraft((current) => ({ ...current, autoPublish: { ...current.autoPublish, [key]: value } }));
  const setPack = (key: "include" | "exclude", value: string[]) => setDraft((current) => ({ ...current, pack: { ...current.pack, [key]: value } }));

  const save = async () => {
    if (invalid || !dirty) return;
    setSaving(true);
    setSaveError(null);
    try {
      const info = await call("projects.saveConfig", { path: project.path, config: draft });
      setBase(structuredClone(info.config));
      setDraft(structuredClone(info.config));
      await refreshProject();
      setSaved(true);
    } catch (error) {
      setSaveError(error);
    } finally {
      setSaving(false);
    }
  };

  const testVersion = autoPublishVersion(draft.autoPublish.pattern, testSubject);

  return (
    <ViewPage className="pb-24">
      {project.configErrors.length > 0 && (
        <Notice tone="danger" title="workshop.json has problems">
          <ul className="flex list-disc flex-col gap-0.5 pl-4">
            {project.configErrors.map((error) => (
              <li key={error}>{error}</li>
            ))}
          </ul>
        </Notice>
      )}

      <Panel title="Workshop item" description="What Steam shows on the item page.">
        <div className="grid gap-4 md:grid-cols-2">
          <TextField value={draft.title} onChange={(value) => set("title", value)} isInvalid={errors.title !== undefined} validationBehavior="aria" className="md:col-span-2">
            <Label>Title</Label>
            <Input />
            <FieldError>{errors.title}</FieldError>
          </TextField>

          <div className="flex flex-col gap-2 md:col-span-2">
            <Switch isSelected={draft.description === null} onChange={(keep) => set("description", keep ? null : "")}>
              <Switch.Content className="font-normal">
                <Switch.Control>
                  <Switch.Thumb />
                </Switch.Control>
                Keep the description that is on Steam
              </Switch.Content>
            </Switch>
            <AnimatePresence initial={false}>
              {draft.description !== null && (
                <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }} transition={{ duration: 0.18 }} className="overflow-hidden">
                  <TextField
                    value={draft.description}
                    onChange={(value) => set("description", value)}
                    isInvalid={errors.description !== undefined}
                    validationBehavior="aria"
                  >
                    <Label>Description</Label>
                    <TextArea rows={5} placeholder="Steam BBCode works here" />
                    {errors.description ? <FieldError>{errors.description}</FieldError> : <Description>{draft.description.length}/8000. Every publish sends it.</Description>}
                  </TextField>
                </motion.div>
              )}
            </AnimatePresence>
          </div>

          <Select value={draft.visibility} onChange={(value) => value !== null && set("visibility", value as Visibility)}>
            <Label>Visibility</Label>
            <Select.Trigger>
              <Select.Value />
              <Select.Indicator />
            </Select.Trigger>
            <Select.Popover>
              <ListBox>
                {VISIBILITIES.map((option) => (
                  <ListBox.Item key={option.id} id={option.id} textValue={option.label}>
                    <div className="flex flex-col">
                      <Label>{option.label}</Label>
                      <Description>{option.description}</Description>
                    </div>
                    <ListBox.ItemIndicator />
                  </ListBox.Item>
                ))}
              </ListBox>
            </Select.Popover>
          </Select>

          <TextField
            value={draft.workshopId ?? ""}
            onChange={(value) => set("workshopId", value.trim() === "" ? null : value.trim())}
            isInvalid={errors.workshopId !== undefined}
            validationBehavior="aria"
          >
            <Label>Workshop id</Label>
            <Input placeholder="Set by the first publish" inputMode="numeric" />
            {errors.workshopId ? <FieldError>{errors.workshopId}</FieldError> : <Description>Empty creates a new item on the next publish.</Description>}
          </TextField>

          <TextField
            value={draft.preview ?? ""}
            onChange={(value) => set("preview", value.trim() === "" ? null : value)}
            isInvalid={errors.preview !== undefined}
            validationBehavior="aria"
          >
            <Label>Preview image</Label>
            <Input placeholder="preview.png" />
            {errors.preview ? <FieldError>{errors.preview}</FieldError> : <Description>Path in the repo. Steam wants it under 1 MB.</Description>}
          </TextField>

          <Field label="Tags" error={errors.tags} description="Press Enter to add.">
            <ChipEditor values={draft.tags} onChange={(tags) => set("tags", tags)} placeholder="Custom" ariaLabel="Add a tag" />
          </Field>
        </div>
      </Panel>

      <Panel title="Addon" description="Where CS2 sees this project.">
        <TextField value={draft.addon} onChange={(value) => set("addon", value)} isInvalid={errors.addon !== undefined} validationBehavior="aria" className="max-w-md">
          <Label>Addon folder</Label>
          <Input className="font-mono" />
          {errors.addon ? (
            <FieldError>{errors.addon}</FieldError>
          ) : (
            <Description>The folder under csgo_addons. Changing it needs a new link to CS2.</Description>
          )}
        </TextField>
      </Panel>

      <Panel title="Packing" description="Globs relative to the build folder. Your rules go before Valve's own.">
        <div className="grid gap-4 md:grid-cols-2">
          <Field label="Include" error={errors.include} description="Files Valve's rules would skip, packed anyway.">
            <ListEditor values={draft.pack.include} onChange={(values) => setPack("include", values)} placeholder="maps/**/*.vpk" ariaLabel="Add an include rule" />
          </Field>
          <Field label="Exclude" error={errors.exclude} description="Never packed.">
            <ListEditor values={draft.pack.exclude} onChange={(values) => setPack("exclude", values)} placeholder="**/*.psd" ariaLabel="Add an exclude rule" />
          </Field>
        </div>
      </Panel>

      <Panel title="Automation">
        <div className="flex flex-col gap-4">
          <SwitchRow label="Auto-build" description="Build changed sources when files change, while the Build tab is open." isSelected={draft.autoBuild} onChange={(value) => set("autoBuild", value)} />
          <SwitchRow
            label="Auto-publish on version commits"
            description="A commit whose summary matches the pattern publishes that version."
            isSelected={draft.autoPublish.enabled}
            onChange={(value) => setAuto("enabled", value)}
          />
          <AnimatePresence initial={false}>
            {draft.autoPublish.enabled && (
              <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }} transition={{ duration: 0.18 }} className="overflow-hidden">
                <div className="grid gap-4 pl-12 md:grid-cols-2">
                  <TextField value={draft.autoPublish.pattern} onChange={(value) => setAuto("pattern", value)} isInvalid={errors.pattern !== undefined} validationBehavior="aria">
                    <Label>Pattern</Label>
                    <Input className="font-mono" />
                    {errors.pattern ? <FieldError>{errors.pattern}</FieldError> : <Description>Regex on the commit summary, group 1 is the version.</Description>}
                  </TextField>
                  <TextField value={testSubject} onChange={setTestSubject}>
                    <Label>Try a summary</Label>
                    <Input />
                    <Description>
                      {errors.pattern ? (
                        "Fix the pattern first."
                      ) : testVersion ? (
                        <span className="flex items-center gap-1 text-success">
                          <Check className="size-3.5" />
                          Publishes v{testVersion}
                        </span>
                      ) : (
                        "No match, this commit would not publish."
                      )}
                    </Description>
                  </TextField>
                  <div className="md:col-span-2">
                    <SwitchRow
                      label="GitHub release"
                      description="Also create a GitHub release with the VPK files. Needs gh logged in."
                      isSelected={draft.autoPublish.githubRelease}
                      onChange={(value) => setAuto("githubRelease", value)}
                    />
                  </div>
                </div>
              </motion.div>
            )}
          </AnimatePresence>
        </div>
      </Panel>

      {saveError !== null && <ErrorNotice title="Could not save workshop.json" error={saveError} />}

      <AnimatePresence>
        {(dirty || saved) && (
          <motion.div
            initial={{ opacity: 0, y: 12 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: 12 }}
            transition={{ duration: 0.2 }}
            className="sticky bottom-4 z-10 flex items-center gap-3 rounded-lg bg-surface px-4 py-3 shadow-[0_8px_24px_rgba(0,0,0,0.25)]"
          >
            {saved && !dirty ? (
              <span className="flex items-center gap-1.5 text-sm text-success">
                <Check className="size-4" />
                Saved
              </span>
            ) : (
              <>
                <span className="text-sm">{invalid ? "Fix the marked fields to save." : "Unsaved changes"}</span>
                <div className="ml-auto flex items-center gap-2">
                  <Button variant="tertiary" isDisabled={saving} onPress={() => setDraft(structuredClone(base))}>
                    <RotateCcw className="size-4" />
                    Reset
                  </Button>
                  <Button variant="primary" isDisabled={invalid} isPending={saving} onPress={() => void save()}>
                    <Save className="size-4" />
                    Save
                  </Button>
                </div>
              </>
            )}
          </motion.div>
        )}
      </AnimatePresence>
    </ViewPage>
  );
}

/* ---------- small form parts ---------- */

function Field({ label, error, description, children }: { label: string; error?: string; description?: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5">
      <span className="text-sm font-medium">{label}</span>
      {children}
      {error ? <span className="text-xs text-danger">{error}</span> : description ? <span className="text-xs text-muted">{description}</span> : null}
    </div>
  );
}

function SwitchRow({ label, description, isSelected, onChange }: { label: string; description: string; isSelected: boolean; onChange: (value: boolean) => void }) {
  return (
    <Switch isSelected={isSelected} onChange={onChange}>
      <Switch.Content className="font-normal">
        <Switch.Control>
          <Switch.Thumb />
        </Switch.Control>
        {label}
      </Switch.Content>
      <Description className="text-xs">{description}</Description>
    </Switch>
  );
}

function useAdder(values: string[], onChange: (values: string[]) => void) {
  const [text, setText] = useState("");
  const add = () => {
    const parts = text
      .split(",")
      .map((part) => part.trim())
      .filter((part) => part !== "" && !values.includes(part));
    if (parts.length > 0) onChange([...values, ...parts]);
    setText("");
  };
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "Enter") {
      event.preventDefault();
      add();
    } else if (event.key === "Backspace" && text === "" && values.length > 0) {
      onChange(values.slice(0, -1));
    }
  };
  return { text, setText, add, onKeyDown };
}

function ChipEditor({ values, onChange, placeholder, ariaLabel }: { values: string[]; onChange: (values: string[]) => void; placeholder: string; ariaLabel: string }) {
  const adder = useAdder(values, onChange);
  return (
    <div className="flex flex-wrap items-center gap-1.5 rounded-sm bg-default/60 p-1.5">
      <AnimatePresence initial={false}>
        {values.map((value) => (
          <motion.span
            key={value}
            layout="position"
            initial={{ opacity: 0, scale: 0.9 }}
            animate={{ opacity: 1, scale: 1 }}
            exit={{ opacity: 0, scale: 0.9 }}
            transition={{ duration: 0.15 }}
            className="flex items-center gap-1 rounded-sm bg-surface py-0.5 pr-0.5 pl-2 text-xs shadow-[0_1px_2px_rgba(0,0,0,0.1)]"
          >
            {value}
            <button
              type="button"
              aria-label={`Remove ${value}`}
              onClick={() => onChange(values.filter((entry) => entry !== value))}
              className="cursor-pointer rounded-sm p-0.5 text-muted transition-colors duration-200 hover:bg-default hover:text-foreground"
            >
              <X className="size-3" />
            </button>
          </motion.span>
        ))}
      </AnimatePresence>
      <input
        aria-label={ariaLabel}
        value={adder.text}
        onChange={(event) => adder.setText(event.target.value)}
        onKeyDown={adder.onKeyDown}
        onBlur={adder.add}
        placeholder={values.length === 0 ? placeholder : ""}
        className="min-w-24 flex-1 bg-transparent px-1 py-0.5 text-sm outline-none placeholder:text-muted"
      />
    </div>
  );
}

function ListEditor({ values, onChange, placeholder, ariaLabel }: { values: string[]; onChange: (values: string[]) => void; placeholder: string; ariaLabel: string }) {
  const adder = useAdder(values, onChange);
  return (
    <div className="flex flex-col gap-1 rounded-sm bg-default/60 p-1.5">
      <AnimatePresence initial={false}>
        {values.map((value, index) => (
          <motion.div
            key={value}
            layout="position"
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: "auto" }}
            exit={{ opacity: 0, height: 0 }}
            transition={{ duration: 0.15 }}
            className="overflow-hidden"
          >
            <div className="group flex items-center gap-2 rounded-sm bg-surface py-1 pr-1 pl-2.5">
              <code className="min-w-0 flex-1 font-mono text-xs wrap-anywhere">{value}</code>
              <button
                type="button"
                aria-label={`Remove ${value}`}
                onClick={() => onChange(values.filter((_, at) => at !== index))}
                className="cursor-pointer rounded-sm p-1 text-muted transition-colors duration-200 hover:bg-default hover:text-foreground"
              >
                <X className="size-3.5" />
              </button>
            </div>
          </motion.div>
        ))}
      </AnimatePresence>
      <div className="flex items-center gap-1">
        <input
          aria-label={ariaLabel}
          value={adder.text}
          onChange={(event) => adder.setText(event.target.value)}
          onKeyDown={adder.onKeyDown}
          placeholder={placeholder}
          className={cn("min-w-0 flex-1 bg-transparent px-2 py-1 font-mono text-xs outline-none placeholder:text-muted")}
        />
        <Button size="sm" variant="ghost" isIconOnly aria-label="Add" className="size-7 min-w-7" isDisabled={adder.text.trim() === ""} onPress={adder.add}>
          <Plus className="size-3.5" />
        </Button>
      </div>
    </div>
  );
}
