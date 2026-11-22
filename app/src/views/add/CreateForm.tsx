// Create a new addon project: title, addon folder name, where the repo goes.
import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { Button, Description, FieldError, Input, Label, Modal, TextField } from "@heroui/react";
import { call } from "../../rpc/client";
import { joinPath } from "../../app/platform";
import { FolderField, useParentFolder } from "./FolderField";
import { addonFromTitle, folderFromAddon, isValidAddon } from "./naming";
import type { AddFlowProps } from "./AddProjectModal";

export function CreateForm({ onDone, onCancel, setBusy }: AddFlowProps) {
  const [title, setTitle] = useState("");
  const [addon, setAddon] = useState("");
  const [addonEdited, setAddonEdited] = useState(false);
  const [parent, setParent] = useParentFolder();

  const effectiveAddon = addonEdited ? addon : addonFromTitle(title);
  const path = parent.trim() && effectiveAddon ? joinPath(parent.trim(), folderFromAddon(effectiveAddon)) : "";
  const addonInvalid = effectiveAddon.length > 0 && !isValidAddon(effectiveAddon);
  const canCreate = title.trim().length > 0 && isValidAddon(effectiveAddon) && parent.trim().length > 0;

  const create = useMutation({
    mutationFn: () => call("projects.create", { path, addon: effectiveAddon, title: title.trim() }),
    onMutate: () => setBusy(true),
    onSettled: () => setBusy(false),
    onSuccess: (project) => {
      setParent(parent.trim());
      onDone(project);
    },
  });

  return (
    <form
      className="contents"
      onSubmit={(event) => {
        event.preventDefault();
        if (canCreate && !create.isPending) create.mutate();
      }}
    >
      <Modal.Body className="mt-5 flex flex-col gap-4">
        <TextField value={title} onChange={setTitle} autoFocus isRequired>
          <Label>Title</Label>
          <Input placeholder="Next HUD" className="w-full" />
          <Description>Shown on the Workshop page. You can change it later.</Description>
        </TextField>

        <TextField
          value={effectiveAddon}
          onChange={(value) => {
            setAddonEdited(true);
            setAddon(value);
          }}
          isInvalid={addonInvalid}
        >
          <Label>Addon name</Label>
          <Input placeholder="next_hud" className="w-full font-mono text-[13px]" />
          {addonInvalid ? (
            <FieldError>Lowercase letters, digits and _ only, up to 64.</FieldError>
          ) : (
            <Description>The folder name under csgo_addons. Lowercase letters, digits and _.</Description>
          )}
        </TextField>

        <FolderField
          label="Put the repo in"
          value={parent}
          onChange={setParent}
          pickerTitle="Where the new project folder goes"
          description={path ? `Creates ${path}` : "Pick the folder that will hold the project folder."}
        />
      </Modal.Body>
      <Modal.Footer className="mt-6">
        <Button variant="secondary" onPress={onCancel} isDisabled={create.isPending}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" isDisabled={!canCreate} isPending={create.isPending}>
          Create project
        </Button>
      </Modal.Footer>
    </form>
  );
}
