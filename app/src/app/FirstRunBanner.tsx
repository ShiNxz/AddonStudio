// Shown while this PC can not compile (no Windows, no CS2, no Workshop Tools). Points at the Doctor.
import { useState } from "react";
import { Button } from "@heroui/react";
import { InfoBar } from "../components/InfoBar";
import { useAppInfo, useDoctor } from "./queries";

const DISMISS_KEY = "addonstudio.firstRunDismissed";

function wasDismissed(): boolean {
  try {
    return sessionStorage.getItem(DISMISS_KEY) === "1";
  } catch {
    return false;
  }
}

export function FirstRunBanner({ onOpenDoctor }: { onOpenDoctor: () => void }) {
  const app = useAppInfo();
  const doctor = useDoctor();
  const [dismissed, setDismissed] = useState(wasDismissed);

  if (dismissed || !app.data || app.data.canCompile) return null;

  const firstProblem = doctor.data?.checks.find((check) => check.status === "error");
  const reason =
    app.data.os !== "windows"
      ? "Compiling and publishing need Windows with CS2 and its Workshop Tools. You can still edit, commit and look around."
      : firstProblem
        ? `${firstProblem.label}: ${firstProblem.detail}`
        : "CS2 or its Workshop Tools are missing.";

  return (
    <div className="shrink-0 px-6 pt-4">
      <InfoBar
        severity="warning"
        title="This PC can not compile addons yet."
        actions={
          <Button size="sm" variant="secondary" onPress={onOpenDoctor}>
            Open Doctor
          </Button>
        }
        onClose={() => {
          setDismissed(true);
          try {
            sessionStorage.setItem(DISMISS_KEY, "1");
          } catch {
            // session only
          }
        }}
      >
        {reason}
      </InfoBar>
    </div>
  );
}
