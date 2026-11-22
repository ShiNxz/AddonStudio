// Doctor: every environment check with a status chip, what was found, and how to fix it (with a copy button).
import { useQueryClient } from "@tanstack/react-query";
import { Button, Chip, Skeleton } from "@heroui/react";
import { CircleAlert, CircleCheck, CircleX, RefreshCw } from "lucide-react";
import type { CheckStatus, DoctorCheck } from "../../rpc/types";
import { CodeText } from "../../components/CodeText";
import { CopyButton } from "../../components/CopyButton";
import { InfoBar } from "../../components/InfoBar";
import { describeError } from "../../components/notify";
import { keys, useDoctor } from "../../app/queries";
import { splitFix } from "./fix";

const STATUS: Record<CheckStatus, { label: string; color: "success" | "warning" | "danger"; Icon: typeof CircleCheck; tone: string }> = {
  ok: { label: "OK", color: "success", Icon: CircleCheck, tone: "text-success" },
  warn: { label: "Warning", color: "warning", Icon: CircleAlert, tone: "text-warning" },
  error: { label: "Problem", color: "danger", Icon: CircleX, tone: "text-danger" },
};

export function DoctorView() {
  const queryClient = useQueryClient();
  const doctor = useDoctor();
  const report = doctor.data;
  const problems = report?.checks.filter((check) => check.status !== "ok").length ?? 0;

  return (
    <div className="flex-1 overflow-y-auto">
      <div className="mx-auto flex max-w-3xl flex-col gap-5 px-8 py-8">
        <div className="flex items-end justify-between gap-4">
          <div className="flex flex-col gap-1">
            <h1 className="text-title font-semibold">Doctor</h1>
            <p className="text-body text-fg-secondary">
              {report
                ? problems === 0
                  ? "Everything Addon Studio needs is in place."
                  : `${problems} ${problems === 1 ? "thing needs" : "things need"} attention.`
                : "Checking this PC..."}
            </p>
          </div>
          <Button
            variant="secondary"
            isPending={doctor.isFetching}
            onPress={() => void queryClient.invalidateQueries({ queryKey: keys.doctor })}
          >
            {doctor.isFetching ? null : <RefreshCw />}
            Check again
          </Button>
        </div>

        {report && (
          <div className="grid grid-cols-2 gap-1">
            <Capability ok={report.canCompile} label="Compile" yes="This PC can compile addons." no="Compiling is not possible yet." />
            <Capability ok={report.canPublish} label="Publish" yes="This PC can publish to the Workshop." no="Publishing is not possible yet." />
          </div>
        )}

        {doctor.isError && (
          <InfoBar severity="error" title="The checks could not run.">
            {describeError(doctor.error).message}
          </InfoBar>
        )}

        {report?.cs2Path && (
          <div className="flex flex-col gap-1.5">
            <span className="text-caption text-muted">CS2 install</span>
            <CodeText value={report.cs2Path} copyLabel="Copy path" />
          </div>
        )}

        <div className="flex flex-col gap-1">
          {doctor.isPending
            ? Array.from({ length: 8 }, (_, index) => <CheckSkeleton key={index} />)
            : report?.checks.map((check) => <CheckRow key={check.id} check={check} />)}
        </div>
      </div>
    </div>
  );
}

function Capability({ ok, label, yes, no }: { ok: boolean; label: string; yes: string; no: string }) {
  return (
    <div className="flex items-center gap-3 rounded-lg bg-surface px-4 py-3 shadow-card">
      {ok ? <CircleCheck className="size-5 shrink-0 text-success" /> : <CircleX className="size-5 shrink-0 text-danger" />}
      <div className="flex min-w-0 flex-col">
        <span className="text-body font-semibold">{label}</span>
        <span className="text-caption text-muted">{ok ? yes : no}</span>
      </div>
    </div>
  );
}

function CheckRow({ check }: { check: DoctorCheck }) {
  const status = STATUS[check.status];
  const fix = check.fix && check.status !== "ok" ? splitFix(check.fix) : null;
  return (
    <div className="flex gap-3 rounded-lg bg-surface px-4 py-3 shadow-card">
      <status.Icon className={`mt-0.5 size-[18px] shrink-0 ${status.tone}`} />
      <div className="flex min-w-0 flex-1 flex-col gap-1.5">
        <div className="flex items-center justify-between gap-3">
          <span className="text-body font-semibold">{check.label}</span>
          <Chip size="sm" variant="soft" color={status.color}>
            {status.label}
          </Chip>
        </div>
        {check.detail && <p className="text-caption break-words text-fg-secondary selectable">{check.detail}</p>}
        {fix && (
          <div className="flex flex-col gap-1.5 pt-0.5">
            {fix.command ? (
              <>
                {fix.text && <p className="text-caption text-muted selectable">{fix.text}</p>}
                <CodeText value={fix.command} copyLabel="Copy command" />
              </>
            ) : (
              <div className="flex items-start gap-1 rounded-sm bg-surface-secondary py-1 pr-1 pl-2.5">
                <p className="min-w-0 flex-1 py-[3px] text-caption text-fg-secondary selectable">{fix.text}</p>
                <CopyButton value={fix.text ?? ""} label="Copy fix" />
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function CheckSkeleton() {
  return (
    <div className="flex gap-3 rounded-lg bg-surface px-4 py-3 shadow-card" aria-hidden>
      <Skeleton className="size-[18px] shrink-0 rounded-full" />
      <div className="flex flex-1 flex-col gap-2">
        <div className="flex justify-between">
          <Skeleton className="h-3.5 w-32" />
          <Skeleton className="h-5 w-14" />
        </div>
        <Skeleton className="h-3 w-3/4" />
      </div>
    </div>
  );
}
