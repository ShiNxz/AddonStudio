// Run: node --import ./node_modules/jiti/lib/jiti-register.mjs --test src/app/tests/*.test.ts
import { test } from "node:test";
import assert from "node:assert/strict";
import { createTauriTransport, type CoreHandlers, type CoreProcess } from "../../rpc/transport-tauri";
import { createMockTransport } from "../../rpc/transport-mock";
import { delay } from "../../rpc/mock/types";

/** A fake sidecar: records stdin, lets the test push stdout chunks and kill it. */
function fakeCore() {
  const spawned: { handlers: CoreHandlers; written: string[] }[] = [];
  const spawn = async (handlers: CoreHandlers): Promise<CoreProcess> => {
    const entry = { handlers, written: [] as string[] };
    spawned.push(entry);
    return {
      write: async (line) => {
        entry.written.push(line);
      },
      kill: async () => handlers.exit("killed"),
    };
  };
  return { spawn, spawned };
}

test("tauri transport joins stdout lines split across chunks and sends newline-terminated requests", async () => {
  const core = fakeCore();
  const transport = await createTauriTransport({ spawn: core.spawn, log: () => undefined });
  const lines: string[] = [];
  transport.onLine((line) => lines.push(line));

  await transport.send(JSON.stringify({ id: 1, method: "app.info" }));
  assert.deepEqual(core.spawned[0]!.written, ['{"id":1,"method":"app.info"}\n']);

  const out = core.spawned[0]!.handlers.stdout;
  out('{"event":"build.log","data":{"requestId":1,');
  out('"line":"ok"}}\n{"id":1,"res');
  out('ult":{"version":"0.1.0"}}\r\n');
  assert.deepEqual(lines, ['{"event":"build.log","data":{"requestId":1,"line":"ok"}}', '{"id":1,"result":{"version":"0.1.0"}}']);
});

test("tauri transport fails in-flight requests and restarts once, then closes on the second exit", async () => {
  const core = fakeCore();
  const transport = await createTauriTransport({ spawn: core.spawn, log: () => undefined });
  const lines: string[] = [];
  const closes: string[] = [];
  transport.onLine((line) => lines.push(line));
  transport.onClose((reason) => closes.push(reason));

  await transport.send(JSON.stringify({ id: 5, method: "build.run", params: { path: "C:\\a" } }));
  await transport.send(JSON.stringify({ id: 6, method: "git.status", params: { path: "C:\\a" } }));
  core.spawned[0]!.handlers.stdout('{"id":6,"result":{}}\n');
  core.spawned[0]!.handlers.exit("exit code 3221225477");

  // id 6 was answered; only 5 gets the synthetic error
  const failed = lines.slice(1).map((line) => JSON.parse(line) as { id: number; error: { code: string; message: string } });
  assert.equal(failed.length, 1);
  assert.equal(failed[0]!.id, 5);
  assert.equal(failed[0]!.error.code, "internal");
  assert.match(failed[0]!.error.message, /restarted.*build\.run/);
  assert.equal(core.spawned.length, 2, "restarted once");
  assert.deepEqual(closes, []);

  // the new process takes the next request
  await transport.send(JSON.stringify({ id: 7, method: "app.info" }));
  assert.equal(core.spawned[1]!.written.length, 1);
  // late output from the dead process is ignored
  core.spawned[0]!.handlers.stdout('{"id":99,"result":{}}\n');
  assert.ok(!lines.some((line) => line.includes('"id":99')));

  core.spawned[1]!.handlers.exit("exit code 1");
  assert.equal(core.spawned.length, 2, "no second restart");
  assert.equal(closes.length, 1);
  assert.match(closes[0]!, /stopped again/);
  await assert.rejects(transport.send(JSON.stringify({ id: 8, method: "app.info" })), /not running/);
});

test("mock transport routes request events with requestId and cancels a running request", async () => {
  const transport = createMockTransport({
    latency: 0,
    handlers: {
      "test.long": async (_params, ctx) => {
        ctx.emit("build.log", { source: "a.xml", line: "compiling" });
        await delay(5_000, ctx.signal);
        return { done: true };
      },
    },
  });
  const messages: Record<string, unknown>[] = [];
  transport.onLine((line) => messages.push(JSON.parse(line) as Record<string, unknown>));

  await transport.send(JSON.stringify({ id: 1, method: "test.long" }));
  await new Promise((resolve) => setTimeout(resolve, 20));
  await transport.send(JSON.stringify({ id: 2, method: "rpc.cancel", params: { requestId: 1 } }));
  await new Promise((resolve) => setTimeout(resolve, 20));

  const event = messages.find((message) => message.event === "build.log") as { data: { requestId: number; line: string } } | undefined;
  assert.equal(event?.data.requestId, 1);
  assert.equal(event?.data.line, "compiling");
  assert.deepEqual(messages.find((message) => message.id === 2), { id: 2, result: { cancelled: true } });
  const answer = messages.find((message) => message.id === 1) as { error?: { code: string } } | undefined;
  assert.equal(answer?.error?.code, "cancelled");
});
