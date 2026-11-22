// A folder path field: type it, or pick it with the native dialog in the desktop app.
import { useState } from "react";
import { Button, Input, Label, TextField } from "@heroui/react";
import { FolderOpen } from "lucide-react";
import { canPickFolders, parentPath, pickFolder } from "../../app/platform";
import { useProjects } from "../../app/queries";

const PARENT_KEY = "addonstudio.parentFolder";

/** The folder new projects go into: the last one used, else the folder of the first recent project. */
export function useParentFolder(): [string, (value: string) => void] {
  const projects = useProjects();
  const [value, setValue] = useState<string | null>(() => {
    try {
      return localStorage.getItem(PARENT_KEY);
    } catch {
      return null;
    }
  });
  const fallback = projects.data?.[0] ? parentPath(projects.data[0].path) : "";
  const set = (next: string) => {
    setValue(next);
    try {
      localStorage.setItem(PARENT_KEY, next);
    } catch {
      // a convenience only
    }
  };
  return [value ?? fallback, set];
}

interface FolderFieldProps {
  label: string;
  value: string;
  onChange: (value: string) => void;
  description?: string;
  pickerTitle?: string;
  isDisabled?: boolean;
}

export function FolderField({ label, value, onChange, description, pickerTitle, isDisabled }: FolderFieldProps) {
  const browse = async () => {
    const picked = await pickFolder(pickerTitle ?? label, value);
    if (picked) onChange(picked);
  };

  return (
    <div className="flex flex-col gap-1">
      <div className="flex items-end gap-2">
        <TextField className="min-w-0 flex-1" value={value} onChange={onChange} isDisabled={isDisabled}>
          <Label>{label}</Label>
          <Input placeholder="C:\Users\you\Addons" className="w-full font-mono text-[13px]" />
        </TextField>
        {canPickFolders() && (
          <Button variant="secondary" isDisabled={isDisabled} onPress={() => void browse()}>
            <FolderOpen />
            Browse
          </Button>
        )}
      </div>
      {description && <p className="text-caption break-all text-muted">{description}</p>}
    </div>
  );
}
