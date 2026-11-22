// Browser dev transport: answers requests from the demo handlers in mock/shell.ts and mock/project.ts, so
// `npm run dev` works without Tauri. It speaks the same JSON lines as the core: responses by id, request events
// with requestId, global events without, and rpc.cancel aborts the handler's AbortSignal.
import { createListeners, type Transport } from "./transport";
import type { RpcErrorBody, RpcRequestMessage } from "./types";
import { MockError, type MockContext, type MockHandler } from "./mock/types";
import { handlers as shellHandlers } from "./mock/shell";
import { handlers as projectHandlers } from "./mock/project";

/** Quick reads answer fast, opening a project or scanning folders takes a little longer, so loading states show. */
export function mockLatency(method: string, random: () => number = Math.random): number {
  const [min, max] =
    method === "app.info" || method === "settings.get" || method === "settings.set" || method === "watch.set"
      ? [30, 80]
      : method === "projects.open" || method === "projects.cs2Addons" || method === "env.doctor" || method.endsWith(".plan")
        ? [180, 420]
        : [80, 220];
  return Math.round(min + random() * (max - min));
}

export interface MockTransportOptions {
  /** Extra or replacement handlers (tests). */
  handlers?: Record<string, MockHandler>;
  /** Fixed latency in ms instead of the realistic one (tests use 0). */
  latency?: number;
}

export function createMockTransport(options: MockTransportOptions = {}): Transport {
  const handlers: Record<string, MockHandler> = { ...shellHandlers, ...projectHandlers, ...options.handlers };
  const lines = createListeners<[string]>();
  const closes = createListeners<[string]>();
  const running = new Map<number, AbortController>();
  let closed = false;

  const write = (message: unknown) => {
    const line = JSON.stringify(message);
    // async like a pipe, never inside send(); setTimeout keeps the order of writes
    setTimeout(() => {
      if (!closed) lines.emit(line);
    }, 0);
  };

  const emitEvent = (event: string, data: unknown, requestId?: number) => {
    const payload =
      requestId === undefined
        ? data
        : { requestId, ...(data !== null && typeof data === "object" ? (data as Record<string, unknown>) : { value: data }) };
    write({ event, data: payload });
  };

  const toError = (error: unknown): RpcErrorBody => {
    if (error instanceof MockError) return { code: error.code, message: error.message, details: error.details ?? null };
    return { code: "internal", message: error instanceof Error ? error.message : String(error), details: null };
  };

  const handle = async (request: RpcRequestMessage) => {
    const { id, method, params } = request;

    if (method === "rpc.cancel") {
      const target = (params as { requestId?: number } | undefined)?.requestId;
      const controller = target === undefined ? undefined : running.get(target);
      controller?.abort();
      write({ id, result: { cancelled: controller !== undefined } });
      return;
    }

    const handler = handlers[method];
    if (!handler) {
      write({ id, error: { code: "invalid_params", message: `Unknown method "${method}".`, details: null } });
      return;
    }

    const controller = new AbortController();
    running.set(id, controller);
    let open = true;

    const ctx: MockContext = {
      requestId: id,
      signal: controller.signal,
      emit: (event, data) => {
        if (open) emitEvent(event, data, id);
      },
      emitGlobal: (event, data) => emitEvent(event, data),
    };

    try {
      const wait = options.latency ?? mockLatency(method);
      if (wait > 0) await new Promise((resolve) => setTimeout(resolve, wait));
      if (controller.signal.aborted) throw new MockError("cancelled", "Cancelled.");
      const result = await handler(params, ctx);
      open = false;
      write(controller.signal.aborted ? { id, error: { code: "cancelled", message: "Cancelled.", details: null } } : { id, result: result ?? null });
    } catch (error) {
      open = false;
      write({ id, error: controller.signal.aborted ? { code: "cancelled", message: "Cancelled.", details: null } : toError(error) });
    } finally {
      running.delete(id);
    }
  };

  return {
    kind: "mock",
    async send(line: string) {
      if (closed) throw new Error("The mock core is closed.");
      let request: RpcRequestMessage;
      try {
        request = JSON.parse(line) as RpcRequestMessage;
      } catch {
        console.warn("mock core: ignored a line that is not JSON", line);
        return;
      }
      void handle(request);
    },
    onLine: lines.add,
    onClose: closes.add,
    async close() {
      if (closed) return;
      for (const controller of running.values()) controller.abort();
      closed = true;
      closes.emit("closed");
    },
  };
}
